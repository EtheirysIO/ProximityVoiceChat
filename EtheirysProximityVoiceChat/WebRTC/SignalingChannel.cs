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
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AsyncAwaitBestPractices;
using EtheirysProximityVoiceChat.Log;
using SocketIO.Serializer.SystemTextJson;
using SocketIOClient;

namespace EtheirysProximityVoiceChat.WebRTC;

public sealed class SignalingChannel : IDisposable
{
    /// <summary>
    /// Wire-protocol version. History:
    ///   • v1: mesh-WebRTC PCM DataChannels (legacy, no longer supported by server).
    ///   • v2: Socket.IO server-relayed Opus over WebSocket (= TCP). All audio on the
    ///         signaling channel itself.
    ///   • v3: signaling stays on Socket.IO/WSS but audio moves to a parallel UDP
    ///         datagram channel with AES-128-GCM AEAD. Server issues per-session
    ///         credentials via the <c>udpCredentials</c> event right after a
    ///         successful <c>ready</c>. v3 clients keep the Socket.IO <c>audio</c>
    ///         subscription as a TCP fallback for the cases where UDP can't be
    ///         established (corporate firewall, restrictive ISP, NAT failure).
    ///   • v4: private rooms overhaul — free-form display names suffixed by
    ///         <c>@CurrentWorld</c>, optional password, "unlisted" flag,
    ///         browse list via <c>/api/rooms/listed</c>, owner / moderator
    ///         model (room-level kick / ban), 24-peer cap, 24-hour empty-room
    ///         TTL. The <c>ready</c> emit now carries two new positional args
    ///         (<c>listed</c>, <c>currentWorld</c>); the server emits
    ///         <c>roomMeta</c> push events to owners/mods on moderation changes.
    /// The server accepts v2/v3/v4 clients in the same room; it cross-stitches
    /// the fan-out so peers on different protocol versions still hear each other.
    /// </summary>
    public const string ProtocolVersion = "4";

    // Since Dalamud 12, for some reason accessing socket parameters such as socket.Connected from the UI thread
    // would crash the game. So, intermediate field booleans are now used to indicate state to the UI.
    public bool Connected => this.socket != null && !this.connecting && this.disconnectCts != null && !this.disconnectCts.IsCancellationRequested;
    public bool Connecting => this.socket != null && this.connecting && this.disconnectCts != null && !this.disconnectCts.IsCancellationRequested;
    public string PeerId { get; set; }
    public string PeerType { get; }
    public string SignalingServerUrl { get; }
    public string Token { get; }
    /// <summary>
    /// Opaque premium token issued by the signaling server after a successful
    /// Discord OAuth link. Sent as the 6th argument of every <c>ready</c>
    /// event. Settable so the caller can refresh it without rebuilding the
    /// channel; an empty string is fine and just leaves the session
    /// non-premium server-side.
    /// </summary>
    public string PremiumToken { get; set; } = string.Empty;
    public string? RoomName { get; private set; }
    public SignalingChannelError? LatestError { get; private set; }
    public string? LatestErrorMessage { get; private set; }

    public event Action? OnConnected;
    public event Action? OnReady;
    /// <summary>
    /// Fires immediately after the server resolves this client's premium
    /// token on every connection. The bool is the server's current verdict;
    /// subscribers should mirror it into Configuration.IsLocalPremium.
    /// </summary>
    public event Action<bool>? OnPremiumStatus;
    /// <summary>
    /// Fires immediately after the server checks this client's peerId
    /// against ADMIN_PEER_IDS on every connection. The bool is whether the
    /// server considers us an admin for this session.
    /// </summary>
    public event Action<bool>? OnAdminStatus;
    /// <summary>
    /// Fires when the server reports a change (or initial state) to a
    /// peer's globalMuted flag. Only emitted to recipients allowed to see
    /// it — admins in the same room AND the target themselves. Arguments:
    /// (peerId, muted, isSelf). When isSelf is true the recipient is the
    /// peer being muted and should drive their own "you are muted" UI;
    /// otherwise the recipient is an admin and should track the peer in a
    /// roster-indicator dictionary.
    /// </summary>
    public event Action<string, bool, bool>? OnMuteState;
    public event Action<SocketIOResponse>? OnMessage;
    /// <summary>
    /// Fires when a server-relayed audio frame arrives. Args: (sender peerId, Opus packet bytes).
    /// </summary>
    public event Action<string, byte[]>? OnAudioFrame;

