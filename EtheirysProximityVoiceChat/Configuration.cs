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
using Dalamud.Configuration;
using Dalamud.Plugin;
using EtheirysProximityVoiceChat.Extensions;
using EtheirysProximityVoiceChat.Input;
using NLog;
using System;
using System.Collections.Generic;

namespace EtheirysProximityVoiceChat;

[Serializable]
public class Configuration : IPluginConfiguration
{
    // Bumped to 1 in v1.1.4 when the legacy single-KeyCode keybind fields
    // (PushToTalkKeybind / MuteMicKeybind / DeafenKeybind) were dropped in
    // favour of the modifier-aware KeyBinding-typed properties below.
    public int Version { get; set; } = 1;

    // Saved UI inputs
    public bool PublicRoom { get; set; }
    public string RoomName { get; set; } = string.Empty;
    public string RoomPassword { get; set; } = string.Empty;

    public int SelectedAudioInputDeviceIndex { get; set; } = -1;
    public int SelectedAudioOutputDeviceIndex { get; set; } = -1;
    public bool PushToTalk { get; set; }
    public int PushToTalkReleaseDelayMs { get; set; } = 20;
    public bool SuppressNoise { get; set; } = true;

    public bool MuteMic { get; set; }
    public bool Deafen { get; set; }

    /// <summary>
    /// Push-to-talk hotkey including any Shift/Ctrl/Alt modifiers.
    /// </summary>
    public KeyBinding PushToTalkBinding { get; set; } = new();
    /// <summary>
    /// Mute-microphone toggle hotkey including any Shift/Ctrl/Alt modifiers.
    /// </summary>
    public KeyBinding MuteMicBinding { get; set; } = new();
    /// <summary>
    /// Deafen toggle hotkey including any Shift/Ctrl/Alt modifiers.
    /// </summary>
    public KeyBinding DeafenBinding { get; set; } = new();

    public float MasterVolume { get; set; } = 2.0f;

    public AudioFalloffModel FalloffModel { get; set; } = new();
    public bool MuteDeadPlayers { get; set; } = true;
    public int MuteDeadPlayersDelayMs { get; set; } = 2000;
    public bool UnmuteAllIfDead { get; set; } = true;
    public bool MuteOutOfMapPlayers { get; set; } = false;
    public bool EnableSpatialization { get; set; } = true;

    public bool PlayRoomJoinAndLeaveSounds { get; set; } = true;
    public bool KeybindsRequireGameFocus { get; set; }
    /// <summary>
    /// When true, the player list shows full character names everywhere.
    /// When false, names are reduced to initials. Replaces the old
    /// ShowInitialsInPrivateRooms toggle which was scoped to private rooms only.
    /// </summary>
    public bool ShowFullPlayerNames { get; set; } = true;

    /// <summary>
    /// When true, draws a microphone icon above the nameplate of each player
    /// in the current voice room (green if speaking, white if silent, hidden
    /// when the peer is muted/deafened). Read by <see cref="UI.NameplateVoiceOverlay"/>.
    /// </summary>
    public bool ShowNameplateIcons { get; set; } = true;

    public bool PrintLogsToChat { get; set; }

    public int MinimumVisibleLogLevel { get; set; } = LogLevel.Info.Ordinal;

    // ─ Profile Settings ──────────────────────────────────────────────────
    /// <summary>
    /// Profile data stored per character, keyed by full name + world.
    /// </summary>
    public Dictionary<string, CharacterProfile> CharacterProfiles { get; set; } = [];

    /// <summary>
    /// User's biography/about me text for the active character.
    /// </summary>
    public string UserBiography { get; set; } = string.Empty;

    /// <summary>
    /// User's pronoun setting for the active character.
    /// </summary>
    public string UserPronoun { get; set; } = "Unset";

    /// <summary>
    /// User's selected color as a hex string for the active character.
    /// </summary>
    public string UserColor { get; set; } = "FFFFFF";

    /// <summary>
    /// User's Twitch link for the active character.
    /// </summary>
    public string TwitchLink { get; set; } = string.Empty;

    /// <summary>
    /// User's current status for the active character.
    /// </summary>
    public string UserStatus { get; set; } = string.Empty;

    [NonSerialized]
    private string activeCharacterKey = string.Empty;

    // ─ Premium Gate ──────────────────────────────────────────────────────
    /// <summary>
    /// Opaque server-issued token that proves this install is linked to a
    /// Discord account with the configured supporter role. Empty when the
    /// user has not linked yet, or after a sign-out.
    /// </summary>
    public string PremiumToken { get; set; } = string.Empty;

    /// <summary>
    /// Unix milliseconds (UTC) at which the server said this token will
    /// expire. The server extends this on every successful ready, so the
    /// effective lifetime is rolling. Plugin only uses this as a hint —
    /// the server is the source of truth.
    /// </summary>
    public long PremiumTokenExpiresAtUtcMs { get; set; }

    /// <summary>
    /// Discord username at link time. Display-only, for the ConfigWindow
    /// "Signed in as ..." line. Not used for any auth decision.
    /// </summary>
    public string PremiumLinkedDiscordUsername { get; set; } = string.Empty;

