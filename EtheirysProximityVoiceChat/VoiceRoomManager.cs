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
    }

    public void Dispose()
    {
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

    public void JoinPublicVoiceRoom()
    {
        if (this.ShouldBeInRoom)
        {
            this.logger.Error("Already should be in voice room, ignoring public room join request.");
            return;
        }
        string roomName = this.mapManager.GetCurrentMapPublicRoomName();
        string[]? otherPlayers = this.mapManager.InSharedWorldMap() ? null : GetOtherPlayerNamesInInstance().ToArray();
        JoinVoiceRoom(roomName, string.Empty, otherPlayers);
        this.mapManager.OnMapChanged += ReconnectToCurrentMapPublicRoom;
    }

    public void JoinPrivateVoiceRoom(string roomName, string roomPassword)
    {
        if (this.ShouldBeInRoom)
        {
            this.logger.Error("Already should be in voice room, ignoring private room join request.");
            return;
        }
        JoinVoiceRoom(roomName, roomPassword, null);
    }

    public Task LeaveVoiceRoom(bool autoRejoin)
    {
        if (!autoRejoin)
        {
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

        this.audioDeviceController.AudioRecordingIsRequested = false;
        this.audioDeviceController.OnAudioRecordingSourceDataAvailable -= SendAudioFrameToServer;
        if (this.SignalingChannel != null)
        {
            this.SignalingChannel.OnAudioFrame -= OnAudioFrameReceived;
        }
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
            this.SignalingChannel.OnErrored -= OnSignalingServerDisconnected;
            this.SignalingChannel.OnPremiumStatus -= OnPremiumStatusReceived;
            this.SignalingChannel.OnAdminStatus -= OnAdminStatusReceived;
            this.SignalingChannel.OnMuteState -= OnMuteStateReceived;
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
        this.Presence.OnPeerAdded += OnPeerAdded;
        this.Presence.OnPeerRemoved += OnPeerRemoved;

        this.logger.Debug("Attempting to connect to signaling channel.");
        this.SignalingChannel.ConnectAsync(roomName, roomPassword, playersInInstance).SafeFireAndForget(ex =>
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
        this.audioDeviceController.OnAudioRecordingSourceDataAvailable += SendAudioFrameToServer;
        if (this.SignalingChannel != null)
        {
            this.SignalingChannel.OnAudioFrame += OnAudioFrameReceived;
        }
        if (this.configuration.PlayRoomJoinAndLeaveSounds)
        {
            this.audioDeviceController.PlaySfx(this.roomJoinSound);
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

    private void OnSignalingServerDisconnected()
    {
        LeaveVoiceRoom(false).SafeFireAndForget(ex => this.logger.Error(ex.ToString()));
    }

    private void OnSignalingServerErrored()
    {
        LeaveVoiceRoom(false).SafeFireAndForget(ex => this.logger.Error(ex.ToString()));
        this.SignalingChannel?.Dispose();
        this.SignalingChannel = null;
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
    /// v2 audio send path: encode one captured 20 ms PCM frame as Opus and emit it
    /// to the signaling server, which fans it out to every other peer in the room.
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

            this.SignalingChannel.EmitAudioAsync(packet)
                .SafeFireAndForget(ex => this.logger.Error(ex.ToString()));
        }
        catch (Exception ex)
        {
            this.logger.Error(ex.ToString());
        }
    }

    /// <summary>
    /// v2 audio receive path: server delivered an Opus packet from another player.
    /// Decode and hand to the per-peer playback channel.
    /// </summary>
    private void OnAudioFrameReceived(string fromPeerId, byte[] opusPacket)
    {
        try
        {
            if (this.opusCodec == null) return;

            // Defense-in-depth: reject frames from peerIds the server hasn't
            // announced. A malicious in-room peer can spoof `from` in audio
            // emits (server pins it to its socket-bound peerId, but that
            // peerId could still be valid-yet-unexpected — e.g. a peer left
            // and an attacker rejoined under their name). Without this gate
            // the plugin would auto-create a playback channel for any string,
            // unbounded.
            var isTrustedAdminAudio = IsTrustedAdminAudioPeerId(fromPeerId);
            if (!isTrustedAdminAudio)
            {
                if (!PeerPresenceManager.IsValidPeerId(fromPeerId)) return;
                if (this.Presence == null || !this.Presence.IsKnownPeer(fromPeerId)) return;
            }

            var pcm = this.opusCodec.Decode(fromPeerId, opusPacket);
            if (pcm == null) return;

            // Lazily ensure the playback channel exists. Membership has
            // already been verified above, so this only creates channels for
            // peers the server told us about.
            this.audioDeviceController.CreateAudioPlaybackChannel(fromPeerId);
            this.audioDeviceController.AddPlaybackSample(fromPeerId, new WaveInEventArgs(pcm, pcm.Length));
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
