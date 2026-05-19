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
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsyncAwaitBestPractices;
using NAudio.Wave;
using EtheirysProximityVoiceChat.Audio;
using EtheirysProximityVoiceChat.Extensions;
using EtheirysProximityVoiceChat.Input;
using EtheirysProximityVoiceChat.Log;
using EtheirysProximityVoiceChat.WebRTC;

namespace EtheirysProximityVoiceChat;

public sealed class VoiceRoomManager : IDisposable
{
    private const string AdminAudioPeerPrefix = "admin::";

    /// <summary>
    /// When in a public room, this plugin will automatically switch voice rooms when the player changes maps.
    /// This property indicates if the player should be connected to a public voice room.
    /// </summary>
    public bool ShouldBeInRoom { get; private set; }

    public bool InRoom { get; private set; }

    /// <summary>
    /// v4 only. Latest <c>roomMeta</c> snapshot pushed by the server. Null
    /// when not in a private room, when the server hasn't yet pushed a
    /// snapshot (the server only emits these to room authorities — owner +
    /// mods — so non-authority joiners never receive one), or after a
    /// disconnect. Read by <c>MainWindow</c> to decide which moderation
    /// controls to render in the per-peer right-click menu.
    /// </summary>
    public WebRTC.SignalingChannel.RoomMetaSnapshot? CurrentRoomMeta { get; private set; }

    /// <summary>
    /// Convenience: true when the local player is the owner or a moderator
    /// of the current room (per the latest <see cref="CurrentRoomMeta"/>).
    /// </summary>
    public bool IsLocalRoomAuthority
    {
        get
        {
            var meta = this.CurrentRoomMeta;
            if (meta == null || string.IsNullOrEmpty(this.localPlayerFullName)) return false;
            if (meta.Value.OwnerPeerId == this.localPlayerFullName) return true;
            var mods = meta.Value.Moderators;
            return mods != null && mods.Contains(this.localPlayerFullName);
        }
    }

    /// <summary>True when the local player is the owner of the current room.</summary>
    public bool IsLocalRoomOwner
    {
        get
        {
            var meta = this.CurrentRoomMeta;
            return meta != null
                && !string.IsNullOrEmpty(this.localPlayerFullName)
                && meta.Value.OwnerPeerId == this.localPlayerFullName;
        }
    }

    public bool InPublicRoom
    {
        get
        {
#if DEBUG
            return false;
#else
            return InRoom && (this.SignalingChannel?.RoomName?.StartsWith("public") ?? false);
#endif
        }
    }

    public IEnumerable<string> PlayersInVoiceRoom
    {
        get
        {
            if (!InRoom) return [];
            var selfName = this.localPlayerFullName ?? "null";
            return this.Presence != null
                ? this.Presence.Peers.Keys.Prepend(selfName)
                : [selfName];
        }
    }

    public SignalingChannel? SignalingChannel { get; private set; }

    /// <summary>
    /// v3 UDP audio transport for the current session, or null when no v3
    /// session has been established yet (mid-connect, mid-disconnect, or
    /// the server didn't issue udpCredentials). <see cref="ActiveAudioTransport"/>
    /// reflects which transport is currently routing send-side audio.
    /// </summary>
    internal UdpAudioChannel? UdpAudioChannel { get; private set; }

    public enum AudioTransport
    {
        /// <summary>No active session yet (or fully disconnected).</summary>
        None = 0,
        /// <summary>Audio uses Socket.IO/WSS — either the user disabled UDP, or UDP is still mid-handshake, or it failed.</summary>
        Tcp = 1,
        /// <summary>Audio uses the UDP datagram channel — the v3 happy path.</summary>
        Udp = 2,
    }

    /// <summary>
    /// Which transport <see cref="SendAudioFrameToServer"/> is currently
    /// using. Updated automatically when the UDP hello completes or fails.
    /// Surfaced in the ConfigWindow's "Audio transport: …" status line so
    /// users and bug reports can see what's actually in flight.
    /// </summary>
    public AudioTransport ActiveAudioTransport { get; private set; } = AudioTransport.None;

    /// <summary>
    /// Optional reason string explaining why <see cref="ActiveAudioTransport"/>
    /// is <see cref="AudioTransport.Tcp"/> rather than UDP — for example
    /// <c>"hello-ack-timeout"</c> or <c>"user-disabled"</c>. Surfaced in the
    /// UI status line and in the per-session info log line for diagnostics.
    /// </summary>
    public string? AudioTransportReason { get; private set; }
    public PeerPresenceManager? Presence { get; private set; }

    public Dictionary<string, TrackedPlayer> TrackedPlayers { get; } = [];

    /// <summary>
    /// Per-room dictionary of peerIds the server has told us are currently
    /// globally muted. The server only sends <c>muteState</c> to admins +
    /// the muted target themselves, so on a non-admin client this dictionary
    /// stays empty (or only contains the local player's own peerId, never
    /// shown in the roster anyway). On an admin client it drives the
    /// mute-indicator icon on each peer row. Cleared on every room leave so
    /// stale entries from a previous session don't leak.
    /// </summary>
    public Dictionary<string, bool> GloballyMutedPeers { get; } = new(StringComparer.Ordinal);

    private const string PeerType = "player";

    // Auto-reconnect: when the socket drops while the user still wants to be
    // in a room, we retry the original join up to ReconnectMaxAttempts times
    // with ReconnectDelay between attempts. The last* fields cache the params
    // of the most recent JoinVoiceRoom call so the retry loop can replay them
    // without depending on the (possibly disposed) SignalingChannel for state.
    private const int ReconnectMaxAttempts = 5;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private CancellationTokenSource? reconnectCts;
    private string? lastRoomName;
    private string? lastRoomPassword;
    private string[]? lastPlayersInInstance;
    // v4 private-room metadata cached for replay by the auto-reconnect loop.
    // Both default to "public-mode-safe" values (listed=true is meaningless
    // for public, currentWorld=null means "server, don't try to interpret
    // this as a v4 private room").
    private bool lastListed = true;
    private string? lastCurrentWorld;

