/*
 * Copyright (c) 2026 Noah Dolph
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
 *
 * See the GNU Affero General Public License for more details.
 */
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EtheirysProximityVoiceChat.Log;

namespace EtheirysProximityVoiceChat.Premium;

/// <summary>
/// Drives the Discord-OAuth linking flow from the plugin side.
///
/// Flow:
///   1. POST /api/auth/start → receive { session_id, user_code, verification_uri }
///   2. Open verification_uri in the user's default browser. The server's
///      /link route redirects to Discord OAuth with state=user_code.
///   3. Poll /api/auth/poll?session_id=... every few seconds until the
///      response is "ok", "denied", or "expired".
///   4. On "ok", persist the returned token + premium status into
///      Configuration and flip IsLocalPremium.
///
/// The flow is cancellable via <see cref="CancelFlow"/> and exposes a small
/// state machine the UI polls each frame (no events / Subjects needed).
/// </summary>
public sealed class PremiumLinker : IDisposable
{
    public enum LinkState
    {
        Idle,
        Starting,
        AwaitingUserInBrowser,
        Polling,
        Complete,
        Failed,
    }

    // Server limits the session to 5 minutes. We pad slightly so a clock-skew
    // doesn't make us bail one tick before the server would. The real expiry
    // comes back in the /start response and is used in place of this when present.
    private const int DefaultExpiresSeconds = 300;
    private const int PollIntervalSeconds = 3;

    private readonly Configuration configuration;
    private readonly ILogger logger;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private CancellationTokenSource? activeFlow;

    public LinkState State { get; private set; } = LinkState.Idle;
    public string StatusMessage { get; private set; } = string.Empty;
    public string UserCode { get; private set; } = string.Empty;
    public string VerificationUri { get; private set; } = string.Empty;
    public bool LastResultPremium { get; private set; }

    public PremiumLinker(Configuration configuration, ILogger logger)
    {
        this.configuration = configuration;
        this.logger = logger;
    }

    public bool InProgress =>
        this.State is LinkState.Starting or LinkState.AwaitingUserInBrowser or LinkState.Polling;

    /// <summary>
    /// Kick off a new link flow. Safe to call from the UI thread — all network
    /// work runs on a background task. If a flow is already in progress this
    /// is a no-op.
    /// </summary>
    public void StartFlow()
    {
        if (this.InProgress) return;
        this.activeFlow?.Cancel();
        this.activeFlow?.Dispose();
        this.activeFlow = new CancellationTokenSource();
        // Reset display state before any UI frame can read it.
        this.State = LinkState.Starting;
        this.StatusMessage = "Requesting session…";
        this.UserCode = string.Empty;
        this.VerificationUri = string.Empty;
        this.LastResultPremium = false;
        var ct = this.activeFlow.Token;
        _ = Task.Run(() => RunFlowAsync(ct), ct);
    }

    public void CancelFlow()
    {
        try { this.activeFlow?.Cancel(); } catch { /* nothing to do */ }
        if (this.InProgress)
        {
            this.State = LinkState.Idle;
            this.StatusMessage = "Cancelled.";
        }
    }