    /// <summary>
    /// Most recent premium-status echo the server sent back for THIS user.
    /// The server emits a <c>premiumStatus</c> event right after resolving
    /// the premium token on every connect; <see cref="VoiceRoomManager"/>
    /// mirrors that into here. Persisted so the lock state is correct on
    /// plugin start before any room is joined — the next connection will
    /// overwrite it from the server's verdict.
    /// </summary>
    public bool IsLocalPremium { get; set; } = false;

    /// <summary>
    /// Server-stamped: true when this user's peerId is in the signaling
    /// server's ADMIN_PEER_IDS allowlist. The server emits <c>adminStatus</c>
    /// right after the premium echo. NOT persisted — admin status is
    /// session-scoped and must be re-confirmed by the server on every
    /// connect. A stale persisted value would let a local user fake the
    /// admin UI between sessions even though no server action would
    /// actually fire (the server validates again per command).
    /// </summary>
    [NonSerialized]
    public bool IsLocalAdmin = false;

    /// <summary>
    /// True when the signaling server has informed us (via a
    /// <c>muteState</c> event with <c>self=true</c>) that an admin has
    /// globally muted us. The server is dropping our audio frames at the
    /// relay; this flag exists so the plugin can render a clear "you
    /// have been globally muted" banner — otherwise we'd appear to be
    /// transmitting normally to ourselves. NOT persisted — global mute
    /// is per-connection on the server, so a reconnect clears it.
    /// </summary>
    [NonSerialized]
    public bool IsLocallyGlobalMuted = false;

    public Dictionary<string, float> PeerVolumes { get; set; } = [];

    // the below exist just to make saving less cumbersome
    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
    }

    public bool SyncActiveCharacterProfile(string? characterKey = null)
    {
        var resolvedKey = ResolveCharacterKey(characterKey);
        if (string.IsNullOrWhiteSpace(resolvedKey))
        {
            return false;
        }

        if (string.Equals(this.activeCharacterKey, resolvedKey, StringComparison.Ordinal))
        {
            return false;
        }

        SaveActiveProfileToDictionary();
        this.activeCharacterKey = resolvedKey;
        LoadActiveProfileFromDictionary();
        return true;
    }

    public string GetActiveCharacterKey() => this.activeCharacterKey;

    private string ResolveCharacterKey(string? characterKey)
    {
        if (!string.IsNullOrWhiteSpace(characterKey))
        {
            return characterKey.Trim();
        }

        var localPlayerName = PluginInitializer.PlayerState?.GetLocalPlayerFullName();
        return string.IsNullOrWhiteSpace(localPlayerName) ? string.Empty : localPlayerName.Trim();
    }

    private void SaveActiveProfileToDictionary()
    {
        if (string.IsNullOrWhiteSpace(this.activeCharacterKey))
        {
            return;
        }

        this.CharacterProfiles[this.activeCharacterKey] = new CharacterProfile
        {
            UserBiography = this.UserBiography,
            UserPronoun = this.UserPronoun,
            UserColor = this.UserColor,
            TwitchLink = this.TwitchLink,
            UserStatus = this.UserStatus,
        };
    }

    private void LoadActiveProfileFromDictionary()
    {
        if (string.IsNullOrWhiteSpace(this.activeCharacterKey))
        {
            return;
        }

        if (!this.CharacterProfiles.TryGetValue(this.activeCharacterKey, out var profile))
        {
            profile = this.CharacterProfiles.Count == 0 && HasLegacyProfileData()
                ? new CharacterProfile
                {
                    UserBiography = this.UserBiography,
                    UserPronoun = this.UserPronoun,
                    UserColor = this.UserColor,
                    TwitchLink = this.TwitchLink,
                    UserStatus = this.UserStatus,
                }
                : new CharacterProfile();

            this.CharacterProfiles[this.activeCharacterKey] = profile;
        }

        this.UserBiography = profile.UserBiography;
        this.UserPronoun = string.IsNullOrWhiteSpace(profile.UserPronoun) ? "Unset" : profile.UserPronoun;
        this.UserColor = string.IsNullOrWhiteSpace(profile.UserColor) ? "FFFFFF" : profile.UserColor;
        this.TwitchLink = profile.TwitchLink;
        this.UserStatus = profile.UserStatus;
    }

    private bool HasLegacyProfileData()
    {
        return !string.IsNullOrWhiteSpace(this.UserBiography)
            || !string.IsNullOrWhiteSpace(this.UserPronoun)
            || !string.IsNullOrWhiteSpace(this.UserColor) && !string.Equals(this.UserColor, "FFFFFF", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(this.TwitchLink)
            || !string.IsNullOrWhiteSpace(this.UserStatus);
    }

    public void Save()
    {
        SaveActiveProfileToDictionary();
        this.pluginInterface!.SavePluginConfig(this);
    }

    [Serializable]
    public sealed class CharacterProfile
    {
        public string UserBiography { get; set; } = string.Empty;
        public string UserPronoun { get; set; } = "Unset";
        public string UserColor { get; set; } = "FFFFFF";
        public string TwitchLink { get; set; } = string.Empty;
        public string UserStatus { get; set; } = string.Empty;
    }
}
