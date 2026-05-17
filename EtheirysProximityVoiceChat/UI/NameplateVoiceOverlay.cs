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
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface;
using EtheirysProximityVoiceChat.Audio;
using EtheirysProximityVoiceChat.Extensions;
using EtheirysProximityVoiceChat.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace EtheirysProximityVoiceChat.UI;

/// <summary>
/// Draws a FontAwesome microphone icon centered just above each in-room
/// player's game-rendered nameplate. Green while speaking, white while
/// silent, hidden when the peer is muted or deafened.
///
/// Positioning technique (the same one Lightless Sync's Lightfinder uses):
/// we don't do our own world→screen projection any more. Instead we subscribe
/// to <c>AddonEvent.PostDraw</c> on the <c>"NamePlate"</c> addon — that fires
/// once per frame, after the game has fully positioned every nameplate UI
/// node. At that point we walk <c>UI3DModule.NamePlateObjectInfoPointers</c>
/// to map each visible nameplate slot to its <c>GameObject*</c>, match it to
/// a peer in our voice room, and read the on-screen pixel position straight
/// off the AtkResNode tree (<c>nameContainer->ScreenX/ScreenY/Width</c> plus
/// the world-scale baked into the node's transform matrix). The cached
/// per-peer screen positions are then drawn during the normal
/// <c>UiBuilder.Draw</c> pass.
///
/// Why this matters: reading screen coords the engine has just finalized is
/// pixel-stable. Re-projecting world coords with <see cref="WorldToScreen"/>
/// every frame picks up animation jitter from idle "breathing", sub-tick
/// easing on <c>NameplateOffset</c>, and any camera motion — that's where
/// the old vibration came from.
///
/// Trade-off: only players whose nameplate is currently in range / visible
/// to the game get a mic icon. Out-of-range or culled players have no
/// nameplate to attach to, so no icon — same as how the game's own
/// nameplate-based UI behaves.
/// </summary>
public sealed class NameplateVoiceOverlay(
    DalamudServices dalamud,
    VoiceRoomManager voiceRoomManager,
    PushToTalkController pushToTalkController,
    IAudioDeviceController audioDeviceController,
    Configuration configuration) : IDalamudHook, IDisposable
{
    private readonly DalamudServices dalamud = dalamud;
    private readonly VoiceRoomManager voiceRoomManager = voiceRoomManager;
    private readonly PushToTalkController pushToTalkController = pushToTalkController;
    private readonly IAudioDeviceController audioDeviceController = audioDeviceController;
    private readonly Configuration configuration = configuration;

    private static readonly string MicGlyph = FontAwesomeIcon.Microphone.ToIconString();

    // VAD oscillates between active/inactive at audio-frame cadence (~20 ms),
    // including during sustained speech (the gaps between phonemes register as
    // silent). Reading that flag straight into the icon color caused visible
    // green↔white flicker. We latch "speaking=true" for this many ms after the
    // last frame the VAD said active, which is the same release-time pattern
    // used by every other voice indicator UI.
    private const long SpeakingHoldMs = 220;
    private readonly Dictionary<string, long> lastSpeakingTickByPlayer = new(StringComparer.Ordinal);

    // PostDraw on "NamePlate" runs on the game framework thread; UiBuilder.Draw
    // runs on the UI thread. We hand screen positions between them through this
    // dictionary, gated by `cacheLock`. Cleared and rebuilt every PostDraw so a
    // peer that loses their nameplate this frame stops drawing immediately.
    private readonly object cacheLock = new();
    private readonly Dictionary<string, Vector2> nameplateScreenPosByPlayer = new(StringComparer.Ordinal);

    // Pixels above the top of the nameplate's NameContainer where the icon
    // center is drawn. Positive values move the icon further above the name.
    private const float IconYOffsetAboveNameplatePx = 8f;
    // The microphone glyph is roughly square at iconFontSize; this is its
    // approximate half-width for centering. (No font metrics API in ImGui
    // unless we push the icon font first; this approximation is good enough.)
    private const float GlyphHalfWidthPx = 6f;
    private const float IconFontSizePx = 20f;

    public void HookToDalamud()
    {
        this.dalamud.PluginInterface.UiBuilder.Draw += DrawOverlay;
        this.dalamud.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "NamePlate", OnNamePlatePostDraw);
    }

    public void Dispose()
    {
        this.dalamud.PluginInterface.UiBuilder.Draw -= DrawOverlay;
        this.dalamud.AddonLifecycle.UnregisterListener(OnNamePlatePostDraw);
        lock (this.cacheLock)
        {
            this.nameplateScreenPosByPlayer.Clear();
        }
        this.lastSpeakingTickByPlayer.Clear();
    }

    // ── Phase 1: post-draw nameplate scan (framework thread) ────────────────
    // Sample the game's just-finalized nameplate positions and cache them by
    // player name. Runs once per frame.
    private unsafe void OnNamePlatePostDraw(AddonEvent type, AddonArgs args)
    {
        if (!this.configuration.ShowNameplateIcons) return;
        if (!this.voiceRoomManager.InRoom) return;
        if (this.dalamud.GameGui.GameUiHidden) { ClearScreenCache(); return; }

        if (args.Addon.Address == nint.Zero) { ClearScreenCache(); return; }
        var addonNamePlate = (AddonNamePlate*)args.Addon.Address;
        if (addonNamePlate == null) { ClearScreenCache(); return; }

        // Set of peer names we care about this frame. Local player first, then
        // every remote peer in the voice room.
        var users = new HashSet<string>(this.voiceRoomManager.PlayersInVoiceRoom, StringComparer.Ordinal);
        if (users.Count == 0) { ClearScreenCache(); return; }

        // Build a temporary map of peerName → GameObject pointer by walking
        // the ObjectTable once. Used to match nameplate slots to peers.
        var peerAddressByName = new Dictionary<string, nint>(StringComparer.Ordinal);
        foreach (var player in this.dalamud.ObjectTable.GetPlayers())
        {
            var name = player.GetPlayerFullName();
            if (string.IsNullOrEmpty(name) || !users.Contains(name)) continue;
            peerAddressByName[name] = player.Address;
        }
        if (peerAddressByName.Count == 0) { ClearScreenCache(); return; }

        // Walk UI3DModule's NamePlateObjectInfoPointers — that's the engine's
        // own list of (GameObject*, NamePlateIndex) entries for nameplates it
        // is currently rendering.
        var fw = Framework.Instance();
        if (fw == null) { ClearScreenCache(); return; }
        var uiModule = fw->GetUIModule();
        if (uiModule == null) { ClearScreenCache(); return; }
        var ui3DModule = uiModule->GetUI3DModule();
        if (ui3DModule == null) { ClearScreenCache(); return; }

        var infoSpan = ui3DModule->NamePlateObjectInfoPointers;
        var safeCount = Math.Min(ui3DModule->NamePlateObjectInfoCount, infoSpan.Length);

        lock (this.cacheLock)
        {
            this.nameplateScreenPosByPlayer.Clear();

            for (int i = 0; i < safeCount; i++)
            {
                var infoPtr = infoSpan[i].Value;
                if (infoPtr == null || infoPtr->GameObject == null) continue;
                var gameObjectAddr = (nint)infoPtr->GameObject;

                // Match this nameplate's GameObject pointer back to one of our peers.
                string? matchedName = null;
                foreach (var (name, addr) in peerAddressByName)
                {
                    if (addr == gameObjectAddr) { matchedName = name; break; }
                }
                if (matchedName == null) continue;

                var nameplateIndex = infoPtr->NamePlateIndex;
                if (nameplateIndex < 0 || nameplateIndex >= AddonNamePlate.NumNamePlateObjects) continue;

                var npObject = addonNamePlate->NamePlateObjectArray[nameplateIndex];
                var nameContainer = npObject.NameContainer;
                if (nameContainer == null) continue;
                if (!nameContainer->IsVisible()) continue;

                // World-space scale baked into the node's transform matrix.
                // sqrt(M11² + M12²) handles both scale-only and rotation+scale
                // cases — for nameplates the rotation is normally zero so this
                // collapses to the scaleX/Y of the node.
                var t = nameContainer->Transform;
                var worldScaleX = MathF.Sqrt(t.M11 * t.M11 + t.M12 * t.M12);
                if (worldScaleX <= 0f) worldScaleX = 1f;

                var containerScreenX = nameContainer->ScreenX;
                var containerScreenY = nameContainer->ScreenY;
                var containerWidth = nameContainer->Width * worldScaleX;

                // Center horizontally on the nameplate, position vertically
                // a few pixels above its top edge. AddText draws from the
                // glyph's top-left, so subtract IconFontSizePx to put the
                // icon's bottom at (top of nameplate - offset).
                var iconCenterX = containerScreenX + containerWidth * 0.5f;
                var iconBottomY = containerScreenY - IconYOffsetAboveNameplatePx;
                var iconPos = new Vector2(
                    MathF.Round(iconCenterX - GlyphHalfWidthPx),
                    MathF.Round(iconBottomY - IconFontSizePx));

                this.nameplateScreenPosByPlayer[matchedName] = iconPos;
            }
        }
    }

    private void ClearScreenCache()
    {
        lock (this.cacheLock)
        {
            this.nameplateScreenPosByPlayer.Clear();
        }
    }

    // ── Phase 2: draw cached positions (UI thread) ──────────────────────────
    private void DrawOverlay()
    {
        if (!this.configuration.ShowNameplateIcons) return;
        if (!this.voiceRoomManager.InRoom) return;

        // Snapshot the cache so we don't hold the lock across ImGui draws.
        KeyValuePair<string, Vector2>[] snapshot;
        lock (this.cacheLock)
        {
            if (this.nameplateScreenPosByPlayer.Count == 0)
            {
                this.lastSpeakingTickByPlayer.Clear();
                return;
            }
            snapshot = new KeyValuePair<string, Vector2>[this.nameplateScreenPosByPlayer.Count];
            var idx = 0;
            foreach (var kv in this.nameplateScreenPosByPlayer) snapshot[idx++] = kv;
        }

        var localName = this.dalamud.PlayerState.GetLocalPlayerFullName();
        var presencePeers = this.voiceRoomManager.Presence?.Peers;
        var drawList = ImGui.GetForegroundDrawList();
        var iconFont = UiBuilder.IconFont;
        var nowMs = Environment.TickCount64;

        var greenColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.18f, 0.90f, 0.32f, 1.00f));
        var whiteColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1.00f, 1.00f, 1.00f, 0.85f));

        var stillPresent = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, iconPos) in snapshot)
        {
            stillPresent.Add(name);
            var isSelf = string.Equals(name, localName, StringComparison.Ordinal);

            // Hide icon when muted or deafened
            bool isMutedOrDeafened;
            if (isSelf)
            {
                isMutedOrDeafened = this.audioDeviceController.MuteMic || this.audioDeviceController.Deafen;
            }
            else
            {
                var flags = (Peer.AudioStateFlags)0;
                if (presencePeers != null && presencePeers.TryGetValue(name, out var peer))
                {
                    flags = peer.AudioState;
                }
                isMutedOrDeafened = flags.HasFlag(Peer.AudioStateFlags.MicMuted)
                                 || flags.HasFlag(Peer.AudioStateFlags.Deafened);
            }
            if (isMutedOrDeafened) continue;

            var instantaneousSpeaking = isSelf
                ? (!this.audioDeviceController.PlayingBackMicAudio &&
                   (this.configuration.PushToTalk
                       ? this.pushToTalkController.PushToTalkKeyDown
                       : this.audioDeviceController.RecordingDataHasActivity))
                : this.audioDeviceController.ChannelHasActivity(name);

            // Hold the speaking flag for SpeakingHoldMs after the last active
            // sample so the green color doesn't flicker through VAD microgaps.
            if (instantaneousSpeaking)
            {
                this.lastSpeakingTickByPlayer[name] = nowMs;
            }
            var speaking = this.lastSpeakingTickByPlayer.TryGetValue(name, out var lastTick)
                && nowMs - lastTick < SpeakingHoldMs;

            drawList.AddText(iconFont, IconFontSizePx, iconPos, speaking ? greenColor : whiteColor, MicGlyph);
        }

        // Prune the speaking-hold map for peers no longer in the cache.
        if (this.lastSpeakingTickByPlayer.Count > stillPresent.Count)
        {
            foreach (var key in new List<string>(this.lastSpeakingTickByPlayer.Keys))
            {
                if (!stillPresent.Contains(key)) this.lastSpeakingTickByPlayer.Remove(key);
            }
        }
    }
}