    /// <summary>True while a reconnect retry loop is in flight.</summary>
    public bool IsReconnecting { get; private set; }
    /// <summary>1..<see cref="ReconnectMaxAttempts"/> while <see cref="IsReconnecting"/> is true. 0 otherwise.</summary>
    public int ReconnectAttempt { get; private set; }

    /// <summary>
    /// Tracks whether the current session was started via
    /// <see cref="JoinPublicVoiceRoom"/> (true) or
    /// <see cref="JoinPrivateVoiceRoom"/> (false). Used at <see cref="Dispose"/>
    /// to decide which kind of resume to write into the Configuration so the
    /// next plugin load can call the matching join entry point.
    /// </summary>
    private bool currentSessionIsPublic;

    private string? localPlayerFullName;
    private OpusCodec? opusCodec;

    private readonly DalamudServices dalamud;
    private readonly Configuration configuration;
    private readonly MapManager mapManager;
    private readonly IAudioDeviceController audioDeviceController;
    private readonly ILogger logger;

    private readonly CachedSound roomJoinSound;
    private readonly CachedSound roomSelfLeaveSound;
    private readonly CachedSound roomOtherLeaveSound;

    public VoiceRoomManager(
        DalamudServices dalamud,
        Configuration configuration,
        MapManager mapManager,
        IAudioDeviceController audioDeviceController,
        ILogger logger)
    {
        this.dalamud = dalamud;
        this.configuration = configuration;
        this.mapManager = mapManager;
        this.audioDeviceController = audioDeviceController;
        this.logger = logger;

        // Signaling URL + TOKEN are compile-time constants in EmbeddedConfig.cs
        // (gitignored on the maintainer's build machine, template in
        // EmbeddedConfig.example.cs). They ship as plain string constants in the
        // shipped DLL — anyone with ILSpy can read them out. Per-user credential
        // rotation is the right server-side fix; tracked separately.

        this.roomJoinSound = new(this.dalamud.PluginInterface.GetResourcePath("join.wav"));
        this.roomOtherLeaveSound = new(this.dalamud.PluginInterface.GetResourcePath("other_leave.wav"));
        this.roomSelfLeaveSound = new(this.dalamud.PluginInterface.GetResourcePath("self_leave.wav"));

        this.dalamud.ClientState.Logout += OnLogout;

        // After all wiring is set up, check whether the previous plugin
        // instance left a resume marker for this same game process.
        TryResumeFromLastSession();
    }

    public void Dispose()
    {
        // Capture resume state BEFORE the explicit-leave path clears intent.
        // Persists to Configuration if the user wants to be in a room when the
        // plugin shuts down (e.g. hot-update). The next plugin load reads this
        // and rejoins automatically — but only if the OS process fingerprint
        // still matches, so a full game restart starts clean.
        PersistResumeState();

        // Drive a graceful disconnect first so the signaling server sees us leave
        // immediately and pushes a "close" message to remaining peers. Without
        // this, the socket dies abruptly and other peers see us "ghost" until
        // Socket.IO's heartbeat times out (~25 s).
        //
        // Bounded with a short timeout: if the disconnect handshake stalls for
        // any reason, the plugin unload still proceeds — the server will fall
        // back to its heartbeat detection. We can't await this; Dispose is sync.
        try
        {
            if (this.InRoom)
            {
                LeaveVoiceRoom(autoRejoin: false).Wait(TimeSpan.FromMilliseconds(750));
            }
        }
        catch (Exception ex)
        {
            // Never let teardown throw — the plugin MUST unload regardless.
            try { this.logger.Error("Error during voice room teardown: {0}", ex); } catch { /* ignore */ }
        }
        finally
        {
            this.SignalingChannel?.Dispose();
            this.Presence?.Dispose();
            this.opusCodec?.Dispose();
            this.opusCodec = null;
            this.dalamud.ClientState.Logout -= OnLogout;
        }
    }

