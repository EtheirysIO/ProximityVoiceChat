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
using System.Collections.Concurrent;
using System.Collections.Generic;
using EtheirysProximityVoiceChat.Log;
using EtheirysProximityVoiceChat.WebRTC;
using SocketIOClient;

namespace EtheirysProximityVoiceChat;

/// <summary>
/// Tracks remote peers using signaling-server presence messages
/// ("open" / "close" / "update"). Replaces WebRTCManager in v2: there are
/// no per-peer connections, no SDP/ICE handshake, no TURN. Audio is
/// server-relayed; this class only maintains the membership list so the
/// UI, spatializer, and audio device controller know who is in the room.
/// </summary>
public sealed class PeerPresenceManager : IDisposable
{
    public IReadOnlyDictionary<string, Peer> Peers => this.peers;

    /// <summary>
    /// Fires when a remote peer enters the room.
    /// The bool is `isNewArrival`: true when this peer just joined an already-active
    /// room (server sets "bePolite: true" on the "open" broadcast), false when this
    /// peer was already in the room and the local client is the one who just joined
    /// (server delivers the existing-roster "open" with "bePolite: false").
    /// Subscribers use this to decide whether to play the join sfx.
    /// </summary>
    public event Action<Peer, bool>? OnPeerAdded;
    public event Action<Peer>? OnPeerRemoved;

    private readonly ConcurrentDictionary<string, Peer> peers = new();
    private readonly SignalingChannel signaling;
    private readonly ILogger logger;
    private bool disposed;

    // FFXIV character names are at most ~21 chars ("FirstName LastName"), plus
    // "@WorldName" of up to ~12 chars, giving a real-world worst case of ~34.
    // 64 is comfortable headroom and matches the server-side cap.
    private const int MaxPeerIdLength = 64;

    /// <summary>
    /// Validates a peerId before it enters the local presence map. Defends
    /// against a hostile in-room peer injecting arbitrary peerIds via crafted
    /// "open" / "update" messages — without this, the relayed `connections[]`
    /// array is fully attacker-controlled. Server-side filtering also exists
    /// (see SignalingServer/src/server.js filterPayloadConnections), but the
    /// plugin keeps its own gate for defense-in-depth.
    /// </summary>
    public static bool IsValidPeerId(string? peerId)
    {
        if (string.IsNullOrEmpty(peerId)) return false;
        if (peerId.Length > MaxPeerIdLength) return false;
        // FFXIV full names look like "Firstname Lastname@World".
        // Allow letters/digits and the small punctuation set actually seen in names.
        foreach (var ch in peerId)
        {
            if (char.IsLetterOrDigit(ch)) continue;
            if (ch == ' ' || ch == '\'' || ch == '-' || ch == '@' || ch == '.') continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// True when the server has told us this peerId is in the room.
    /// Used by VoiceRoomManager to drop audio frames from unlisted peers.
    /// </summary>
    public bool IsKnownPeer(string peerId) => this.peers.ContainsKey(peerId);

    public PeerPresenceManager(SignalingChannel signaling, ILogger logger)
    {
        this.signaling = signaling;
        this.logger = logger;
        this.signaling.OnMessage += OnSignalingMessage;
    }

    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;
        this.signaling.OnMessage -= OnSignalingMessage;
        // Tear down listeners' state for any peer we still hold.
        foreach (var peer in this.peers.Values)
        {
            try { OnPeerRemoved?.Invoke(peer); }
            catch (Exception ex) { this.logger.Error("OnPeerRemoved threw: {0}", ex); }
        }
        this.peers.Clear();
        this.OnPeerAdded = null;
        this.OnPeerRemoved = null;
    }

    private void OnSignalingMessage(SocketIOResponse response)
    {
        SignalMessage msg;
        try { msg = response.GetValue<SignalMessage>(); }
        catch (Exception ex) { this.logger.Error("Could not parse signal message: {0}", ex); return; }

        var action = msg.payload.action;
        if (string.IsNullOrEmpty(action)) return;

        switch (action)
        {
            case "open":
                HandleOpen(msg);
                break;
            case "close":
                HandleClose(msg);
                break;
            case "update":
                HandleUpdate(msg);
                break;
            // "sdp" / "ice" are v1 WebRTC handshake actions. No v2 client sends them
            // and we ignore any stragglers from old peers (those are rejected at
            // the auth handshake by the server's protocol-version check, so this
            // branch should never fire in practice).
        }
    }

    private void HandleOpen(SignalMessage msg)
    {
        var conns = msg.payload.connections;
        if (conns == null) return;
        var isNewArrival = msg.payload.bePolite ?? false;
        foreach (var c in conns)
        {
            if (!IsValidPeerId(c.peerId))
            {
                this.logger.Warn("Rejected peer with invalid peerId in 'open' message");
                continue;
            }
            if (c.peerId == this.signaling.PeerId) continue; // ignore self
            var peer = new Peer
            {
                PeerId = c.peerId,
                AudioState = (Peer.AudioStateFlags)c.audioState,
                IsPremium = c.isPremium,
                Pronoun = c.pronoun ?? "Unset",
                Biography = c.biography ?? string.Empty,
                Status = c.status ?? string.Empty,
                Color = c.color ?? "FFFFFF",
                TwitchLink = c.twitchLink ?? string.Empty,
            };
            if (this.peers.TryAdd(c.peerId, peer))
            {
                this.logger.Debug("Peer added: {0} (newArrival={1})", c.peerId, isNewArrival);
                try { OnPeerAdded?.Invoke(peer, isNewArrival); }
                catch (Exception ex) { this.logger.Error("OnPeerAdded threw: {0}", ex); }
            }
        }
    }

    private void HandleClose(SignalMessage msg)
    {
        var who = msg.from;
        if (!IsValidPeerId(who)) return;
        if (this.peers.TryRemove(who, out var leftPeer))
        {
            this.logger.Debug("Peer removed: {0}", who);
            try { OnPeerRemoved?.Invoke(leftPeer); }
            catch (Exception ex) { this.logger.Error("OnPeerRemoved threw: {0}", ex); }
        }
    }

    private void HandleUpdate(SignalMessage msg)
    {
        var conns = msg.payload.connections;
        if (conns == null) return;
        foreach (var c in conns)
        {
            if (!IsValidPeerId(c.peerId)) continue;
            // Only update peers we already know about. An "update" claiming a
            // brand-new peerId is either a malformed message or an injection
            // attempt — either way, don't auto-add. Real arrivals come through
            // "open" first.
            if (this.peers.TryGetValue(c.peerId, out var p))
            {
                p.AudioState = (Peer.AudioStateFlags)c.audioState;
                p.IsPremium = c.isPremium;
                if (c.pronoun != null) p.Pronoun = c.pronoun;
                if (c.biography != null) p.Biography = c.biography;
                if (c.status != null) p.Status = c.status;
                if (c.color != null) p.Color = c.color;
                if (c.twitchLink != null) p.TwitchLink = c.twitchLink;
            }
        }
    }
}