    /// <summary>
    /// Clear the local premium token and best-effort POST to the server to
    /// invalidate it remotely. Always clears local state, even if the server
    /// is unreachable.
    /// </summary>
    public async Task SignOutAsync()
    {
        var token = this.configuration.PremiumToken;
        this.configuration.PremiumToken = string.Empty;
        this.configuration.PremiumTokenExpiresAtUtcMs = 0;
        this.configuration.PremiumLinkedDiscordUsername = string.Empty;
        this.configuration.IsLocalPremium = false;
        this.configuration.Save();
        this.State = LinkState.Idle;
        this.StatusMessage = string.Empty;
        if (string.IsNullOrEmpty(token)) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BuildUrl("/api/auth/unlink"))
            {
                Content = JsonContent.Create(new { token }),
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", EmbeddedConfig.SignalingServerToken);
            using var resp = await this.http.SendAsync(req).ConfigureAwait(false);
            // Best-effort. Any failure here is fine — local clear already happened.
        }
        catch (Exception ex)
        {
            this.logger.Debug("Sign-out POST failed (ignored): {0}", ex.Message);
        }
    }

    public void Dispose()
    {
        try { this.activeFlow?.Cancel(); } catch { /* nothing to do */ }
        this.activeFlow?.Dispose();
        this.activeFlow = null;
        this.http.Dispose();
    }

    private async Task RunFlowAsync(CancellationToken ct)
    {
        try
        {
            // 1. /api/auth/start
            string sessionId, userCode, verificationUri;
            int expiresIn = DefaultExpiresSeconds;
            try
            {
                using var startBody = new StringContent("{}", Encoding.UTF8, "application/json");
                using var startResp = await this.http.PostAsync(BuildUrl("/api/auth/start"), startBody, ct).ConfigureAwait(false);
                if (!startResp.IsSuccessStatusCode)
                {
                    Fail($"Server rejected link request ({(int)startResp.StatusCode}).");
                    return;
                }
                await using var stream = await startResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
                sessionId = doc.RootElement.TryGetProperty("session_id", out var sid) ? sid.GetString() ?? string.Empty : string.Empty;
                userCode = doc.RootElement.TryGetProperty("user_code", out var uc) ? uc.GetString() ?? string.Empty : string.Empty;
                verificationUri = doc.RootElement.TryGetProperty("verification_uri", out var vu) ? vu.GetString() ?? string.Empty : string.Empty;
                if (doc.RootElement.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number)
                {
                    expiresIn = Math.Max(60, ei.GetInt32());
                }
                if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(verificationUri))
                {
                    Fail("Server returned an incomplete session.");
                    return;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Fail($"Could not contact the signaling server: {ex.Message}");
                return;
            }

            this.UserCode = userCode;
            this.VerificationUri = verificationUri;
            this.State = LinkState.AwaitingUserInBrowser;
            this.StatusMessage = "A browser tab is opening. Log in with Discord to continue.";

            try
            {
                Process.Start(new ProcessStartInfo(verificationUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                this.StatusMessage = "Open this URL manually: " + verificationUri;
                this.logger.Debug("Browser launch failed: {0}", ex.Message);
            }

            // 2. Poll /api/auth/poll
            this.State = LinkState.Polling;
            var deadline = DateTime.UtcNow.AddSeconds(expiresIn);
            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                string status = "pending";
                string? token = null;
                long expiresAt = 0;
                bool premium = false;
                try
                {
                    using var pollResp = await this.http.GetAsync(BuildUrl($"/api/auth/poll?session_id={Uri.EscapeDataString(sessionId)}"), ct).ConfigureAwait(false);
                    if (!pollResp.IsSuccessStatusCode)
                    {
                        // Transient — keep polling until deadline.
                        continue;
                    }
                    await using var stream = await pollResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
                    if (doc.RootElement.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String)
                    {
                        status = s.GetString() ?? "pending";
                    }
                    if (status == "ok")
                    {
                        token = doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
                        if (doc.RootElement.TryGetProperty("expires_at", out var ea) && ea.ValueKind == JsonValueKind.Number)
                        {
                            expiresAt = ea.GetInt64();
                        }
                        if (doc.RootElement.TryGetProperty("premium", out var pe))
                        {
                            premium = pe.ValueKind == JsonValueKind.True;
                        }
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    this.logger.Debug("Poll error (will retry): {0}", ex.Message);
                    continue;
                }

                switch (status)
                {
                    case "pending":
                        continue;
                    case "denied":
                        Fail("Discord linking was denied.");
                        return;
                    case "expired":
                        Fail("Linking session expired. Try again from the plugin.");
                        return;
                    case "ok":
                        if (string.IsNullOrEmpty(token))
                        {
                            Fail("Server returned a malformed token.");
                            return;
                        }
                        this.configuration.PremiumToken = token;
                        this.configuration.PremiumTokenExpiresAtUtcMs = expiresAt;
                        this.configuration.IsLocalPremium = premium;
                        this.configuration.Save();
                        this.State = LinkState.Complete;
                        this.LastResultPremium = premium;
                        this.StatusMessage = premium
                            ? "Linked. Premium features unlocked — reconnect to a room for changes to take effect."
                            : "Linked, but the supporter role was not detected on your Discord account.";
                        return;
                }
            }

            if (!ct.IsCancellationRequested)
            {
                Fail("Linking timed out.");
            }
        }
        catch (OperationCanceledException) { /* normal cancel */ }
        catch (Exception ex)
        {
            this.logger.Error("PremiumLinker.RunFlowAsync threw: {0}", ex);
            Fail("Unexpected error during linking.");
        }
    }

    private void Fail(string message)
    {
        this.State = LinkState.Failed;
        this.StatusMessage = message;
    }

    private static string BuildUrl(string path)
    {
        var baseUrl = (EmbeddedConfig.SignalingServerUrl ?? string.Empty).TrimEnd('/');
        return baseUrl + path;
    }
}