    /// <summary>
    /// Write a resume marker into <see cref="Configuration"/> if the user
    /// still <em>wants</em> to be in a room at shutdown — covers both
    /// connected sessions and ones that were mid-reconnect. The marker
    /// includes the current OS process fingerprint; on the next plugin load,
    /// <see cref="TryResumeFromLastSession"/> only rejoins when that
    /// fingerprint still matches (i.e. the game wasn't restarted).
    /// </summary>
    private void PersistResumeState()
    {
        try
        {
            if (this.ShouldBeInRoom)
            {
                var (pid, startTicks) = GetCurrentProcessFingerprint();
                this.configuration.ResumeRoomKind = this.currentSessionIsPublic ? "public" : "private";
                // Public rooms re-derive the room name from the current map on
                // rejoin, so we only persist room name + password for private.
                this.configuration.ResumeRoomName = this.currentSessionIsPublic
                    ? string.Empty
                    : (this.lastRoomName ?? string.Empty);
                this.configuration.ResumeRoomPassword = this.currentSessionIsPublic
                    ? string.Empty
                    : (this.lastRoomPassword ?? string.Empty);
                this.configuration.ResumeProcessId = pid;
                this.configuration.ResumeProcessStartTimeTicks = startTicks;
            }
            else
            {
                // No active intent — make sure any leftover marker from an
                // earlier session is cleared, so we don't auto-rejoin after the
                // user explicitly left.
                ClearResumeState();
            }
            this.configuration.Save();
        }
        catch (Exception ex)
        {
            // Never let resume-state persistence block plugin shutdown.
            try { this.logger.Error("Failed to persist resume state: {0}", ex); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Reads a resume marker (if any) left by the previous plugin instance,
    /// validates the OS process fingerprint, and on a match schedules a
    /// rejoin via the existing public/private entry points. Always clears
    /// the marker after reading so a saved password lives on disk for at
    /// most one plugin lifecycle, and so a stale marker from an unrelated
    /// game launch is discarded the first time the plugin loads under it.
    /// </summary>
    private void TryResumeFromLastSession()
    {
        var kind = this.configuration.ResumeRoomKind;
        var savedPid = this.configuration.ResumeProcessId;
        var savedStartTicks = this.configuration.ResumeProcessStartTimeTicks;
        var savedRoomName = this.configuration.ResumeRoomName;
        var savedRoomPassword = this.configuration.ResumeRoomPassword;

        // Clear-on-read, regardless of whether we end up using the data.
        ClearResumeState();
        this.configuration.Save();

        if (string.IsNullOrEmpty(kind)) return;

        var (curPid, curStartTicks) = GetCurrentProcessFingerprint();
        if (savedPid != curPid || savedStartTicks != curStartTicks)
        {
            this.logger.Info(
                "Skipping voice-room resume: game process changed (saved pid={0} start={1}, current pid={2} start={3}).",
                savedPid, savedStartTicks, curPid, curStartTicks);
            return;
        }

        this.logger.Info("Resuming previous voice session (kind={0}) after plugin reload.", kind);

        // The join entry points need PlayerState populated. If the player is
        // already in-game, give Dalamud a beat to fully initialize after the
        // plugin reload (matches ReconnectToCurrentMapPublicRoom's pattern).
        // If not logged in yet (plugin reloaded at title screen), wait for the
        // Login event and fire then.
        void DoJoin()
        {
            try
            {
                if (kind == "public") JoinPublicVoiceRoom();
                else if (kind == "private")
                {
                    // v4: private joins need (listed, currentWorld) — read
                    // listed from the live Configuration toggle, and pull
                    // the current world from PlayerState. If the world isn't
                    // available (shouldn't happen post-Login, but defensive),
                    // skip the resume rather than emit a guaranteed-to-fail
                    // ready with an empty world.
                    var listed = !this.configuration.RoomUnlisted;
                    string? world = null;
                    try
                    {
                        var w = this.dalamud.PlayerState.CurrentWorld;
                        if (w.IsValid) world = w.Value.Name.ExtractText();
                    }
                    catch { /* leave world null below */ }
                    if (string.IsNullOrWhiteSpace(world))
                    {
                        this.logger.Info("Skipping private-room resume: current world is not yet available.");
                        return;
                    }
                    JoinPrivateVoiceRoom(savedRoomName, savedRoomPassword, listed, world);
                }
            }
            catch (Exception ex) { this.logger.Error(ex.ToString()); }
        }

        if (this.dalamud.ClientState.IsLoggedIn)
        {
            Task.Run(async () =>
            {
                await Task.Delay(1000).ConfigureAwait(false);
                await this.dalamud.Framework.Run(DoJoin).ConfigureAwait(false);
            }).SafeFireAndForget(ex => this.logger.Error(ex.ToString()));
        }
        else
        {
            // One-shot Login subscription: detach inside the handler so a
            // later normal login doesn't re-fire the resume.
            void OnLoginOnce()
            {
                this.dalamud.ClientState.Login -= OnLoginOnce;
                Task.Run(async () =>
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                    await this.dalamud.Framework.Run(DoJoin).ConfigureAwait(false);
                }).SafeFireAndForget(ex => this.logger.Error(ex.ToString()));
            }
            this.dalamud.ClientState.Login += OnLoginOnce;
        }
    }

    private void ClearResumeState()
    {
        this.configuration.ResumeRoomKind = string.Empty;
        this.configuration.ResumeRoomName = string.Empty;
        this.configuration.ResumeRoomPassword = string.Empty;
        this.configuration.ResumeProcessId = 0;
        this.configuration.ResumeProcessStartTimeTicks = 0;
    }

    /// <summary>
    /// Returns <c>(processId, startTimeUtcTicks)</c> for the running game
    /// process. The start-time component defeats PID recycling across OS
    /// reboots — two different game launches can share the same PID but not
    /// the same start time.
    /// </summary>
    private static (long processId, long startTimeUtcTicks) GetCurrentProcessFingerprint()
    {
        try
        {
            using var proc = Process.GetCurrentProcess();
            return (proc.Id, proc.StartTime.ToUniversalTime().Ticks);
        }
        catch
        {
            // If for any reason we can't read process info, fall back to
            // sentinel values that will never match a saved fingerprint and
            // therefore safely suppress the resume.
            return (0, 0);
        }
    }

    public void JoinPublicVoiceRoom()
    {
        if (this.ShouldBeInRoom)
        {
            this.logger.Error("Already should be in voice room, ignoring public room join request.");
            return;
        }
        // Flag the session kind so Dispose can write the right resume entry.
        this.currentSessionIsPublic = true;
        string roomName = this.mapManager.GetCurrentMapPublicRoomName();
        string[]? otherPlayers = this.mapManager.InSharedWorldMap() ? null : GetOtherPlayerNamesInInstance().ToArray();
        JoinVoiceRoom(roomName, string.Empty, otherPlayers);
        this.mapManager.OnMapChanged += ReconnectToCurrentMapPublicRoom;
    }

    public void JoinPrivateVoiceRoom(string roomName, string roomPassword, bool listed = true, string? currentWorld = null)
    {
        if (this.ShouldBeInRoom)
        {
            this.logger.Error("Already should be in voice room, ignoring private room join request.");
            return;
        }
        this.currentSessionIsPublic = false;
        // v4: cache the listed/world parts of the join params so the retry
        // loop replays them. lastListed / lastCurrentWorld pair with the
        // lastRoomName / lastRoomPassword fields already cached in
        // JoinVoiceRoom below.
        this.lastListed = listed;
        this.lastCurrentWorld = currentWorld;
        JoinVoiceRoom(roomName, roomPassword, null);
    }

    public Task LeaveVoiceRoom(bool autoRejoin)
    {
        if (!autoRejoin)
        {
            // Explicit leave (Leave button, logout, or final give-up after a
            // failed retry loop). Cancel any in-flight reconnect and discard
            // the cached join params so an immediate involuntary disconnect
            // can't replay them.
            CancelReconnect();
            this.lastRoomName = null;
            this.lastRoomPassword = null;
            this.lastPlayersInInstance = null;

            this.ShouldBeInRoom = false;
            this.mapManager.OnMapChanged -= ReconnectToCurrentMapPublicRoom;
        }

        if (!this.InRoom)
        {
            return Task.CompletedTask;
        }

        this.logger.Debug("Attempting to leave voice room.");

        this.InRoom = false;
        this.localPlayerFullName = null;

        // Mute state is session-scoped on the server. Clear the admin-visible
        // dictionary AND the local-target flag so leftover state from this
        // room doesn't leak into the next one.
        this.GloballyMutedPeers.Clear();
        this.configuration.IsLocallyGlobalMuted = false;
        this.CurrentRoomMeta = null;

        this.audioDeviceController.AudioRecordingIsRequested = false;
        this.audioDeviceController.OnAudioRecordingSourceDataAvailable -= SendAudioFrameToServer;
        if (this.SignalingChannel != null)
        {
            this.SignalingChannel.OnAudioFrame -= OnAudioFrameReceivedTcp;
            this.SignalingChannel.OnUdpCredentialsReceived -= OnUdpCredentialsReceived;
        }
        // Tear down the v3 UDP channel for this session. Detach the audio
        // event first so a stale in-flight datagram can't fire into a
        // disposed pipeline.
        if (this.UdpAudioChannel != null)
        {
            try { this.UdpAudioChannel.OnAudioFrame -= OnAudioFrameReceivedUdp; } catch { /* nothing to do */ }
            try { this.UdpAudioChannel.Dispose(); } catch (Exception ex) { this.logger.Debug("UDP channel dispose threw: {0}", ex.Message); }
            this.UdpAudioChannel = null;
        }
        ActiveAudioTransport = AudioTransport.None;
        AudioTransportReason = null;
        // Drop per-peer seq tracking — a peer who rejoins next session will
        // start fresh from their first new packet's seq.
        this.lastUdpSenderSeqByPeer.Clear();
        this.opusCodec?.Dispose();
        this.opusCodec = null;

        if (this.Presence != null)
        {
            this.Presence.OnPeerAdded -= OnPeerAdded;
            this.Presence.OnPeerRemoved -= OnPeerRemoved;
            this.Presence.Dispose();
            this.Presence = null;
        }

        if (this.configuration.PlayRoomJoinAndLeaveSounds)
        {
            this.audioDeviceController.PlaySfx(this.roomSelfLeaveSound)
                .ContinueWith(task => this.audioDeviceController.AudioPlaybackIsRequested = false, TaskContinuationOptions.OnlyOnRanToCompletion)
                .SafeFireAndForget(ex =>
                {
                    if (ex is not TaskCanceledException) { this.logger.Error(ex.ToString()); }
                });
        }
        else
        {
            this.audioDeviceController.AudioPlaybackIsRequested = false;
        }

        if (this.SignalingChannel != null)
        {
            this.SignalingChannel.OnConnected -= OnSignalingServerConnected;
            this.SignalingChannel.OnReady -= OnSignalingServerReady;
            this.SignalingChannel.OnDisconnected -= OnSignalingServerDisconnected;
            this.SignalingChannel.OnErrored -= OnSignalingServerErrored;
            this.SignalingChannel.OnPremiumStatus -= OnPremiumStatusReceived;
            this.SignalingChannel.OnAdminStatus -= OnAdminStatusReceived;
            this.SignalingChannel.OnMuteState -= OnMuteStateReceived;
            this.SignalingChannel.OnRoomMeta -= OnRoomMetaReceived;
            return this.SignalingChannel.DisconnectAsync();
        }
        else
        {
            return Task.CompletedTask;
        }
    }

    public void PushPlayerAudioState()
    {
        this.configuration.SyncActiveCharacterProfile();
        if (this.SignalingChannel == null || !this.SignalingChannel.Connected)
        {
            return;
        }

        ushort audioState = 0;
        if (this.audioDeviceController.MuteMic || this.audioDeviceController.PlayingBackMicAudio)
        {
            audioState |= (ushort)Peer.AudioStateFlags.MicMuted;
        }
        if (this.audioDeviceController.Deafen || this.audioDeviceController.PlayingBackMicAudio)
        {
            audioState |= (ushort)Peer.AudioStateFlags.Deafened;
        }
        this.logger.Trace("Pushing player audio state: {0}", audioState);
        this.SignalingChannel.SendAsync(new SignalMessage.SignalPayload
        {
            action = "update",
            connections = [ new SignalMessage.SignalPayload.Connection
            {
                peerId = this.SignalingChannel.PeerId,
                audioState = audioState,
                pronoun = this.configuration.UserPronoun,
                biography = this.configuration.UserBiography,
                status = this.configuration.UserStatus,
                color = this.configuration.UserColor,
                twitchLink = this.configuration.TwitchLink,
            }],
        }).SafeFireAndForget(ex => this.logger.Error(ex.ToString()));
    }

    private void OnLogout(int type, int code)
    {
        LeaveVoiceRoom(false);
    }

    private IEnumerable<string> GetOtherPlayerNamesInInstance()
    {
        return this.dalamud.ObjectTable.GetPlayers()
            .Select(p => p.GetPlayerFullName())
            .Where(s => s != null)
            .Where(s => s != this.dalamud.PlayerState.GetLocalPlayerFullName())
            .Cast<string>();
    }

    private void JoinVoiceRoom(string roomName, string roomPassword, string[]? playersInInstance)
    {
        if (this.InRoom)
        {
            this.logger.Error("Already in voice room, ignoring join request.");
            return;
        }

        // Cache the join params so the auto-reconnect loop (HandleInvoluntaryDrop
        // → ScheduleReconnect) can replay this exact join after a socket drop.
        // Cleared in LeaveVoiceRoom(false) so explicit leaves can't be replayed.
        this.lastRoomName = roomName;
        this.lastRoomPassword = roomPassword;
        this.lastPlayersInInstance = playersInInstance;

        this.logger.Debug("Attempting to join voice room.");

        var playerName = this.dalamud.PlayerState.GetLocalPlayerFullName();
        if (playerName == null)
        {
#if DEBUG
            playerName = "testPeer14";
            this.logger.Warn("Player name is null. Setting it to {0} for debugging.", playerName);
#else
            this.logger.Error("Player name is null, cannot join voice room.");
            return;
#endif
        }

        this.InRoom = true;
        this.ShouldBeInRoom = true;
        this.localPlayerFullName = playerName;

        this.logger.Trace("Creating SignalingChannel class with peerId {0}", playerName);
        if (this.SignalingChannel == null)
        {
            this.SignalingChannel = new SignalingChannel(playerName,
                PeerType,
                EmbeddedConfig.SignalingServerUrl,
                EmbeddedConfig.SignalingServerToken,
                this.logger,
                true);
        }
        else
        {
            this.SignalingChannel.PeerId = playerName;
        }
        // Always refresh from configuration just before connecting so a token
        // saved or cleared since the channel was constructed is reflected on
        // the next ready emit.
        this.SignalingChannel.PremiumToken = this.configuration.PremiumToken ?? string.Empty;

        // v2: presence comes from signaling messages, not WebRTC peer connections.
        this.Presence ??= new PeerPresenceManager(this.SignalingChannel, this.logger);

        this.SignalingChannel.OnConnected += OnSignalingServerConnected;
        this.SignalingChannel.OnReady += OnSignalingServerReady;
        this.SignalingChannel.OnDisconnected += OnSignalingServerDisconnected;
        this.SignalingChannel.OnErrored += OnSignalingServerErrored;
        this.SignalingChannel.OnPremiumStatus += OnPremiumStatusReceived;
        this.SignalingChannel.OnAdminStatus += OnAdminStatusReceived;
        this.SignalingChannel.OnMuteState += OnMuteStateReceived;
        this.SignalingChannel.OnRoomMeta += OnRoomMetaReceived;
        this.Presence.OnPeerAdded += OnPeerAdded;
        this.Presence.OnPeerRemoved += OnPeerRemoved;

        this.logger.Debug("Attempting to connect to signaling channel.");
        this.SignalingChannel.ConnectAsync(
            roomName,
            roomPassword,
            playersInInstance,
            this.lastListed,
            this.lastCurrentWorld).SafeFireAndForget(ex =>
        {
            if (ex is not OperationCanceledException)
            {
                this.logger.Error(ex.ToString());
            }
        });
    }

    private void ReconnectToCurrentMapPublicRoom()
    {
        if (this.ShouldBeInRoom &&
            (!this.InRoom || this.SignalingChannel?.RoomName != this.mapManager.GetCurrentMapPublicRoomName()))
        {
            Task.Run(async () =>
            {
                await this.LeaveVoiceRoom(true);
                // Add an arbitrary delay here as loading a new map can result in a null local player name during load.
                // This delay hopefully allows the game to populate that field before a reconnection attempt happens.
                // Also in some housing districts, the mapId is different after the OnTerritoryChanged event
                await Task.Delay(1000);
                // Accessing the object table must happen on the main thread
                this.dalamud.Framework.Run(() =>
                {
                    var roomName = this.mapManager.GetCurrentMapPublicRoomName();
                    string[]? otherPlayers = this.mapManager.InSharedWorldMap() ? null : GetOtherPlayerNamesInInstance().ToArray();
                    this.JoinVoiceRoom(roomName, string.Empty, otherPlayers);
                }).SafeFireAndForget(ex => this.logger.Error(ex.ToString()));
            });
        }
    }

    private void OnSignalingServerConnected()
    {
        this.audioDeviceController.AudioRecordingIsRequested = true;
        this.audioDeviceController.AudioPlaybackIsRequested = true;
        // Fresh codec per room session — Opus encoder/decoder state must start clean.
        this.opusCodec?.Dispose();
        this.opusCodec = new OpusCodec(this.logger);

        // Defensive subscribe pattern (-= then +=) to guarantee exactly-one
        // subscription regardless of how many times this handler fires. Without
        // this guard, a race between the SocketIOClient library's own auto-
        // reconnect (Reconnection=true in SignalingChannel) and our
        // ScheduleReconnect loop can let OnSignalingServerConnected run twice
        // before LeaveVoiceRoom(autoRejoin: true) has had a chance to
        // unsubscribe — and a double-subscribed SendAudioFrameToServer
        // transmits each captured 20 ms frame twice on the wire, which is
        // exactly the "peer hears me twice + robotic" symptom (the receiver
        // decodes two sequential frames with identical PCM and the Opus
        // encoder's stateful predictor fights itself between them). C#'s
        // event API treats -= on an unsubscribed handler as a no-op, so this
        // is safe on the first connect too.
        this.audioDeviceController.OnAudioRecordingSourceDataAvailable -= SendAudioFrameToServer;
        this.audioDeviceController.OnAudioRecordingSourceDataAvailable += SendAudioFrameToServer;
        if (this.SignalingChannel != null)
        {
            this.SignalingChannel.OnAudioFrame -= OnAudioFrameReceivedTcp;
            this.SignalingChannel.OnAudioFrame += OnAudioFrameReceivedTcp;
            // v3: subscribe to the server's UDP credentials emission so we
            // can stand up the UDP audio channel after `ready` completes.
            // Same defensive -= / += pattern: this handler can fire on every
            // (re)connect and we must not stack handlers across sessions.
            this.SignalingChannel.OnUdpCredentialsReceived -= OnUdpCredentialsReceived;
            this.SignalingChannel.OnUdpCredentialsReceived += OnUdpCredentialsReceived;
        }

        // Start every session with transport state cleared. We default to
        // TCP — UDP will flip us over if/when the hello-handshake succeeds.
        ActiveAudioTransport = AudioTransport.Tcp;
        AudioTransportReason = "udp-not-yet-attempted";

        if (this.configuration.PlayRoomJoinAndLeaveSounds)
        {
            this.audioDeviceController.PlaySfx(this.roomJoinSound);
        }
    }

    /// <summary>
    /// Server delivered the per-session UDP credentials. Stand up the UDP
    /// audio channel and try the hello-handshake. On success, flip the
    /// send-side transport to UDP; on failure (timeout, network blocked),
    /// stay on the Socket.IO fallback that's already wired.
    /// </summary>
    private void OnUdpCredentialsReceived(UdpCredentials creds)
    {
        try
        {
            if (!this.configuration.PreferUdpAudio)
            {
                this.logger.Info("UDP audio disabled by user preference; staying on Socket.IO transport.");
                ActiveAudioTransport = AudioTransport.Tcp;
                AudioTransportReason = "user-disabled";
                return;
            }

            // Tear down any prior channel from a previous session before
            // creating a fresh one. Idempotent on the happy path.
            try { this.UdpAudioChannel?.Dispose(); } catch { /* nothing to do */ }
            this.UdpAudioChannel = null;

            UdpAudioChannel channel;
            try
            {
                channel = new UdpAudioChannel(creds, this.logger);
            }
            catch (Exception ex)
            {
                this.logger.Error("UDP channel construct failed: {0}", ex.Message);
                ActiveAudioTransport = AudioTransport.Tcp;
                AudioTransportReason = "construct-failed";
                return;
            }

            channel.OnAudioFrame += OnAudioFrameReceivedUdp;
            this.UdpAudioChannel = channel;

            // Run StartAsync in the background; the hello-handshake completes
            // in milliseconds on the happy path, up to HelloTimeoutMs on
            // failure. Audio is allowed to flow over TCP in the meantime.
            _ = Task.Run(async () =>
            {
                try
                {
                    await channel.StartAsync().ConfigureAwait(false);
                    if (channel.Ready)
                    {
                        ActiveAudioTransport = AudioTransport.Udp;
                        AudioTransportReason = null;
                        this.logger.Info("Audio transport: UDP (session={0})", creds.SessionIdHex);
                    }
                    else
                    {
                        var reason = channel.LastError is TimeoutException ? "hello-ack-timeout" : "udp-error";
                        ActiveAudioTransport = AudioTransport.Tcp;
                        AudioTransportReason = reason;
                        this.logger.Info("Audio transport: TCP fallback (reason={0})", reason);
                        // Tear down the dead UDP channel so subsequent
                        // session restarts don't see a stale one.
                        try { channel.Dispose(); } catch { /* nothing to do */ }
                        if (ReferenceEquals(this.UdpAudioChannel, channel)) this.UdpAudioChannel = null;
                    }
                }
                catch (Exception ex)
                {
                    this.logger.Error("UDP channel StartAsync threw: {0}", ex);
                    ActiveAudioTransport = AudioTransport.Tcp;
                    AudioTransportReason = "exception";
                    try { channel.Dispose(); } catch { /* nothing to do */ }
                    if (ReferenceEquals(this.UdpAudioChannel, channel)) this.UdpAudioChannel = null;
                }
            });
        }
        catch (Exception ex)
        {
            this.logger.Error("OnUdpCredentialsReceived failed: {0}", ex);
        }
    }

    private void OnSignalingServerReady()
    {
        PushPlayerAudioState();
    }

    private void OnPremiumStatusReceived(bool premium)
    {
        // Server is source of truth. Mirror into config so the lock state
        // in ConfigWindow flips immediately, and persist so the right state
        // is shown on next plugin load before any room join.
        if (this.configuration.IsLocalPremium == premium) return;
        this.configuration.IsLocalPremium = premium;
        this.configuration.Save();
    }

    private void OnAdminStatusReceived(bool isAdmin)
    {
        // Session-scoped: server re-confirms on every connect, so we don't
        // persist this. Just mirror into the runtime cache so MainWindow's
        // right-click menu knows whether to render the admin items.
        this.configuration.IsLocalAdmin = isAdmin;
    }

    /// <summary>
    /// v4 only. Cache the latest <c>roomMeta</c> snapshot pushed by the
    /// server. The server pushes these only to room authorities (owner +
    /// mods); non-authority peers never see one. Consumers
    /// (<c>MainWindow</c>) read <see cref="CurrentRoomMeta"/> each frame.
    /// </summary>
    private void OnRoomMetaReceived(WebRTC.SignalingChannel.RoomMetaSnapshot snapshot)
    {
        this.CurrentRoomMeta = snapshot;
    }

    private void OnMuteStateReceived(string peerId, bool muted, bool isSelf)
    {
        // The server only emits muteState to admins in the same room AND to
        // the muted target themselves. We treat the two cases distinctly:
        //   • self  → drive the "you are globally muted" banner on the main
        //             window. No roster-indicator change (the local player
        //             row doesn't render its own indicator).
        //   • other → admin client only; populate the roster dictionary so
        //             DrawPeerRow can render the muted-by-admin icon.
        if (isSelf)
        {
            this.configuration.IsLocallyGlobalMuted = muted;
            return;
        }
        if (muted)
        {
            this.GloballyMutedPeers[peerId] = true;
        }
        else
        {
            this.GloballyMutedPeers.Remove(peerId);
        }
    }

    private void OnSignalingServerDisconnected() => HandleInvoluntaryDrop();

    private void OnSignalingServerErrored()
    {
        // Errored sockets can't be reused — drop the SignalingChannel entirely
        // so the retry loop builds a fresh one on the next JoinVoiceRoom call.
        this.SignalingChannel?.Dispose();
        this.SignalingChannel = null;
        HandleInvoluntaryDrop();
    }

    /// <summary>
    /// Called when the socket dropped on its own (not via the user's Leave
    /// button or a logout). If the user still wants to be in a room, schedule
    /// up to <see cref="ReconnectMaxAttempts"/> retries spaced
    /// <see cref="ReconnectDelay"/> apart. Otherwise fall back to a normal
    /// teardown.
    /// </summary>
    private void HandleInvoluntaryDrop()
    {
        // A retry loop is already in flight — let it own the recovery. Without
        // this guard, every failed mid-loop connect would restart the counter
        // and we'd never give up.
        if (this.IsReconnecting) return;

        if (!this.ShouldBeInRoom || this.lastRoomName == null)
        {
            LeaveVoiceRoom(false).SafeFireAndForget(ex => this.logger.Error(ex.ToString()));
            return;
        }

        // If the server gave us a structured error code, decide whether it's
        // worth retrying. The auto-reconnect loop is there for transient
        // network failures, not for "your room name is banned" / "the room is
        // full" / "you're banned from this room" — those would re-fail on
        // every retry with the same parameters and just spam the server while
        // leaving the plugin's UI stuck in a fake "in-room" state during the
        // attempts. Treat any *known* error code as permanent and fall
        // through to a clean leave; only Unknown (or no error at all) is
        // assumed transient.
        var latestError = this.SignalingChannel?.LatestError;
        if (latestError is SignalingChannelError code && code != SignalingChannelError.Unknown)
        {
            this.logger.Info("Server rejected the join ({0}); skipping auto-reconnect.", code);
            LeaveVoiceRoom(false).SafeFireAndForget(ex => this.logger.Error(ex.ToString()));
            return;
        }

        ScheduleReconnect();
    }

    private void ScheduleReconnect()
    {
        CancelReconnect();
        var cts = new CancellationTokenSource();
        this.reconnectCts = cts;
        this.IsReconnecting = true;
        this.ReconnectAttempt = 0;

        var roomName = this.lastRoomName;
        var roomPassword = this.lastRoomPassword ?? string.Empty;
        var players = this.lastPlayersInInstance;

        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 1; i <= ReconnectMaxAttempts; i++)
                {
                    if (cts.IsCancellationRequested) return;
                    this.ReconnectAttempt = i;
                    this.logger.Info("Signaling reconnect attempt {0}/{1}", i, ReconnectMaxAttempts);

                    // Tear the prior session down without dropping ShouldBeInRoom.
                    // Necessary on both the initial drop and between failed attempts:
                    // JoinVoiceRoom refuses to run while InRoom == true.
                    await LeaveVoiceRoom(autoRejoin: true).ConfigureAwait(false);
                    if (cts.IsCancellationRequested) return;

                    // JoinVoiceRoom must run on the main thread because it touches
                    // Dalamud's PlayerState (player name lookup). Matches the
                    // pattern used by ReconnectToCurrentMapPublicRoom.
                    await this.dalamud.Framework.Run(() =>
                    {
                        if (cts.IsCancellationRequested) return;
                        if (roomName == null) return;
                        JoinVoiceRoom(roomName, roomPassword, players);
                    }).ConfigureAwait(false);

                    // Wait the retry interval to see whether OnConnected fires.
                    // If the socket comes up in this window, we're done; otherwise
                    // the next iteration tears down and tries again.
                    try { await Task.Delay(ReconnectDelay, cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }

                    if (this.SignalingChannel?.Connected == true)
                    {
                        this.logger.Info("Signaling reconnect succeeded on attempt {0}", i);
                        return;
                    }
                }

                // All attempts failed — give up and fall through to a normal leave
                // so ShouldBeInRoom clears and the UI returns to "Not connected".
                this.logger.Warn("Signaling reconnect gave up after {0} attempts", ReconnectMaxAttempts);
                await LeaveVoiceRoom(autoRejoin: false).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* user left or logged out — silent */ }
            catch (Exception ex)
            {
                this.logger.Error(ex.ToString());
            }
            finally
            {
                ClearReconnectState();
            }
        });
    }

    private void CancelReconnect()
    {
        try
        {
            this.reconnectCts?.Cancel();
            this.reconnectCts?.Dispose();
        }
        catch { /* nothing to do */ }
        this.reconnectCts = null;
    }

    private void ClearReconnectState()
    {
        this.IsReconnecting = false;
        this.ReconnectAttempt = 0;
    }

    /// <summary>
    /// A remote peer entered the room (presence-only — no WebRTC negotiation in v2).
    /// Wire up the playback channel + tracked-player record so audio frames have somewhere to land.
    /// </summary>
    /// <param name="isNewArrival">true if this peer just joined an active room; false if we are
    /// the new joiner and this peer was already here. Drives whether to play the join sfx.</param>
    private void OnPeerAdded(Peer peer, bool isNewArrival)
    {
        this.audioDeviceController.CreateAudioPlaybackChannel(peer.PeerId);
        if (!this.TrackedPlayers.ContainsKey(peer.PeerId))
        {
            this.TrackedPlayers.Add(peer.PeerId, new TrackedPlayer());
        }
        if (isNewArrival && this.configuration.PlayRoomJoinAndLeaveSounds)
        {
            this.audioDeviceController.PlaySfx(this.roomJoinSound);
        }
    }

    private void OnPeerRemoved(Peer peer)
    {
        this.opusCodec?.RemovePeer(peer.PeerId);
        this.audioDeviceController.RemoveAudioPlaybackChannel(peer.PeerId);
        this.TrackedPlayers.Remove(peer.PeerId);
        if (this.configuration.PlayRoomJoinAndLeaveSounds)
        {
            this.audioDeviceController.PlaySfx(this.roomOtherLeaveSound);
        }
    }

    /// <summary>
    /// Audio send path: encode one captured 20 ms PCM frame as Opus and
    /// emit it to the signaling server, which fans it out to every other
    /// peer in the room. Routes via UDP when the v3 channel is ready;
    /// falls back to Socket.IO otherwise. Both transports converge at the
    /// server — peers receive on whichever transport the server picks for
    /// them, so the sender's choice is independent of the receivers'.
    /// </summary>
    private void SendAudioFrameToServer(object? sender, WaveInEventArgs e)
    {
        try
        {
            if (this.audioDeviceController.PlayingBackMicAudio) return;
            if (this.opusCodec == null) return;
            if (this.SignalingChannel == null || !this.SignalingChannel.Connected) return;

            var packet = this.opusCodec.Encode(e.Buffer, e.BytesRecorded);
            if (packet == null)
            {
                // Encoder declined (e.g. short frame at start/stop of recording) — skip silently.
                return;
            }

            // Prefer UDP when the v3 channel is up. Send is fire-and-forget
            // on both paths so a slow socket can't backpressure the capture
            // thread; the UDP path drops on EWOULDBLOCK rather than queueing.
            var udp = this.UdpAudioChannel;
            if (udp != null && udp.Ready)
            {
                udp.SendAudioFrame(packet);
            }
            else
            {
                this.SignalingChannel.EmitAudioAsync(packet)
                    .SafeFireAndForget(ex => this.logger.Error(ex.ToString()));
            }
        }
        catch (Exception ex)
        {
            this.logger.Error(ex.ToString());
        }
    }

    /// <summary>
    /// Per-peer last seen sender sequence number. UDP path only — the
    /// Socket.IO transport doesn't carry sender sequence numbers so we
    /// can't detect gaps there (and TCP doesn't lose packets anyway). On
    /// a one-frame gap we use Opus FEC to recover the missing frame; on
    /// reorder / duplicate we just drop the late-arriving packet.
    /// </summary>
    private readonly Dictionary<string, uint> lastUdpSenderSeqByPeer = new(StringComparer.Ordinal);

    /// <summary>
    /// Audio receive path called for frames that arrived on the Socket.IO
    /// transport. Tags the frame as TCP-delivered for the jitter buffer's
    /// adaptive depth. No FEC recovery (no sender-seq available, and TCP
    /// guarantees ordered delivery so gaps don't exist).
    /// </summary>
    private void OnAudioFrameReceivedTcp(string fromPeerId, byte[] opusPacket)
    {
        if (!TryValidateAudioSender(fromPeerId)) return;
        DecodeAndPlay(fromPeerId, opusPacket, recoverPreviousViaFec: false, fromUdp: false);
    }

    /// <summary>
    /// Audio receive path called for frames that arrived on the UDP
    /// transport (decrypted + framing-validated upstream in
    /// <see cref="UdpAudioChannel"/>). Uses <paramref name="senderSeq"/>
    /// to detect one-frame gaps and recover the missing frame via Opus
    /// FEC before decoding the current one.
    /// </summary>
    private void OnAudioFrameReceivedUdp(string fromPeerId, uint senderSeq, byte[] opusPacket)
    {
        if (!TryValidateAudioSender(fromPeerId)) return;

        // Per-peer seq tracking. On the first packet from a peer the dict
        // entry doesn't exist; treat that as no gap (just record the seq).
        bool recoverPreviousViaFec = false;
        if (this.lastUdpSenderSeqByPeer.TryGetValue(fromPeerId, out var lastSeq))
        {
            // Wrap-safe comparison: subtract as int32. If the new seq is
            // <= lastSeq (delta ≤ 0), drop as reorder / duplicate.
            var delta = (int)(senderSeq - lastSeq);
            if (delta <= 0) return;
            if (delta == 2) recoverPreviousViaFec = true;        // exactly one missing
            // delta > 2: more than one frame lost. FEC can only recover the
            // immediately-previous frame; older missing frames are gone.
            // Still try FEC for the most recent missing one.
            else if (delta > 2) recoverPreviousViaFec = true;
        }
        this.lastUdpSenderSeqByPeer[fromPeerId] = senderSeq;

        DecodeAndPlay(fromPeerId, opusPacket, recoverPreviousViaFec, fromUdp: true);
    }

    /// <summary>
    /// Defense-in-depth: reject frames from peerIds the server hasn't
    /// announced. A malicious in-room peer can spoof `from` in audio
    /// emits (server pins it to its socket-bound peerId, but that
    /// peerId could still be valid-yet-unexpected — e.g. a peer left
    /// and an attacker rejoined under their name). Without this gate
    /// the plugin would auto-create a playback channel for any string,
    /// unbounded.
    /// </summary>
    private bool TryValidateAudioSender(string fromPeerId)
    {
        var isTrustedAdminAudio = IsTrustedAdminAudioPeerId(fromPeerId);
        if (isTrustedAdminAudio) return true;
        if (!PeerPresenceManager.IsValidPeerId(fromPeerId)) return false;
        if (this.Presence == null || !this.Presence.IsKnownPeer(fromPeerId)) return false;
        return true;
    }

    /// <summary>
    /// Decode (with optional FEC recovery of the prior frame) and hand
    /// the resulting PCM to the per-peer playback channel.
    /// </summary>
    private void DecodeAndPlay(string fromPeerId, byte[] opusPacket, bool recoverPreviousViaFec, bool fromUdp)
    {
        try
        {
            if (this.opusCodec == null) return;

            // Lazily ensure the playback channel exists. Membership has
            // already been verified upstream.
            this.audioDeviceController.CreateAudioPlaybackChannel(fromPeerId);

            // FEC recovery: when there's a detected one-or-more frame gap,
            // pull the redundant copy of the immediately-prior frame out of
            // this packet's FEC bits. Emit it BEFORE the current frame so
            // playback ordering matches the wall-clock ordering of the
            // sender's mic. If the FEC payload is absent (encoder didn't
            // include one) the recovery returns null and we just play the
            // current frame normally.
            if (recoverPreviousViaFec)
            {
                var recovered = this.opusCodec.DecodeFecRecovered(fromPeerId, opusPacket);
                if (recovered != null)
                {
                    this.audioDeviceController.AddPlaybackSample(
                        fromPeerId,
                        new WaveInEventArgs(recovered, recovered.Length),
                        fromUdp);
                }
            }

            var pcm = this.opusCodec.Decode(fromPeerId, opusPacket);
            if (pcm == null) return;
            this.audioDeviceController.AddPlaybackSample(
                fromPeerId,
                new WaveInEventArgs(pcm, pcm.Length),
                fromUdp);
        }
        catch (Exception ex)
        {
            this.logger.Error(ex.ToString());
        }
    }

    private static bool IsTrustedAdminAudioPeerId(string peerId)
    {
        return !string.IsNullOrWhiteSpace(peerId)
               && peerId.StartsWith(AdminAudioPeerPrefix, StringComparison.Ordinal)
               && peerId.Length <= 64;
    }
}
