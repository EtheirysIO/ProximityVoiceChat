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
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using EtheirysProximityVoiceChat.Log;

namespace EtheirysProximityVoiceChat.WebRTC;

/// <summary>
/// Pulls the v4 server's browse list of listed private rooms (the rooms whose
/// owners did NOT tick "Unlisted"). The plugin presents this as the inline
/// catalogue in Private mode so users can pick an existing room with one
/// click instead of typing the exact name.
///
/// Wire shape — GET <c>/api/rooms/listed</c> with Bearer auth, returns
/// <code>{ ok: true, rooms: [{displayName, world, hasPassword, peerCount}, …] }</code>.
/// The endpoint contains NO peerIds or names of who's in the rooms — only the
/// four fields above. See server.js comments on the route handler for the
/// rationale.
/// </summary>
public sealed class PrivateRoomCatalog : IDisposable
{
    /// <summary>One row in the browse list.</summary>
    public readonly struct RoomEntry
    {
        public string DisplayName { get; init; }
        public string World { get; init; }
        public bool HasPassword { get; init; }
        public int PeerCount { get; init; }
    }

    /// <summary>
    /// Coarse status driving the inline UI. The View can render a spinner /
    /// retry button / "no rooms" placeholder off this enum without owning
    /// any cancellation tokens of its own.
    /// </summary>
    public enum CatalogStatus
    {
        Idle,
        Loading,
        Loaded,
        Failed,
    }

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly ILogger logger;
    private CancellationTokenSource? activeFetch;

    public CatalogStatus Status { get; private set; } = CatalogStatus.Idle;
    public IReadOnlyList<RoomEntry> Rooms { get; private set; } = Array.Empty<RoomEntry>();
    public string LastErrorMessage { get; private set; } = string.Empty;
    public DateTimeOffset LastFetchedAtUtc { get; private set; } = DateTimeOffset.MinValue;

    public PrivateRoomCatalog(ILogger logger)
    {
        this.logger = logger;
    }

    /// <summary>
    /// Kick off a fresh fetch. If one is already in flight it's cancelled so
    /// the Refresh button feels snappy when mashed. Result lands in
    /// <see cref="Rooms"/> / <see cref="Status"/> for the View to poll.
    /// </summary>
    public async Task RefreshAsync()
    {
        // Cancel any prior in-flight fetch so we don't race two requests'
        // results into Rooms.
        try { this.activeFetch?.Cancel(); } catch { /* swallow */ }
        this.activeFetch?.Dispose();
        var cts = new CancellationTokenSource();
        this.activeFetch = cts;

        this.Status = CatalogStatus.Loading;
        this.LastErrorMessage = string.Empty;

        try
        {
            var baseUrl = (EmbeddedConfig.SignalingServerUrl ?? string.Empty).TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                this.Status = CatalogStatus.Failed;
                this.LastErrorMessage = "Signaling server URL is not configured.";
                return;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/rooms/listed");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", EmbeddedConfig.SignalingServerToken ?? string.Empty);
            req.Headers.Accept.ParseAdd("application/json");

            using var resp = await this.http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                this.Status = CatalogStatus.Failed;
                this.LastErrorMessage = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
                this.logger.Debug("PrivateRoomCatalog refresh failed: {0}", this.LastErrorMessage);
                return;
            }

            var payload = await resp.Content
                .ReadFromJsonAsync<RoomsListedPayload>(cancellationToken: cts.Token)
                .ConfigureAwait(false);

            if (payload is null || !payload.ok)
            {
                this.Status = CatalogStatus.Failed;
                this.LastErrorMessage = "Server replied with ok=false.";
                return;
            }

            var raw = payload.rooms ?? Array.Empty<RoomsListedEntry>();
            var rooms = new List<RoomEntry>(raw.Length);
            foreach (var r in raw)
            {
                if (string.IsNullOrWhiteSpace(r.displayName)) continue;
                if (string.IsNullOrWhiteSpace(r.world)) continue;
                rooms.Add(new RoomEntry
                {
                    DisplayName = r.displayName!,
                    World = r.world!,
                    HasPassword = r.hasPassword,
                    PeerCount = Math.Max(0, r.peerCount),
                });
            }

            this.Rooms = rooms;
            this.Status = CatalogStatus.Loaded;
            this.LastFetchedAtUtc = DateTimeOffset.UtcNow;
        }
        catch (TaskCanceledException)
        {
            // Either user-cancelled (a new refresh fired) or network timeout.
            // Don't flip the status off Loading if a newer fetch is now in
            // flight — that newer call will set the terminal state.
            if (cts == this.activeFetch)
            {
                this.Status = CatalogStatus.Failed;
                this.LastErrorMessage = "Request timed out.";
            }
        }
        catch (Exception ex)
        {
            this.Status = CatalogStatus.Failed;
            this.LastErrorMessage = ex.Message;
            this.logger.Debug("PrivateRoomCatalog refresh threw: {0}", ex.Message);
        }
        finally
        {
            if (cts == this.activeFetch)
            {
                this.activeFetch = null;
            }
            cts.Dispose();
        }
    }

    public void Dispose()
    {
        try { this.activeFetch?.Cancel(); } catch { /* swallow */ }
        this.activeFetch?.Dispose();
        this.activeFetch = null;
        this.http.Dispose();
    }

    // Inbound wire shape. System.Text.Json's case-insensitive matching is on
    // by default for ReadFromJsonAsync, so lowercase field names align with
    // the server JSON without an explicit policy.
#pragma warning disable CS0649 // populated via System.Text.Json reflection
    private sealed class RoomsListedPayload
    {
        public bool ok { get; set; }
        public RoomsListedEntry[]? rooms { get; set; }
    }

    private sealed class RoomsListedEntry
    {
        public string? displayName { get; set; }
        public string? world { get; set; }
        public bool hasPassword { get; set; }
        public int peerCount { get; set; }
    }
#pragma warning restore CS0649
}