    /// <summary>
    /// Fires when the server sends us our UDP audio-session credentials —
    /// always immediately after the post-<c>ready</c> roster broadcast, on
    /// every connection. Only emitted for v3+ clients. The subscriber should
    /// construct a <c>UdpAudioChannel</c> with the supplied credentials and
    /// attempt the UDP hello-handshake; on success, audio routes over UDP;
    /// on failure, the existing Socket.IO <see cref="OnAudioFrame"/> path
    /// is the fallback.
    /// Internal because <c>UdpCredentials</c> is internal — only consumers
    /// inside the plugin assembly need it.
    /// </summary>
    internal event Action<UdpCredentials>? OnUdpCredentialsReceived;

    /// <summary>
    /// v4 only. Server pushes a meta snapshot (owner, moderators, bans,
    /// listed, hasPassword) to room authorities (owner + mods) on every
    /// moderation change. Subscribers should cache the latest snapshot and
    /// refresh the manage-room UI.
    /// </summary>
    public event Action<RoomMetaSnapshot>? OnRoomMeta;

    /// <summary>
    /// v4 only. Server broadcasts a server-wide "users online" count every
    /// 5 s. Subscribers should debounce on the consumer side if they're
    /// driving anything more expensive than a TextUnformatted.
    /// </summary>
    public event Action<int>? OnOnlineCount;

    /// <summary>
    /// Latest <c>onlineCount</c> received from the server. Null until the
    /// first broadcast lands. Cleared on disconnect so the UI can hide the
    /// line while disconnected.
    /// </summary>
    public int? LastOnlineCount { get; private set; }

    public event Action? OnDisconnected;
    public event Action? OnErrored;

    /// <summary>
    /// Most recent client → server round-trip latency in milliseconds, measured
    /// as the median of the last <see cref="PingSampleWindow"/> "clientPing"
    /// emit-with-ack round trips. Null until the first sample has come back, or
    /// while disconnected.
    /// </summary>
    public int? LastLatencyMs { get; private set; }

    /// <summary>
    /// Fires every time a fresh latency sample has been folded into the
    /// rolling median. Subscribers see the current median, not the raw sample.
    /// </summary>
    public event Action<int>? OnLatencyUpdated;

    private SocketIOClient.SocketIO? socket;
    private CancellationTokenSource? disconnectCts;
    private CancellationTokenSource? pingCts;
    private string? roomPassword;
    private string[]? playersInInstance;
    // v4: extra positional args sent with the `ready` emit. `listed` defaults to
    // true (legacy behaviour) and `currentWorld` is "" for public-room joins;
    // the server ignores both for public rooms.
    private bool roomListed = true;
    private string? roomCurrentWorld;
    private bool connecting;
    // Connecting to an empty room may not send back a "Ready" reply, so don't rely on this for connection state
    private bool ready;

    private readonly Queue<int> pingSamples = new();
    private const int PingSampleWindow = 5;
    private const int PingIntervalMs = 1_000;

    private readonly ILogger logger;
    private readonly bool verbose;

    public SignalingChannel(string peerId, string peerType, string signalingServerUrl, string token, ILogger logger, bool verbose = false)
    {
        this.PeerId = peerId;
        this.PeerType = peerType;
        this.SignalingServerUrl = signalingServerUrl;
        this.Token = token;
        this.logger = logger;
        this.verbose = verbose;
    }

    public Task ConnectAsync(string roomName, string roomPassword, string[]? playersInInstance, bool listed = true, string? currentWorld = null)
    {
        if (this.socket == null)
        {
            this.socket = new SocketIOClient.SocketIO(this.SignalingServerUrl, new SocketIOOptions
            {
                Auth = new Dictionary<string, string>()
                {
                    { "token", this.Token },
                    { "protocolVersion", ProtocolVersion },
                },
                Reconnection = true,
            });
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                IncludeFields = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };
            options.Converters.Add(new JsonStringEnumConverter());
            this.socket.Serializer = new SystemTextJsonSerializer(options);
            this.AddListeners();
        }

