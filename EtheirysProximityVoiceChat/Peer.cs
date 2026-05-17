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

namespace EtheirysProximityVoiceChat;

/// <summary>
/// A remote participant tracked via signaling-server presence. In v2 this is
/// purely a presence record — there is no per-peer WebRTC connection.
/// </summary>
public sealed class Peer
{
    public required string PeerId { get; init; }
    public AudioStateFlags AudioState { get; set; }

    /// <summary>
    /// True when the server says this peer is currently linked to a Discord
    /// account holding the configured supporter role. Drives whether the
    /// profile section is shown for this peer. Server is the sole writer —
    /// the plugin never sets this from local state.
    /// </summary>
    public bool IsPremium { get; set; }

    /// <summary>
    /// User's pronoun preference. Up to 24 chars free-form when the peer is
    /// premium; empty / "Unset" otherwise.
    /// </summary>
    public string Pronoun { get; set; } = "Unset";

    /// <summary>
    /// User's biography/about me text.
    /// </summary>
    public string Biography { get; set; } = string.Empty;

    /// <summary>
    /// User's current status text.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// User's selected color as a hex string (e.g., "FF5733").
    /// </summary>
    public string Color { get; set; } = "FFFFFF";

    /// <summary>
    /// User's Twitch link (if set).
    /// </summary>
    public string TwitchLink { get; set; } = string.Empty;

    [Flags]
    public enum AudioStateFlags : ushort
    {
        Default  = 0,
        MicMuted = 1 << 0,
        Deafened = 1 << 1,
    }
}