        if (this.socket.Connected)
        {
            this.logger.Error("Signaling server is already connected.");
            return Task.CompletedTask;
        }
        this.connecting = true;
        this.ready = false;
        this.disconnectCts?.Dispose();
        this.disconnectCts = new();
        this.RoomName = roomName;
        this.roomPassword = roomPassword;
        this.playersInInstance = playersInInstance;
        this.roomListed = listed;
        this.roomCurrentWorld = currentWorld;
        return this.socket.ConnectAsync(this.disconnectCts.Token);
    }

    public Task SendAsync(SignalMessage.SignalPayload payload)
    {
        if (this.socket != null && this.socket.Connected)
        {
            return this.socket.EmitAsync("message", new SignalMessage
            {
                from = this.PeerId,
                target = "all",
                payload = payload,
            });
        }
        else
        {
            return Task.CompletedTask;
        }
    }

    public Task SendToAsync(string targetPeerId, SignalMessage.SignalPayload payload)
    {
        if (this.socket != null && this.socket.Connected)
        {
            return this.socket.EmitAsync("messageOne", new SignalMessage
            {
                from = this.PeerId,
                target = targetPeerId,
                payload = payload,
            });
        }
        else
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Send one Opus-encoded audio frame upstream. The server fans it out to other peers in the same room.
    /// SocketIOClient transmits the byte array as a binary attachment (no JSON re-encoding overhead).
    /// </summary>
    public Task EmitAudioAsync(byte[] opusPacket)
    {
        if (this.socket != null && this.socket.Connected)
        {
            return this.socket.EmitAsync("audio", opusPacket);
        }
        else
        {
            return Task.CompletedTask;
        }
    }

    public async Task DisconnectAsync()
    {
        if (this.socket != null)
        {
            if (this.socket.Connected)
            {
                try
                {
                    await this.socket.DisconnectAsync().ConfigureAwait(false);
                    return;
                }
                catch (InvalidOperationException ex)
                {
                    // Socket.IO client can race its internal task transitions when
                    // disconnect is requested concurrently from multiple paths.
                    this.logger.Debug("Disconnect race ignored: {0}", ex.Message);
                    this.DisposeSocket();
                    return;
                }
                catch (AggregateException ex) when (ex.InnerException is InvalidOperationException)
                {
                    this.logger.Debug("Disconnect aggregate race ignored: {0}", ex.InnerException?.Message ?? ex.Message);
                    this.DisposeSocket();
                    return;
                }
            }
            else
            {
                this.logger.Debug("Cancelling signaling server connection.");
                try
                {
                    this.disconnectCts?.Cancel();
                }
                catch (InvalidOperationException ex)
                {
                    this.logger.Debug("Disconnect cancellation race ignored: {0}", ex.Message);
                }
                this.DisposeSocket();
                return;
            }
        }
    }

    public void ClearLatestError()
    {
        this.LatestError = null;
        this.LatestErrorMessage = null;
    }

    public void Dispose()
    {
        StopLatencyPingLoop();
        this.OnConnected = null;
        this.OnReady = null;
        this.OnPremiumStatus = null;
        this.OnAdminStatus = null;
        this.OnMuteState = null;
        this.OnMessage = null;
        this.OnAudioFrame = null;
        this.OnUdpCredentialsReceived = null;
        this.OnRoomMeta = null;
        this.OnOnlineCount = null;
        this.OnDisconnected = null;
        this.OnLatencyUpdated = null;
        this.DisposeSocket();
    }

    private void AddListeners()
    {
        if (this.socket != null)
        {
            this.socket.OnConnected += this.OnConnect;
            this.socket.OnDisconnected += this.OnDisconnect;
            this.socket.OnError += this.OnError;
            this.socket.OnReconnected += this.OnReconnect;
            this.socket.On("message", this.OnMessageCallback);
            this.socket.On("audio", this.OnAudioCallback);
            this.socket.On("serverDisconnect", this.OnServerDisconnect);
            this.socket.On("ping", this.OnPingCallback);
            this.socket.On("premiumStatus", this.OnPremiumStatusCallback);
            this.socket.On("adminStatus", this.OnAdminStatusCallback);
            this.socket.On("muteState", this.OnMuteStateCallback);
            this.socket.On("udpCredentials", this.OnUdpCredentialsCallback);
            this.socket.On("roomMeta", this.OnRoomMetaCallback);
            this.socket.On("onlineCount", this.OnOnlineCountCallback);
        }
    }

    private void DisposeSocket()
    {
        if (this.socket != null)
        {
            this.socket.OnConnected -= this.OnConnect;
            this.socket.OnDisconnected -= this.OnDisconnect;
            this.socket.OnError -= this.OnError;
            this.socket.OnReconnected -= this.OnReconnect;
            this.socket.Off("message");
            this.socket.Off("audio");
            this.socket.Off("serverDisconnect");
            this.socket.Off("ping");
            this.socket.Off("premiumStatus");
            this.socket.Off("adminStatus");
            this.socket.Off("muteState");
            this.socket.Off("udpCredentials");
            this.socket.Off("roomMeta");
            this.socket.Off("onlineCount");
            this.socket.Dispose();
        }
        this.socket = null;
        this.LastOnlineCount = null;
    }

    /// <summary>
    /// Variant of <see cref="DisposeSocket"/> safe to call from inside one of
    /// the library's own event callbacks (<c>OnDisconnect</c>, <c>OnError</c>,
    /// or a server-side message handler like <c>serverDisconnect</c>).
    /// SocketIOClient's internal <c>InvokeDisconnect</c>/<c>InvokeError</c>
    /// continues to touch its own <see cref="CancellationTokenSource"/> after
    /// firing the event — disposing the socket synchronously from within the
    /// handler tears that CTS down mid-flight and produces an unobserved
    /// <see cref="ObjectDisposedException"/> on the finalizer thread.
    ///
    /// This helper detaches our handlers synchronously (so we stop receiving
    /// callbacks immediately) but defers the actual <c>socket.Dispose()</c>
    /// to a <see cref="Task.Run"/> continuation, letting the library finish
    /// its own teardown before we tear down its underlying state. Any
    /// race-condition exception on the deferred dispose is swallowed since
    /// the library was already in the middle of disposing itself.
    /// </summary>
    private void DisposeSocketDeferred()
    {
        var sock = this.socket;
        this.socket = null;
        if (sock == null) return;

        // Detach our handlers up front — we don't want any more callbacks
        // from this socket, even if the deferred Dispose() is still pending.
        sock.OnConnected -= this.OnConnect;
        sock.OnDisconnected -= this.OnDisconnect;
        sock.OnError -= this.OnError;
        sock.OnReconnected -= this.OnReconnect;
        sock.Off("message");
        sock.Off("audio");
        sock.Off("serverDisconnect");
        sock.Off("ping");
        sock.Off("premiumStatus");
        sock.Off("adminStatus");
        sock.Off("muteState");
        sock.Off("udpCredentials");
        sock.Off("roomMeta");
        sock.Off("onlineCount");

        Task.Run(() =>
        {
            try
            {
                sock.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Library already tore its own internal state down — expected
                // race when disconnecting at the same instant the server
                // closes the socket. Nothing to do.
            }
            catch (Exception ex)
            {
                this.logger.Debug("Deferred socket dispose threw: {0}", ex.Message);
            }
        });
    }

    private void OnConnect(object? sender, EventArgs args)
    {
        try
        {
            if (this.socket == null || !this.socket.Connected)
            {
                return;
            }

            if (this.verbose)
            {
                this.logger.Debug("Connected to signaling server.");
            }
            this.connecting = false;
            this.OnConnected?.Invoke();
            // v4 ready signature: peerId, peerType, roomName, roomPassword,
            // playersInInstance, premiumToken, listed, currentWorld.
            // The server tolerates the extra trailing args from older v2/v3
            // emits (Socket.IO handler params get padded with undefined), but
            // v4 clients MUST send them or private-room create/join is
            // rejected on the server side with "Invalid or missing world."
            this.socket.EmitAsync(
                "ready",
                this.PeerId,
                this.PeerType,
                this.RoomName,
                this.roomPassword,
                this.playersInInstance,
                this.PremiumToken ?? string.Empty,
                this.roomListed,
                this.roomCurrentWorld ?? string.Empty)
                .SafeFireAndForget(ex => this.logger.Error(ex.ToString()));
            StartLatencyPingLoop();
        }
        catch (Exception ex)
        {
            this.logger.Error(ex.ToString());
        }
    }

    private void OnDisconnect(object? sender, string reason)
    {
        try
        {
            if (this.verbose)
            {
                this.logger.Debug("Disconnected from signaling server, reason: {0}", reason);
            }
            StopLatencyPingLoop();
            this.OnDisconnected?.Invoke();
            // Fires from inside the library's InvokeDisconnect — defer the
            // Dispose so the library can finish its own internal teardown.
            this.DisposeSocketDeferred();
        }
        catch (Exception ex)
        {
            this.logger.Error(ex.ToString());
        }
    }

    private void OnError(object? sender, string error)
    {
        this.logger.Error("Signaling server ERROR: " + error);
        // An errored socket is considered disconnected, but we'll need to manually set disconnection state
        // and cancel the connection attempt.
        this.disconnectCts?.Cancel();
        this.disconnectCts?.Dispose();
        this.disconnectCts = null;
        this.OnErrored?.Invoke();
        // Same race as OnDisconnect: the library still touches its own CTS
        // after this handler returns. Defer the actual socket dispose.
        this.DisposeSocketDeferred();
    }

    private void OnReconnect(object? sender, int attempts)
    {
        if (this.verbose)
        {
            this.logger.Info("Signaling server reconnect, attempts: {0}", attempts);
        }
    }

    private void OnMessageCallback(SocketIOResponse response)
    {
        //if (this.verbose)
        //{
        //    this.logger.Trace("Signaling server message: {0}", response);
        //}
        this.OnMessage?.Invoke(response);
        // Assume that any message callback implies readiness
        if (!this.ready)
        {
            this.ready = true;
            this.OnReady?.Invoke();
        }
    }

    private void OnAudioCallback(SocketIOResponse response)
    {
        try
        {
            // Server emits ("audio", senderPeerId, opusBytes).
            // SocketIOClient automatically materializes the binary attachment as byte[].
            var from = response.GetValue<string>(0);
            var frame = response.GetValue<byte[]>(1);
            if (!string.IsNullOrEmpty(from) && frame != null && frame.Length > 0)
            {
                this.OnAudioFrame?.Invoke(from, frame);
            }
        }
        catch (Exception ex)
        {
            this.logger.Error("Failed to parse audio frame: {0}", ex);
        }
    }

    /// <summary>
    /// Server emits "premiumStatus" right after token resolution in `ready`.
    /// We surface the boolean to any subscriber (VoiceRoomManager mirrors it
    /// into Configuration.IsLocalPremium so the UI knows which mode to draw).
    /// </summary>
    private void OnPremiumStatusCallback(SocketIOResponse response)
    {
        try
        {
            var payload = response.GetValue<PremiumStatusPayload>();
            this.OnPremiumStatus?.Invoke(payload.premium);
        }
        catch (Exception ex)
        {
            this.logger.Debug("premiumStatus parse failed: {0}", ex.Message);
        }
    }

    // Fields are populated by SocketIOClient's reflection-based JSON
    // deserializer, which the C# compiler can't see — hence the CS0649
    // "field is never assigned" false positive. Disable the warning around
    // each inbound payload struct.
#pragma warning disable CS0649
    private struct PremiumStatusPayload
    {
        public bool premium;
    }
#pragma warning restore CS0649

    /// <summary>
    /// v3+ only. Server emits "udpCredentials" right after the post-ready
    /// roster broadcast, on every connection. Carries the per-session
    /// AES-128-GCM key + opaque session id + UDP endpoint. Subscribers
    /// (<c>VoiceRoomManager</c>) hand these to a <c>UdpAudioChannel</c>
    /// and start the UDP hello-handshake; on failure the existing Socket.IO
    /// audio fallback continues to work without further action.
    /// </summary>
    private void OnUdpCredentialsCallback(SocketIOResponse response)
    {
        try
        {
            var payload = response.GetValue<UdpCredentialsPayload>();

            // sessionId and sessionKey arrive as hex strings — decode here
            // so the consumer always sees the binary form.
            byte[] sessionId, sessionKey;
            try
            {
                sessionId = Convert.FromHexString(payload.sessionId ?? string.Empty);
                sessionKey = Convert.FromHexString(payload.sessionKey ?? string.Empty);
            }
            catch (FormatException ex)
            {
                this.logger.Warn("udpCredentials: bad hex ({0}); UDP disabled for this session.", ex.Message);
                return;
            }
            if (sessionId.Length != 8 || sessionKey.Length != 16)
            {
                this.logger.Warn(
                    "udpCredentials: wrong sizes (sessionId={0}b, sessionKey={1}b); UDP disabled.",
                    sessionId.Length, sessionKey.Length);
                return;
            }
            if (string.IsNullOrWhiteSpace(payload.udpHost) || payload.udpPort <= 0 || payload.udpPort > 65535)
            {
                this.logger.Warn("udpCredentials: invalid endpoint {0}:{1}; UDP disabled.",
                    payload.udpHost ?? "(null)", payload.udpPort);
                return;
            }

            var creds = new UdpCredentials
            {
                SessionIdBytes = sessionId,
                SessionKey = sessionKey,
                UdpHost = payload.udpHost,
                UdpPort = payload.udpPort,
                TtlSeconds = payload.ttlSeconds,
            };
            this.OnUdpCredentialsReceived?.Invoke(creds);
        }
        catch (Exception ex)
        {
            this.logger.Debug("udpCredentials parse failed: {0}", ex.Message);
        }
    }

#pragma warning disable CS0649 // deserialized via reflection — see PremiumStatusPayload
    private struct UdpCredentialsPayload
    {
        public string? sessionId;     // hex
        public string? sessionKey;    // hex
        public string? udpHost;
        public int udpPort;
        public int ttlSeconds;
    }
#pragma warning restore CS0649

    /// <summary>
    /// Server emits "adminStatus" right after "premiumStatus" in `ready`.
    /// Carries whether the server considers us an admin for this session
    /// (peerId matched ADMIN_PEER_IDS). Subscribers update their UI; the
    /// server still validates every admin command, so a tampered client
    /// can't actually do anything.
    /// </summary>
    private void OnAdminStatusCallback(SocketIOResponse response)
    {
        try
        {
            var payload = response.GetValue<AdminStatusPayload>();
            this.OnAdminStatus?.Invoke(payload.isAdmin);
        }
        catch (Exception ex)
        {
            this.logger.Debug("adminStatus parse failed: {0}", ex.Message);
        }
    }

#pragma warning disable CS0649 // deserialized via reflection — see PremiumStatusPayload
    private struct AdminStatusPayload
    {
        public bool isAdmin;
    }
#pragma warning restore CS0649

    /// <summary>
    /// Server emits "muteState" only to admins in the same room as the
    /// target peer AND to the target themselves. Non-admin observers in
    /// the room never see this — the silent-mute invariant is preserved
    /// by who the server sends the event to, not by anything the plugin
    /// does. The <c>self</c> flag tells the plugin which UI to drive
    /// (banner for self, roster indicator for someone else).
    /// </summary>
    private void OnMuteStateCallback(SocketIOResponse response)
    {
        try
        {
            var payload = response.GetValue<MuteStatePayload>();
            if (string.IsNullOrEmpty(payload.peerId)) return;
            this.OnMuteState?.Invoke(payload.peerId, payload.muted, payload.self);
        }
        catch (Exception ex)
        {
            this.logger.Debug("muteState parse failed: {0}", ex.Message);
        }
    }

#pragma warning disable CS0649 // deserialized via reflection — see PremiumStatusPayload
    private struct MuteStatePayload
    {
        public string peerId;
        public bool muted;
        public bool self;
    }
#pragma warning restore CS0649

    /// <summary>
    /// Admin command: silently mute (or unmute) a peer for the entire
    /// channel. The server validates that the caller is on its admin
    /// allowlist and drops the request otherwise; rendering the menu item
    /// is a UI convenience, not the security boundary.
    /// </summary>
    public Task SendAdminGlobalMuteAsync(string targetPeerId, bool mute)
    {
        if (this.socket == null || !this.socket.Connected) return Task.CompletedTask;
        return this.socket.EmitAsync("adminGlobalMute", new AdminGlobalMutePayload { peerId = targetPeerId, mute = mute });
    }

    /// <summary>
    /// Admin command: disconnect a peer and lock them out of every room
    /// for 30 s. Server-validated like <see cref="SendAdminGlobalMuteAsync"/>.
    /// </summary>
    public Task SendAdminKickAsync(string targetPeerId)
    {
        if (this.socket == null || !this.socket.Connected) return Task.CompletedTask;
        return this.socket.EmitAsync("adminKick", new AdminKickPayload { peerId = targetPeerId });
    }

    private struct AdminGlobalMutePayload
    {
        public string peerId;
        public bool mute;
    }

    private struct AdminKickPayload
    {
        public string peerId;
    }

    /// <summary>
    /// Server-driven liveness probe used by the signaling server's
    /// stale-socket eviction path: when a new socket tries to claim a peerId
    /// already bound to an existing socket, the server emits "ping" to that
    /// existing socket with an ack callback. If we don't ack within ~2 s,
    /// the server assumes our connection is dead and evicts us. So just
    /// acknowledge immediately — the mere fact that the SocketIO event loop
    /// is reaching us proves we're alive.
    /// </summary>
    private void OnPingCallback(SocketIOResponse response)
    {
        try
        {
            response.CallbackAsync().SafeFireAndForget(ex => this.logger.Debug("ping ack failed: {0}", ex));
        }
        catch (Exception ex)
        {
            this.logger.Debug("ping ack threw: {0}", ex);
        }
    }

    /// <summary>
    /// Client-initiated RTT measurement. Emits "clientPing" with an ack on a
    /// fixed interval; the server's handler immediately acks, so the elapsed
    /// time between emit and callback is a usable signaling-server round-trip
    /// latency. Samples are kept in a sliding window and reported as the
    /// median to smooth out jitter.
    /// </summary>
    private void StartLatencyPingLoop()
    {
        StopLatencyPingLoop();
        var cts = new CancellationTokenSource();
        this.pingCts = cts;
        lock (this.pingSamples) { this.pingSamples.Clear(); }
        this.LastLatencyMs = null;
        _ = Task.Run(() => LatencyPingLoop(cts.Token));
    }

    private void StopLatencyPingLoop()
    {
        try
        {
            this.pingCts?.Cancel();
            this.pingCts?.Dispose();
        }
        catch { /* nothing to do */ }
        this.pingCts = null;
        this.LastLatencyMs = null;
        lock (this.pingSamples) { this.pingSamples.Clear(); }
    }

    private async Task LatencyPingLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var localSocket = this.socket;
                if (localSocket != null && localSocket.Connected)
                {
                    var sentAt = Environment.TickCount64;
                    await localSocket.EmitAsync("clientPing", _ =>
                    {
                        if (ct.IsCancellationRequested) return;
                        var rtt = (int)Math.Max(0, Environment.TickCount64 - sentAt);
                        RecordLatencySample(rtt);
                    }).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (this.verbose) this.logger.Debug("clientPing emit failed: {0}", ex.Message);
            }

            try { await Task.Delay(PingIntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void RecordLatencySample(int rttMs)
    {
        int median;
        lock (this.pingSamples)
        {
            this.pingSamples.Enqueue(rttMs);
            while (this.pingSamples.Count > PingSampleWindow)
            {
                this.pingSamples.Dequeue();
            }
            var sorted = this.pingSamples.OrderBy(x => x).ToArray();
            median = sorted[sorted.Length / 2];
        }
        this.LastLatencyMs = median;
        try { this.OnLatencyUpdated?.Invoke(median); }
        catch (Exception ex) { this.logger.Debug("OnLatencyUpdated threw: {0}", ex.Message); }
    }

    /// <summary>
    /// v4 only. Server pushes <c>roomMeta</c> snapshots to authorities on
    /// moderation changes. Subscribers re-render the manage-room UI.
    /// </summary>
    private void OnRoomMetaCallback(SocketIOResponse response)
    {
        try
        {
            var payload = response.GetValue<RoomMetaPayload>();
            this.OnRoomMeta?.Invoke(new RoomMetaSnapshot
            {
                OwnerPeerId = payload.ownerPeerId ?? string.Empty,
                Moderators = payload.moderators ?? Array.Empty<string>(),
                BannedPeerIds = payload.bannedPeerIds ?? Array.Empty<string>(),
                Listed = payload.listed,
                HasPassword = payload.hasPassword,
            });
        }
        catch (Exception ex)
        {
            this.logger.Debug("roomMeta parse failed: {0}", ex.Message);
        }
    }

    /// <summary>
    /// v4 only. Server broadcasts the server-wide online count every 5 s.
    /// </summary>
    private void OnOnlineCountCallback(SocketIOResponse response)
    {
        try
        {
            // Server emits the value as a bare integer (`io.emit("onlineCount", N)`)
            // — pull it out of slot 0.
            var n = response.GetValue<int>(0);
            this.LastOnlineCount = n;
            this.OnOnlineCount?.Invoke(n);
        }
        catch (Exception ex)
        {
            this.logger.Debug("onlineCount parse failed: {0}", ex.Message);
        }
    }

#pragma warning disable CS0649 // deserialized via reflection — see PremiumStatusPayload
    private struct RoomMetaPayload
    {
        public string? ownerPeerId;
        public string[]? moderators;
        public string[]? bannedPeerIds;
        public bool listed;
        public bool hasPassword;
    }
#pragma warning restore CS0649

    /// <summary>
    /// Cached parsed shape of the server's <c>roomMeta</c> snapshot. Held by
    /// consumers (e.g. <c>MainWindow</c>) so the manage-room UI can re-render
    /// without a round-trip on every frame.
    /// </summary>
    public readonly struct RoomMetaSnapshot
    {
        public string OwnerPeerId { get; init; }
        public IReadOnlyList<string> Moderators { get; init; }
        public IReadOnlyList<string> BannedPeerIds { get; init; }
        public bool Listed { get; init; }
        public bool HasPassword { get; init; }
    }

    /// <summary>
    /// v4 owner/mod: kick a peer from the room. They can reconnect immediately
    /// unless they're also banned (see <see cref="SendRoomBanAsync"/>).
    /// </summary>
    public Task SendRoomKickAsync(string targetPeerId)
    {
        if (this.socket == null || !this.socket.Connected) return Task.CompletedTask;
        return this.socket.EmitAsync("roomKick", new RoomTargetPayload { peerId = targetPeerId });
    }

    /// <summary>
    /// v4 owner/mod: ban a peer from the room. Kicks them (if present) and
    /// rejects future re-join attempts with the same peerId.
    /// </summary>
    public Task SendRoomBanAsync(string targetPeerId)
    {
        if (this.socket == null || !this.socket.Connected) return Task.CompletedTask;
        return this.socket.EmitAsync("roomBan", new RoomTargetPayload { peerId = targetPeerId });
    }

    /// <summary>v4 owner/mod: undo a ban.</summary>
    public Task SendRoomUnbanAsync(string targetPeerId)
    {
        if (this.socket == null || !this.socket.Connected) return Task.CompletedTask;
        return this.socket.EmitAsync("roomUnban", new RoomTargetPayload { peerId = targetPeerId });
    }

    /// <summary>v4 owner only: appoint a moderator.</summary>
    public Task SendRoomAppointModAsync(string targetPeerId)
    {
        if (this.socket == null || !this.socket.Connected) return Task.CompletedTask;
        return this.socket.EmitAsync("roomAppointMod", new RoomTargetPayload { peerId = targetPeerId });
    }

    /// <summary>v4 owner only: revoke a moderator.</summary>
    public Task SendRoomRevokeModAsync(string targetPeerId)
    {
        if (this.socket == null || !this.socket.Connected) return Task.CompletedTask;
        return this.socket.EmitAsync("roomRevokeMod", new RoomTargetPayload { peerId = targetPeerId });
    }

    private struct RoomTargetPayload
    {
        public string peerId;
    }

    private void OnServerDisconnect(SocketIOResponse response)
    {
        var msg = response.GetValue<SignalDisconnectMessage>();
        var errorMsg = msg.message ?? string.Empty;
        var reason = msg.reason ?? string.Empty;
        this.LatestErrorMessage = errorMsg;

        // Prefer the structured `reason` discriminator over substring matching
        // on the message. v4 server emits codes for the cases that the plugin
        // needs to distinguish; we still fall back to the substring sniff for
        // legacy v3 messages and any case the server didn't tag.
        if (reason.Equals("room-full", StringComparison.OrdinalIgnoreCase))
        {
            this.LatestError = SignalingChannelError.PrivateRoomFull;
        }
        else if (reason.Equals("banned", StringComparison.OrdinalIgnoreCase))
        {
            this.LatestError = SignalingChannelError.BannedFromPrivateRoom;
        }
        else if (reason.Equals("bad-password", StringComparison.OrdinalIgnoreCase)
              || errorMsg.Contains("incorrect password", StringComparison.OrdinalIgnoreCase))
        {
            this.LatestError = SignalingChannelError.IncorrectPrivateRoomPassword;
        }
        else if (errorMsg.Contains("room does not exist", StringComparison.OrdinalIgnoreCase))
        {
            this.LatestError = SignalingChannelError.NonexistentPrivateRoom;
        }
        else if (errorMsg.Contains("already own", StringComparison.OrdinalIgnoreCase))
        {
            this.LatestError = SignalingChannelError.AlreadyOwnAnotherRoom;
        }
        else if (errorMsg.Contains("Room name", StringComparison.OrdinalIgnoreCase)
              || errorMsg.Contains("Invalid or missing world", StringComparison.OrdinalIgnoreCase))
        {
            this.LatestError = SignalingChannelError.InvalidPrivateRoomName;
        }
        else if (reason.Contains("kicked", StringComparison.OrdinalIgnoreCase)
              || reason.Equals("room-kicked", StringComparison.OrdinalIgnoreCase)
              || errorMsg.Contains("kicked", StringComparison.OrdinalIgnoreCase))
        {
            this.LatestError = SignalingChannelError.KickedFromChannel;
        }
        else
        {
            this.LatestError = SignalingChannelError.Unknown;
        }
        this.logger.Error("Signaling server disconnect: {0}", response);

        // DEPRECATED COMMENT:
        // This message auto disconnects the client, but does not immediately set the socket state to not Connected.
        // So we need to dispose and nullify the token to avoid calling Cancel on the token, which for some reason
        // throws an exception due to cancellation token subscriptions.
        this.disconnectCts?.Dispose();
        this.disconnectCts = null;
        // Defer the actual socket dispose: this handler is invoked from inside
        // the SocketIOClient event loop, and the library can race with us
        // tearing down its internal state — see DisposeSocketDeferred docs.
        this.DisposeSocketDeferred();

        // Since Dalamud 12, this message no longer auto disconnects the client.
        this.OnDisconnected?.Invoke();
    }
}
