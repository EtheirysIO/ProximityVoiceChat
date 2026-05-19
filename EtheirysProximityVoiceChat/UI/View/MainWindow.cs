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
using System.Net.Http;
using System.Net.Http.Json;
using System.Numerics;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using EtheirysProximityVoiceChat.Audio;
using EtheirysProximityVoiceChat.Extensions;
using EtheirysProximityVoiceChat.Input;
using EtheirysProximityVoiceChat.UI.Presenter;
using EtheirysProximityVoiceChat.UI.Util;
using EtheirysProximityVoiceChat.WebRTC;
using Lumina.Excel.Sheets;
using Reactive.Bindings;

namespace EtheirysProximityVoiceChat.UI.View;

public sealed class MainWindow : Window, IPluginUIView, IDisposable
{
    // Plugin posts reports to the signaling server, which proxies them to a
    // Discord webhook held in REPORT_WEBHOOK_URL on the server side. Keeping
    // the webhook URL out of the shipped DLL prevents trivial abuse and lets
    // us rotate the destination without re-releasing the plugin.
    private const string ReportRelativePath = "/api/report";

    // Feature flag: hides all "click to view profile" interactions in the main
    // window while the Premium gate is still dormant (no Discord OAuth
    // credentials live on the signaling server yet). Flip to true alongside
    // ConfigWindow.ShowProfileFeature when premium goes live.
    private const bool ShowProfileFeature = false;

    private static readonly string[] ReportReasons =
    {
        "Harassment or hate speech",
        "Unsolicited explicit content in public",
        "Consent boundary violation (would not disengage)",
        "Private room intrusion attempt",
        "Mic abuse or disruptive noise",
        "Doxxing or real-life threats",
        "Other (see details)",
    };

    // ImGui needs a bool ref, can't ref a property
    private bool visible = false;
    public bool Visible
    {
        get => this.visible;
        set => this.visible = value;
    }

    public IReactiveProperty<bool> PublicRoom { get; } = new ReactiveProperty<bool>();
    public IReactiveProperty<string> RoomName { get; } = new ReactiveProperty<string>(string.Empty);
    public IReactiveProperty<string> RoomPassword { get; } = new ReactiveProperty<string>(string.Empty);

    private readonly Subject<Unit> joinVoiceRoom = new();
    public IObservable<Unit> JoinVoiceRoom => joinVoiceRoom.AsObservable();
    private readonly Subject<Unit> leaveVoiceRoom = new();
    public IObservable<Unit> LeaveVoiceRoom => leaveVoiceRoom.AsObservable();

    public IObservable<bool> MuteMic => muteMic.AsObservable();
    private readonly Subject<bool> muteMic = new();
    public IObservable<bool> Deafen => deafen.AsObservable();
    private readonly Subject<bool> deafen = new();

    public IObservable<(string playerName, float volume)> SetPeerVolume => setPeerVolume.AsObservable();
    private readonly Subject<(string playerName, float volume)> setPeerVolume = new();

    private readonly WindowSystem windowSystem;
    private readonly DalamudServices dalamud;
    private readonly PushToTalkController pushToTalkController;
    private readonly IAudioDeviceController audioDeviceController;
    private readonly VoiceRoomManager voiceRoomManager;
    private readonly MapManager mapChangeHandler;
    private readonly Configuration configuration;
    private readonly ConfigWindow configWindow;
    private readonly HttpClient httpClient = new();
    private readonly Queue<DateTime> reportTimestampsUtc = new();

    private bool reportSubmitting;
    private bool openReportModalRequested;
    private bool openReportSubmittedModalRequested;
    // Kick modal: pops once per LatestError transition into KickedFromChannel.
    // kickModalShownForCurrentError prevents the modal from spamming open on
    // every frame while the error sits there; it resets when LatestError
    // clears or transitions away from the kick state.
    private bool openKickModalRequested;
    private bool kickModalShownForCurrentError;
    private string kickModalMessage = string.Empty;
    private bool closeReportModalRequested;
    private string reportTargetUser = string.Empty;
    private int reportReasonIndex;
    private string reportDetails = string.Empty;
    private string reportStatusMessage = string.Empty;
    private bool openProfileModalRequested;
    private string profileTargetUser = string.Empty;
    private bool profileTargetIsSelf;

    private bool editingStatus = false;
    private string statusEditBuffer = string.Empty;

    /// <summary>
    /// Window-relative X (in points) where the channel name's first glyph is
    /// drawn in <see cref="DrawVoiceChannel"/>. Captured each frame the channel
    /// row renders, then reused by <see cref="DrawPeerRow"/> to align the
    /// voice-activity dot with the same X — so the dot lines up under the "L"
    /// of "Limsa…", and the gap between dot and name matches the gap between
    /// the speaker icon and the channel name. Initialised to a sensible
    /// fallback in case a peer row ever draws before the channel row.
    /// </summary>
    private float channelNameAlignX = 28f;

    private readonly string windowName;

    public MainWindow(
        WindowSystem windowSystem,
        DalamudServices dalamud,
        PushToTalkController pushToTalkController,
        VoiceRoomManager voiceRoomManager,
        MapManager mapChangeHandler,
        Configuration configuration,
        ConfigWindow configWindow) : base(PluginInitializer.Name)
    {
        this.windowSystem = windowSystem;
        this.dalamud = dalamud;
        this.pushToTalkController = pushToTalkController;
        this.audioDeviceController = pushToTalkController;
        this.voiceRoomManager = voiceRoomManager;
        this.mapChangeHandler = mapChangeHandler;
        this.configuration = configuration;
        this.configWindow = configWindow;

        var version = GetType().Assembly.GetName().Version?.ToString() ?? string.Empty;
        this.windowName = PluginInitializer.Name;
        if (version.Length > 0)
        {
            var versionArray = version.Split(".");
            version = string.Join(".", versionArray.Take(3));
            this.windowName += $" v{version}";
        }
#if DEBUG
        this.windowName += " (DEBUG)";
#endif
        windowSystem.AddWindow(this);

#if DEBUG
        visible = true;
#endif
    }

    public override void Draw()
    {
        if (!Visible) return;

        this.configuration.SyncActiveCharacterProfile();

        ImGui.SetNextWindowSize(new Vector2(360, 520), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(320, 300), new Vector2(float.MaxValue, float.MaxValue));
        if (ImGui.Begin(this.windowName, ref this.visible))
        {
            DrawContents();
        }
        ImGui.End();
    }

    public void Dispose()
    {
        this.httpClient.Dispose();
        windowSystem.RemoveWindow(this);
    }

    // Colour constants for MainWindow chrome live in Vector4Colors so the
    // theme stays consistent with ConfigWindow and any future surface.

    private string GetLocalPlayerDisplayName() =>
        this.dalamud.PlayerState.GetLocalPlayerFullName() ?? "Unknown Player";

    private string GetCurrentZoneDisplayName()
    {
        // Resolve the human-readable territory name (e.g. "Limsa Lominsa Lower Decks")
        // from the current TerritoryType via Lumina. The internal public-room id from
        // MapManager is still what's used to bucket players on the signaling server —
        // this is purely cosmetic.
        try
        {
            var row = this.dalamud.DataManager.GetExcelSheet<TerritoryType>()
                         ?.GetRow(this.dalamud.ClientState.TerritoryType);
            var name = row?.PlaceName.Value.Name.ExtractText();
            return string.IsNullOrWhiteSpace(name) ? "Unknown Zone" : name;
        }
        catch
        {
            return "Unknown Zone";
        }
    }

    private string GetCurrentWorldDisplayName()
    {
        try
        {
            // Same source `MapManager.GetCurrentMapPublicRoomName` uses for the
            // world component of the public room id, so the world label in the
            // UI always matches the room the join button would join.
            var world = this.dalamud.PlayerState.CurrentWorld;
            return world.IsValid ? world.Value.Name.ExtractText() ?? string.Empty : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private string GetVoiceChannelDisplayName()
    {
        var zone = GetCurrentZoneDisplayName();
        var world = GetCurrentWorldDisplayName();
        return string.IsNullOrEmpty(world) ? zone : $"{zone} @ {world}";
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Main layout
    // ══════════════════════════════════════════════════════════════════════════
    private void DrawContents()
    {
        // Rounded buttons (Join / Leave / mic / headphones / settings / end-call)
        // to match the redesigned look. Applies to every Button/ImageButton drawn
        // inside this window.
        using var frameRounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 6f);
        using var grabRounding = ImRaii.PushStyle(ImGuiStyleVar.GrabRounding, 6f);

        var latestError = this.voiceRoomManager.SignalingChannel?.LatestError;
        var unsupportedOs = latestError == SignalingChannelError.UnsupportedOperatingSystem;

        if (unsupportedOs)
        {
            using var c = ImRaii.PushColor(ImGuiCol.Text, Vector4Colors.Red);
            ImGui.TextWrapped("  Voice chat is only supported on Windows.");
        }
        else
        {
            // Single zone-based voice channel routed through the existing public-room
            // join path. The presenter routes JoinVoiceRoom → JoinPublicVoiceRoom when
            // PublicRoom.Value is true, so make sure that's set.
            this.PublicRoom.Value = true;

            DrawVoiceChannel();
        }

        if (this.openReportModalRequested)
        {
            this.openReportModalRequested = false;
            ImGui.OpenPopup("report-player-modal");
        }

        if (this.openReportSubmittedModalRequested)
        {
            this.openReportSubmittedModalRequested = false;
            ImGui.OpenPopup("report-submitted-modal");
        }

        // Watch for a KickedFromChannel error transition and pop the modal
        // exactly once per occurrence. The user dismisses it explicitly,
        // which clears LatestError and lets future kicks re-trigger.
        var latestErrForKick = this.voiceRoomManager.SignalingChannel?.LatestError;
        if (latestErrForKick == SignalingChannelError.KickedFromChannel)
        {
            if (!this.kickModalShownForCurrentError)
            {
                this.kickModalShownForCurrentError = true;
                this.openKickModalRequested = true;
                this.kickModalMessage = this.voiceRoomManager.SignalingChannel?.LatestErrorMessage
                    ?? "You have been kicked from the voice chat.";
            }
        }
        else
        {
            // Error cleared or replaced — re-arm for the next kick.
            this.kickModalShownForCurrentError = false;
        }
        if (this.openKickModalRequested)
        {
            this.openKickModalRequested = false;
            ImGui.OpenPopup("You have been kicked##kicked-by-admin-modal");
        }

        // Push footer to the bottom of the window
        var footerHeight = 96f;
        var remaining = ImGui.GetContentRegionAvail().Y - footerHeight;
        if (remaining > 0)
        {
            ImGui.Dummy(new Vector2(0, remaining));
        }

        ImGui.Separator();
        ImGui.Spacing();
        DrawDeviceControlsBar();

        DrawReportModal();
        DrawReportSubmittedModal();
        DrawKickedByAdminModal();
    }

    // ── voice channel row + peer roster ──────────────────────────────────────
    private void DrawVoiceChannel()
    {
        var inRoom = this.voiceRoomManager.InRoom;
        var connecting = this.voiceRoomManager.SignalingChannel?.Connecting ?? false;
        var channelName = GetVoiceChannelDisplayName();
        var dl = ImGui.GetWindowDrawList();

        // If an admin has globally muted us on the server, our audio is being
        // dropped at the relay even though our local VAD still sees us
        // transmitting. Show a prominent banner so the user understands why
        // no one is hearing them.
        if (inRoom && this.configuration.IsLocallyGlobalMuted)
        {
            var bannerStart = ImGui.GetCursorScreenPos() - new Vector2(4, 0);
            var bannerEnd = bannerStart + new Vector2(ImGui.GetContentRegionAvail().X + 8, ImGui.GetTextLineHeightWithSpacing() + 8);
            dl.AddRectFilled(bannerStart, bannerEnd, ImGui.ColorConvertFloat4ToU32(new Vector4(0.40f, 0.10f, 0.10f, 0.95f)));
            dl.AddRect(bannerStart, bannerEnd, ImGui.ColorConvertFloat4ToU32(new Vector4(0.95f, 0.45f, 0.45f, 1.0f)));
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8);
            ImGui.Dummy(new Vector2(0, 4));
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8);
            using (var iconFont = ImRaii.PushFont(UiBuilder.IconFont))
            using (var c = ImRaii.PushColor(ImGuiCol.Text, new Vector4(1.0f, 0.85f, 0.85f, 1f)))
            {
                ImGui.TextUnformatted(FontAwesomeIcon.MicrophoneSlash.ToIconString());
            }
            ImGui.SameLine(0, 8);
            using (var c = ImRaii.PushColor(ImGuiCol.Text, new Vector4(1.0f, 0.92f, 0.92f, 1f)))
            {
                ImGui.TextUnformatted("You have been globally muted by an admin. Others cannot hear you.");
            }
            ImGui.Dummy(new Vector2(0, 4));
        }

        var rosterMaterialized = inRoom
            ? this.voiceRoomManager.PlayersInVoiceRoom.ToList()
            : new List<string>();

        // Sort the roster by spatial distance so the closest peers (the
        // ones you can actually hear) bubble to the top. Self stays at
        // position 0 unconditionally; everyone else is grouped into
        // "in-range, sorted by distance ascending" followed by
        // "out-of-range (NaN distance), preserving roster order".
        // Stable on ties via the original-index tiebreaker.
        if (inRoom && rosterMaterialized.Count > 1)
        {
            var selfUser = rosterMaterialized[0];
            rosterMaterialized = rosterMaterialized
                .Skip(1)
                .Select((user, originalIndex) => new
                {
                    user,
                    originalIndex,
                    distance = GetTrackedPlayerDistance(user),
                })
                .OrderBy(entry => float.IsNaN(entry.distance) ? 1 : 0)
                .ThenBy(entry => float.IsNaN(entry.distance) ? float.MaxValue : entry.distance)
                .ThenBy(entry => entry.originalIndex)
                .Select(entry => entry.user)
                .Prepend(selfUser)
                .ToList();
        }
        int count = rosterMaterialized.Count;

        if (inRoom)
        {
            var rowPos = ImGui.GetCursorScreenPos() - new Vector2(4, 2);
            var rowEnd = rowPos + new Vector2(ImGui.GetContentRegionAvail().X + 8, ImGui.GetTextLineHeightWithSpacing() + 4);
            dl.AddRectFilled(rowPos, rowEnd, ImGui.ColorConvertFloat4ToU32(Vector4Colors.JoinedBackground));
        }

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8);
        using (var iconFont = ImRaii.PushFont(UiBuilder.IconFont))
        {
            var icon = inRoom ? FontAwesomeIcon.VolumeUp.ToIconString() : FontAwesomeIcon.VolumeDown.ToIconString();
            ImGui.TextColored(inRoom ? Vector4Colors.Green : Vector4Colors.Gray, icon);
        }
        ImGui.SameLine();
        // Capture where the channel name's first glyph lands — this is the
        // X-coordinate that DrawPeerRow uses to align each peer's voice dot.
        this.channelNameAlignX = ImGui.GetCursorPosX();
        var countLabel = count > 0 ? $" ({count})" : string.Empty;
        ImGui.Text($"{channelName}{countLabel}");
        if (ImGui.IsItemClicked() && !inRoom && !connecting)
        {
            this.joinVoiceRoom.OnNext(Unit.Default);
        }

        var btnW = 60f;
        ImGui.SameLine(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - btnW);
        if (inRoom)
        {
            using var bc = ImRaii.PushColor(ImGuiCol.Button, Vector4Colors.LeaveButton);
            using var bhc = ImRaii.PushColor(ImGuiCol.ButtonHovered, Vector4Colors.LeaveButtonHover);
            if (ImGui.Button("Leave##voice", new Vector2(btnW, 0)))
            {
                this.leaveVoiceRoom.OnNext(Unit.Default);
            }
        }
        else
        {
            using var dis = ImRaii.Disabled(connecting);
            using var bc = ImRaii.PushColor(ImGuiCol.Button, Vector4Colors.JoinButton);
            using var bhc = ImRaii.PushColor(ImGuiCol.ButtonHovered, Vector4Colors.JoinButtonHover);
            if (ImGui.Button(connecting ? "...##voice" : "Join##voice", new Vector2(btnW, 0)))
            {
                this.joinVoiceRoom.OnNext(Unit.Default);
            }
        }

        if (rosterMaterialized.Count > 0)
        {
            // Peer rows used to live inside ImGui.Indent(20), which left the
            // voice dot to the left of the "L" in the channel name and a
            // wider-than-expected gap before the player name. Each peer row
            // now positions its own cursor via this.channelNameAlignX so the
            // dot lines up exactly under the channel name and the dot→name
            // gap mirrors the speaker-icon→channel-name gap.
            foreach (var (user, index) in rosterMaterialized.Select((u, i) => (u, i)))
            {
                DrawPeerRow(user, index);
            }
            ImGui.Dummy(new Vector2(0, 2));
        }

        // Surface non-fatal signaling errors below the channel row when we are
        // not in a room (the UnsupportedOperatingSystem case is handled earlier).
        if (!inRoom)
        {
            var err = this.voiceRoomManager.SignalingChannel?.LatestError;
            if (err != null && err != SignalingChannelError.UnsupportedOperatingSystem)
            {
                ImGui.Spacing();
                using var c = ImRaii.PushColor(ImGuiCol.Text, Vector4Colors.Red);
                switch (err)
                {
                    case SignalingChannelError.IncorrectPrivateRoomPassword:
                        ImGui.Text("  Incorrect password");
                        break;
                    case SignalingChannelError.NonexistentPrivateRoom:
                        ImGui.Text("  Room not found");
                        break;
                    case SignalingChannelError.KickedFromChannel:
                    {
                        var roomName = this.voiceRoomManager.SignalingChannel?.RoomName;
                        var kickedFrom = !string.IsNullOrWhiteSpace(roomName) && !roomName.StartsWith("public", StringComparison.Ordinal)
                            ? roomName
                            : GetVoiceChannelDisplayName();
                        var kickedMessage = $"You've been kicked from {kickedFrom}.";
                        ImGui.Text($"  {kickedMessage}");
                        break;
                    }
                    default:
                    {
                        // Fall back to the server-supplied error string when the
                        // error code doesn't match one of the cases above.
                        // LatestErrorMessage is populated by SignalingChannel's
                        // OnServerDisconnect handler from the structured
                        // serverDisconnect payload, so we surface the actual
                        // reason (e.g. "Server is shutting down (SIGTERM)")
                        // instead of the unhelpful "Unknown error" boilerplate.
                        var latestErrorMessage = this.voiceRoomManager.SignalingChannel?.LatestErrorMessage;
                        if (!string.IsNullOrWhiteSpace(latestErrorMessage))
                        {
                            ImGui.TextWrapped($"  {latestErrorMessage}");
                        }
                        else
                        {
                            ImGui.Text("  Unknown error");
                        }
                        break;
                    }
                }
            }
        }
    }

    private void DrawPeerRow(string user, int index)
    {
        bool isSelf = index == 0;

        if (!this.configuration.PeerVolumes.TryGetValue(user, out var currentVolume))
        {
            currentVolume = 1.0f;
        }

        // Distance: prefer the precomputed tracked-player value (already kept in
        // sync by the Spatializer); falls back to NaN for the local row.
        var distance = !isSelf && this.voiceRoomManager.TrackedPlayers.TryGetValue(user, out var tp)
            ? tp.Distance
            : float.NaN;
        // NaN distance for a non-self peer means the Spatializer can't see
        // them (different map / instance / out-of-stream-range). Renders
        // their roster row in muted gray with an explanatory tooltip so
        // users understand "I can see them in the room but I can't hear
        // them right now".
        var outOfRange = !isSelf && float.IsNaN(distance);

        // Speaking state: local mic for self, per-channel relay activity for peers.
        var isSpeaking = isSelf
            ? (!this.audioDeviceController.PlayingBackMicAudio &&
               (this.configuration.PushToTalk
                   ? this.pushToTalkController.PushToTalkKeyDown
                   : this.audioDeviceController.RecordingDataHasActivity))
            : this.audioDeviceController.ChannelHasActivity(user);

        // Per-peer audio state flags from presence.
        var peerFlags = (Peer.AudioStateFlags)0;
        if (!isSelf && this.voiceRoomManager.Presence != null &&
            this.voiceRoomManager.Presence.Peers.TryGetValue(user, out var presencePeer))
        {
            peerFlags = presencePeer.AudioState;
        }

        var showMuted = isSelf
            ? this.audioDeviceController.MuteMic
            : (peerFlags.HasFlag(Peer.AudioStateFlags.MicMuted) || currentVolume <= 0.001f);
        var showDeafened = isSelf
            ? this.audioDeviceController.Deafen
            : peerFlags.HasFlag(Peer.AudioStateFlags.Deafened);

        using var id = ImRaii.PushId($"peer:{user}");

        var peerProfile = this.GetProfileForUser(user, isSelf);
        // Falls back to white when the peer (including self) is not premium —
        // never leaks the local user's color to the other end of a non-premium
        // pairing.
        var profileColor = Vector4Colors.HexToVector4(peerProfile?.Color ?? "FFFFFF");

        // Distance-aware voice-activity dot. Replaces the older 3-bar signal
        // indicator: a single green circle whose brightness fades with distance
        // (full bright at 0y, invisible past FalloffModel.MaximumDistance). The
        // dot is only drawn while the peer is actually speaking — silence and
        // out-of-range both render as empty space.
        //
        // The dot's slot is positioned at channelNameAlignX (captured in
        // DrawVoiceChannel) so its left edge sits directly under the channel
        // name's first glyph. The slot width matches the dot size exactly
        // (no extra padding) so the subsequent ImGui.SameLine() leaves only
        // the default ItemSpacing.X gap before the player name — mirroring
        // the speaker-icon → channel-name gap.
        ImGui.SetCursorPosX(this.channelNameAlignX);
        var cursorPos = ImGui.GetCursorScreenPos();
        var indicatorSize = new Vector2(14f, 12f);
        var indicatorPos = cursorPos + new Vector2(1f, MathF.Max(0f, (ImGui.GetTextLineHeight() - indicatorSize.Y) * 0.5f));
        var intensity = GetVoiceDotIntensity(isSelf, isSpeaking, distance);
        var drawList = ImGui.GetWindowDrawList();
        DrawVoiceDot(drawList, indicatorPos, indicatorSize, intensity);
        ImGui.Dummy(new Vector2(indicatorSize.X, 0));
        ImGui.SameLine();

        // Player label. Honors the "Show full player names" toggle to match
        // the rest of the plugin's name-display behavior.
        var rawName = this.configuration.ShowFullPlayerNames
            ? FormatPlayerDisplayName(user)
            : Utils.ConvertToInitialsName(user);
        var distStr = !isSelf && !float.IsNaN(distance) ? $" ({distance:F1}y)" : string.Empty;
        var fullLabel = $"{rawName}{distStr}";

        // Out-of-range peers render in muted gray so they read visually as
        // "present but inaudible". Everyone else gets their normal profile
        // color (white for non-premium peers — see GetProfileForUser).
        var labelColor = outOfRange
            ? new Vector4(0.78f, 0.78f, 0.78f, 1f)
            : profileColor;
        ImGui.TextColored(labelColor, fullLabel);
        if (outOfRange)
        {
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Player out of range, and cannot be heard");
            }
        }
        // ShowProfileFeature is a compile-time const false (see ConfigWindow.cs
        // for the rationale) → the if-body folds away. Suppress CS0162 so the
        // feature-flag intent stays visible in source.
#pragma warning disable CS0162
        else if (ShowProfileFeature)
        {
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Click to view profile");
            }
            if (ImGui.IsItemClicked())
            {
                this.profileTargetUser = user;
                this.profileTargetIsSelf = isSelf;
                this.openProfileModalRequested = true;
            }
        }
        else if (!isSelf && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Right-click for options");
        }
#pragma warning restore CS0162

        // Right-click context menu on a peer row:
        //   • Mute toggle + local volume slider — adjusts how loud the peer
        //     sounds on YOUR machine only. The signaling server isn't told
        //     about it; other listeners hear that peer at their own settings.
        //   • "Report Player" — opens the existing report modal, which posts
        //     through the signaling server's /api/report endpoint.
        // Hidden for the self row: no point in self-volume here (your local
        // playback level is in Settings → Audio Devices) and you can't
        // report yourself.
        if (!isSelf && ImGui.BeginPopupContextItem($"peer-ctx-{user}"))
        {
            var ctxMuteIcon = currentVolume <= 0.001f
                ? FontAwesomeIcon.MicrophoneSlash.ToIconString()
                : FontAwesomeIcon.Microphone.ToIconString();
            if (DrawIconButton($"peer-ctx-mute-{user}", ctxMuteIcon, new Vector2(28f, 28f)))
            {
                this.setPeerVolume.OnNext((user, currentVolume <= 0.001f ? 1.0f : 0.0f));
            }
            ImGui.SameLine(0f, 8f);
            var ctxVolPercent = currentVolume * 100f;
            ImGui.SetNextItemWidth(180f);
            if (ImGui.SliderFloat($"##peer-ctx-vol-{user}", ref ctxVolPercent, 0f, 200f, "%.0f%%"))
            {
                this.setPeerVolume.OnNext((user, ctxVolPercent / 100f));
            }

            ImGui.Separator();
            if (IsReportQuotaExceeded(out _))
            {
                ImGui.TextColored(new Vector4(0.72f, 0.72f, 0.72f, 1f), "Report quota reached. Try again later.");
            }
            else
            {
                if (Common.DrawDangerButton("Report Player", new Vector2(ImGui.GetContentRegionAvail().X, 28f)))
                {
                    this.reportTargetUser = user;
                    this.reportReasonIndex = 0;
                    this.reportDetails = string.Empty;
                    this.reportStatusMessage = string.Empty;
                    this.openReportModalRequested = true;
                    // Close the right-click popup so the report modal isn't
                    // stacked on top of it — the modal opens next frame via
                    // openReportModalRequested.
                    ImGui.CloseCurrentPopup();
                }
            }

            // ── Admin tools ────────────────────────────────────────────
            // Only rendered when the server has stamped IsLocalAdmin=true for
            // this session (peerId matched ADMIN_PEER_IDS on connect). The
            // server validates each admin command independently, so a tampered
            // client showing these buttons can't actually trigger anything.
            if (this.configuration.IsLocalAdmin)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.TextColored(new Vector4(1.0f, 0.78f, 0.35f, 1f), "Admin tools");
                ImGui.Spacing();

                var adminBtnWidth = ImGui.GetContentRegionAvail().X;
                var halfWidth = (adminBtnWidth - 6f) * 0.5f;
                if (ImGui.Button($"Global Mute##admin-mute-{user}", new Vector2(halfWidth, 26f)))
                {
                    _ = this.voiceRoomManager.SignalingChannel?.SendAdminGlobalMuteAsync(user, true);
                }
                ImGui.SameLine(0f, 6f);
                if (ImGui.Button($"Global Unmute##admin-unmute-{user}", new Vector2(halfWidth, 26f)))
                {
                    _ = this.voiceRoomManager.SignalingChannel?.SendAdminGlobalMuteAsync(user, false);
                }

                ImGui.Spacing();
                if (Common.DrawDangerButton("Kick (30s)", new Vector2(adminBtnWidth, 28f)))
                {
                    _ = this.voiceRoomManager.SignalingChannel?.SendAdminKickAsync(user);
                    ImGui.CloseCurrentPopup();
                }
            }

            ImGui.EndPopup();
        }

        // Inline mute/deafen icons to the right of the label.
        if (showMuted || showDeafened)
        {
            var itemMin = ImGui.GetItemRectMin();
            var iconX = itemMin.X + ImGui.GetStyle().FramePadding.X + ImGui.CalcTextSize(fullLabel).X + 8f;
            var iconY = itemMin.Y + Math.Max(0f, (ImGui.GetItemRectSize().Y - ImGui.GetTextLineHeight()) * 0.5f);
            ImGui.SetCursorScreenPos(new Vector2(iconX, iconY));

            using var iconFont = ImRaii.PushFont(UiBuilder.IconFont);
            using var iconColor = ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.95f, 0.35f, 0.35f, 1f));

            if (showMuted)
            {
                ImGui.TextUnformatted(FontAwesomeIcon.MicrophoneSlash.ToIconString());
            }

            if (showDeafened)
            {
                if (showMuted)
                {
                    ImGui.SameLine(0, 5f);
                }
                ImGui.TextUnformatted(FontAwesomeIcon.Headphones.ToIconString());
            }
        }

        // Admin-visible indicator: this peer has been globally muted by an
        // admin on the server. Distinct from the regular self-mute icon
        // above — gold-orange vs red — and only rendered on admin clients
        // (the server only sends muteState to admins + the target, so
        // GloballyMutedPeers stays empty on non-admin sessions).
        if (!isSelf
            && this.configuration.IsLocalAdmin
            && this.voiceRoomManager.GloballyMutedPeers.ContainsKey(user))
        {
            if (showMuted || showDeafened)
            {
                ImGui.SameLine(0, 5f);
            }
            else
            {
                var itemMin = ImGui.GetItemRectMin();
                var iconX = itemMin.X + ImGui.GetStyle().FramePadding.X + ImGui.CalcTextSize(fullLabel).X + 8f;
                var iconY = itemMin.Y + Math.Max(0f, (ImGui.GetItemRectSize().Y - ImGui.GetTextLineHeight()) * 0.5f);
                ImGui.SetCursorScreenPos(new Vector2(iconX, iconY));
            }

            bool hovered;
            using (var iconFont = ImRaii.PushFont(UiBuilder.IconFont))
            using (var iconColor = ImRaii.PushColor(ImGuiCol.Text, new Vector4(1.0f, 0.78f, 0.35f, 1f)))
            {
                ImGui.TextUnformatted(FontAwesomeIcon.MicrophoneSlash.ToIconString());
                hovered = ImGui.IsItemHovered();
            }
            if (hovered)
            {
                // SetTooltip after the icon-font ImRaii blocks have disposed
                // so the tooltip itself renders in the default font.
                ImGui.SetTooltip("Globally muted by admin");
            }
        }

        if (peerProfile != null && !string.IsNullOrEmpty(peerProfile.TwitchLink))
        {
            ImGui.SameLine(0, 12f);
            DrawTwitchPill(peerProfile.TwitchLink);
        }

        if (this.openProfileModalRequested && string.Equals(this.profileTargetUser, user, StringComparison.Ordinal))
        {
            ImGui.OpenPopup("player-profile-popup");
            this.openProfileModalRequested = false;
        }

        ImGui.SetNextWindowSize(new Vector2(316f, 385f), ImGuiCond.Always);
        using var popupPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(22f, 22f));
        using var popupRounding = ImRaii.PushStyle(ImGuiStyleVar.PopupRounding, 12f);
        using var popupBorderSize = ImRaii.PushStyle(ImGuiStyleVar.PopupBorderSize, 1f);
        using var popupBg = ImRaii.PushColor(ImGuiCol.PopupBg, new Vector4(0.17f, 0.17f, 0.17f, 0.98f));
        using var popupBorder = ImRaii.PushColor(ImGuiCol.Border, new Vector4(0.38f, 0.38f, 0.38f, 0.65f));
        if (ImGui.BeginPopup("player-profile-popup"))
        {
            DrawProfilePopupHeader(user, profileColor, peerProfile);

            DrawProfileSectionDivider();

            if (peerProfile != null)
            {
                DrawProfileStatusSection(peerProfile.Status);

                DrawProfileSectionDivider();

                DrawProfileAboutSection(peerProfile.Biography);

                if (!string.IsNullOrWhiteSpace(peerProfile.TwitchLink))
                {
                    DrawProfileTwitchSection(peerProfile.TwitchLink);
                }
            }

            DrawProfileSectionDivider();

            if (!isSelf)
            {
                var muteIcon = currentVolume <= 0.001f
                    ? FontAwesomeIcon.MicrophoneSlash.ToIconString()
                    : FontAwesomeIcon.Microphone.ToIconString();
                if (DrawIconButton("profile-mute", muteIcon, new Vector2(32f, 32f)))
                {
                    this.setPeerVolume.OnNext((user, currentVolume <= 0.001f ? 1.0f : 0.0f));
                }

                ImGui.SameLine(0f, 12f);
                var volumePercent = currentVolume * 100.0f;
                var pctText = $"{volumePercent:0}%";
                var pctWidth = ImGui.CalcTextSize(pctText).X + 8f;
                var sliderWidth = Math.Max(40f, ImGui.GetContentRegionAvail().X - pctWidth);
                using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(ImGui.GetStyle().FramePadding.X, 9f)))
                {
                    ImGui.SetNextItemWidth(sliderWidth);
                    if (ImGui.SliderFloat("##ProfileVolume", ref volumePercent, 0.0f, 200.0f, string.Empty))
                    {
                        this.setPeerVolume.OnNext((user, volumePercent / 100.0f));
                    }
                }
                ImGui.SameLine(0f, 8f);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(pctText);

                ImGui.Dummy(new Vector2(0f, 24f));
                if (!IsReportQuotaExceeded(out _))
                {
                    if (Common.DrawDangerButton("Report Player", new Vector2(ImGui.GetContentRegionAvail().X, 32f)))
                    {
                        this.reportTargetUser = user;
                        this.reportReasonIndex = 0;
                        this.reportDetails = string.Empty;
                        this.reportStatusMessage = string.Empty;
                        this.openReportModalRequested = true;
                    }
                }
            }
            else
            {
                ImGui.TextColored(new Vector4(0.72f, 0.72f, 0.72f, 1f), "This is your profile.");
            }

            ImGui.EndPopup();
        }
    }

    /// <summary>
    /// Helper for the roster sort + out-of-range visual: looks up a peer's
    /// current <see cref="TrackedPlayer.Distance"/>, returns NaN when the
    /// Spatializer hasn't computed one (peer in a different map / instance /
    /// out-of-stream-range). DrawVoiceChannel uses NaN to sink the peer to
    /// the bottom of the list; DrawPeerRow uses it to render the row gray.
    /// </summary>
    private float GetTrackedPlayerDistance(string user)
    {
        return this.voiceRoomManager.TrackedPlayers.TryGetValue(user, out var tracked)
            ? tracked.Distance
            : float.NaN;
    }

    /// <summary>
    /// Returns the brightness (0..1) for the per-peer voice-activity dot.
    /// 0 means "don't draw it at all" — used for silence and for peers past
    /// <see cref="Configuration.FalloffModel"/>'s <c>MaximumDistance</c>.
    /// 1 means full green. Brightness falls off linearly with distance so the
    /// dot visually tracks how audible the peer is. Self / NaN distance always
    /// render at full intensity when speaking.
    /// </summary>
    private float GetVoiceDotIntensity(bool isSelf, bool isSpeaking, float distance)
    {
        if (!isSpeaking)
        {
            return 0f;
        }

        if (isSelf || float.IsNaN(distance))
        {
            return 1f;
        }

        var maxDistance = this.configuration.FalloffModel.MaximumDistance;
        if (maxDistance <= 0f)
        {
            return 1f;
        }

        // Linear falloff: full bright at 0y, invisible at >= maxDistance.
        var intensity = 1f - (distance / maxDistance);
        return Math.Clamp(intensity, 0f, 1f);
    }

    /// <summary>
    /// Draws a green voice-activity dot inside the reserved indicator slot.
    /// Skips drawing entirely when intensity is 0 so out-of-range / silent
    /// peers leave a clean empty slot rather than a dim placeholder.
    /// </summary>
    private static void DrawVoiceDot(ImDrawListPtr drawList, Vector2 topLeft, Vector2 size, float intensity)
    {
        if (intensity <= 0f) return;

        // Center the dot inside the slot. Radius is sized to leave a 1 px
        // margin so it never touches the row's bounding box.
        var center = new Vector2(topLeft.X + size.X * 0.5f, topLeft.Y + size.Y * 0.5f);
        var radius = MathF.Min(size.X, size.Y) * 0.5f - 1f;

        // Color scales both saturation and alpha with intensity so far-away
        // peers fade out gracefully rather than just becoming transparent
        // green-on-background.
        var color = new Vector4(0.35f * intensity, 0.95f * intensity, 0.35f * intensity, intensity);
        drawList.AddCircleFilled(center, radius, ImGui.ColorConvertFloat4ToU32(color));
    }

    private void DrawReportModal()
    {
        var reporter = GetLocalPlayerDisplayName();
        var inRoom = this.voiceRoomManager.InRoom;
        if (ImGui.BeginPopupModal("report-player-modal", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextColored(Vector4Colors.Header, "Report Player");
            ImGui.Spacing();

            ImGui.TextWrapped($"Reporter: {reporter}");
            ImGui.TextWrapped($"Reported: {FormatPlayerDisplayName(this.reportTargetUser)}");
            if (inRoom)
            {
                ImGui.TextColored(Vector4Colors.Gray, $"Channel: {GetVoiceChannelDisplayName()}");
            }

            ImGui.Spacing();
            ImGui.Text("Violation");
            ImGui.SetNextItemWidth(360f);
            ImGui.Combo("##report-reason", ref this.reportReasonIndex, ReportReasons, ReportReasons.Length);

            ImGui.Spacing();
            ImGui.Text("Additional details (optional)");
            ImGui.SetNextItemWidth(360f);
            ImGui.InputTextMultiline("##report-details", ref this.reportDetails, 700, new Vector2(360f, 90f));

            var quotaExceeded = IsReportQuotaExceeded(out _);
            if (quotaExceeded)
            {
                ImGui.Spacing();
                ImGui.TextColored(Vector4Colors.Orange, "Reporting is temporarily unavailable.");
            }

            if (!string.IsNullOrWhiteSpace(this.reportStatusMessage))
            {
                ImGui.Spacing();
                var statusColor = this.reportStatusMessage.StartsWith("Report submitted", StringComparison.OrdinalIgnoreCase)
                    ? Vector4Colors.Green
                    : Vector4Colors.Red;
                ImGui.TextColored(statusColor, this.reportStatusMessage);
            }

            ImGui.Spacing();
            using (ImRaii.Disabled(this.reportSubmitting || quotaExceeded || string.IsNullOrWhiteSpace(this.reportTargetUser)))
            {
                if (ImGui.Button(this.reportSubmitting ? "Submitting..." : "Submit Report", new Vector2(150f, 0f)))
                {
                    _ = SubmitReportAsync(reporter, this.reportTargetUser, ReportReasons[this.reportReasonIndex], this.reportDetails);
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(110f, 0f)))
            {
                this.reportStatusMessage = string.Empty;
                ImGui.CloseCurrentPopup();
            }

            if (this.closeReportModalRequested)
            {
                this.closeReportModalRequested = false;
                this.reportStatusMessage = string.Empty;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void DrawReportSubmittedModal()
    {
        if (ImGui.BeginPopupModal("report-submitted-modal", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("Thanks for reporting.");
            ImGui.Spacing();
            if (ImGui.Button("OK", new Vector2(110f, 0f)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void DrawKickedByAdminModal()
    {
        ImGui.SetNextWindowSize(new Vector2(380f, 0f), ImGuiCond.Appearing);
        // NoTitleBar: removes ImGui's automatic title bar entirely so the
        // modal can never display the popup ID as its title under any
        // circumstance — the "Visible##internal-id" split-label form is
        // correct ImGui syntax (the same pattern the rest of this file
        // uses for Leave##voice / Retry##mic-retry / etc.), but a stale
        // cached build with the old single-string label would show
        // "kicked-by-admin-modal" verbatim in the title bar. Belt-and-
        // suspenders: kill the title bar instead. The red
        // "Kicked from voice chat" text inside the modal body now serves
        // as the visible heading; the user dismisses via the OK button.
        if (ImGui.BeginPopupModal("You have been kicked##kicked-by-admin-modal", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoTitleBar))
        {
            ImGui.TextColored(new Vector4(0.95f, 0.45f, 0.45f, 1f), "Kicked from voice chat");
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(this.kickModalMessage)
                ? "You have been kicked from the voice chat by an admin."
                : this.kickModalMessage);
            ImGui.Spacing();
            if (ImGui.Button("OK", new Vector2(110f, 0f)))
            {
                // Clear the underlying error so the modal doesn't immediately
                // re-arm; future kicks will set the error again and re-trigger.
                this.voiceRoomManager.SignalingChannel?.ClearLatestError();
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private bool IsReportQuotaExceeded(out TimeSpan retryAfter)
    {
        var now = DateTime.UtcNow;
        while (this.reportTimestampsUtc.Count > 0 && now - this.reportTimestampsUtc.Peek() >= TimeSpan.FromHours(1))
        {
            this.reportTimestampsUtc.Dequeue();
        }

        if (this.reportTimestampsUtc.Count < 5)
        {
            retryAfter = TimeSpan.Zero;
            return false;
        }

        retryAfter = TimeSpan.FromHours(1) - (now - this.reportTimestampsUtc.Peek());
        return true;
    }

    private async System.Threading.Tasks.Task SubmitReportAsync(
        string reporter,
        string reported,
        string violation,
        string details)
    {
        if (this.reportSubmitting)
        {
            return;
        }

        if (IsReportQuotaExceeded(out _))
        {
            this.reportStatusMessage = "Reporting is temporarily unavailable.";
            return;
        }

        this.reportSubmitting = true;
        this.reportStatusMessage = string.Empty;

        try
        {
            var (reporterFirst, reporterLast, reporterServer) = ParseCharacterName(reporter);
            var (reportedFirst, reportedLast, reportedServer) = ParseCharacterName(reported);

            // Flat payload — the signaling server owns the Discord-embed envelope.
            var payload = new
            {
                reporter = $"{reporterFirst} {reporterLast} @ {reporterServer}",
                reported = $"{reportedFirst} {reportedLast} @ {reportedServer}",
                violation,
                channel = GetVoiceChannelDisplayName(),
                details = string.IsNullOrWhiteSpace(details) ? string.Empty : details.Trim(),
            };

            var endpoint = BuildReportEndpointUrl();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(payload),
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", EmbeddedConfig.SignalingServerToken);

            using var response = await this.httpClient.SendAsync(request).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                this.reportTimestampsUtc.Enqueue(DateTime.UtcNow);
                this.reportStatusMessage = string.Empty;
                this.reportDetails = string.Empty;
                this.closeReportModalRequested = true;
                this.openReportSubmittedModalRequested = true;
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                this.reportStatusMessage = $"Report failed ({(int)response.StatusCode}).";
                if (!string.IsNullOrWhiteSpace(body))
                {
                    this.dalamud.Log.Error("Report submission response: {0}", body);
                }
            }
        }
        catch (Exception ex)
        {
            this.reportStatusMessage = "Report failed due to a network error.";
            this.dalamud.Log.Error(ex, "Failed to submit report.");
        }
        finally
        {
            this.reportSubmitting = false;
        }
    }

    private static string BuildReportEndpointUrl()
    {
        var baseUrl = EmbeddedConfig.SignalingServerUrl ?? string.Empty;
        var trimmed = baseUrl.TrimEnd('/');
        return string.Concat(trimmed, ReportRelativePath);
    }

    private static (string first, string last, string server) ParseCharacterName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ("Unknown", "Player", "Unknown");
        }

        var cleaned = raw.Replace(",", string.Empty).Trim();
        var atSplit = cleaned.Split('@', 2, StringSplitOptions.TrimEntries);
        var namePart = atSplit[0].Trim();
        var serverPart = atSplit.Length > 1 && !string.IsNullOrWhiteSpace(atSplit[1]) ? atSplit[1].Trim() : "Unknown";

        var nameTokens = namePart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var first = nameTokens.Length > 0 ? nameTokens[0] : "Unknown";
        var last = nameTokens.Length > 1 ? nameTokens[1] : "Unknown";
        return (first, last, serverPart);
    }

    // ── footer: status + local-player identity + audio buttons ───────────────
    private void DrawDeviceControlsBar()
    {
        var inRoom = this.voiceRoomManager.InRoom;
        var connecting = this.voiceRoomManager.SignalingChannel?.Connecting ?? false;
        var reconnecting = this.voiceRoomManager.IsReconnecting;
        var reconnectAttempt = this.voiceRoomManager.ReconnectAttempt;

        if (inRoom)
        {
            // Wi-Fi-style 3-bar strength indicator next to the channel name.
            // Color + lit count are derived from SignalingChannel.LastLatencyMs.
            var latency = this.voiceRoomManager.SignalingChannel?.LastLatencyMs;
            DrawConnectionStrengthBars(latency);
            ImGui.SameLine();
            ImGui.TextUnformatted(GetVoiceChannelDisplayName());
        }
        else if (reconnecting)
        {
            ImGui.TextColored(Vector4Colors.Orange, $"Reconnecting ({reconnectAttempt}/5)...");
        }
        else if (connecting)
        {
            ImGui.TextColored(Vector4Colors.Orange, "Connecting...");
        }
        else
        {
            ImGui.TextColored(Vector4Colors.Gray, "Not connected");
        }

        ImGui.Spacing();
        DrawLocalPlayerIdentity();
    }

    /// <summary>
    /// Render a Wi-Fi-style 3-bar connection strength indicator. Latency
    /// thresholds (tuned for real-time voice):
    ///   <list type="bullet">
    ///     <item>&lt; 80 ms → 3 bars lit, green</item>
    ///     <item>80–199 ms → 2 bars lit, orange</item>
    ///     <item>≥ 200 ms → 1 bar lit, red</item>
    ///     <item>no sample yet → all bars dim/gray</item>
    ///   </list>
    /// Hover shows the latest median latency in ms — matching the tooltip the
    /// old "Connected to …" text used to expose.
    /// </summary>
    private void DrawConnectionStrengthBars(int? latencyMs)
    {
        int lit;
        Vector4 color;
        if (!latencyMs.HasValue)        { lit = 0; color = Vector4Colors.Gray; }
        else if (latencyMs.Value < 80)  { lit = 3; color = Vector4Colors.Green; }
        else if (latencyMs.Value < 200) { lit = 2; color = Vector4Colors.Orange; }
        else                            { lit = 1; color = Vector4Colors.Red; }

        // Unlit bars: dark-tinted version of the live color so they read as
        // "off but related". The all-dim (no-sample) state uses a neutral gray
        // instead so it doesn't look like a red signal.
        var dim = lit == 0
            ? new Vector4(0.35f, 0.35f, 0.35f, 0.6f)
            : new Vector4(color.X * 0.3f, color.Y * 0.3f, color.Z * 0.3f, 0.6f);

        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var lineH = ImGui.GetTextLineHeight();
        var barW = MathF.Max(2f, lineH * 0.18f);
        var gap = MathF.Max(1f, lineH * 0.10f);
        var baseY = origin.Y + lineH;        // bars sit on the text baseline

        for (int i = 0; i < 3; i++)
        {
            var h = lineH * (0.35f + 0.30f * i);   // 35%, 65%, 95% of line height
            var x = origin.X + i * (barW + gap);
            var c = ImGui.ColorConvertFloat4ToU32(i < lit ? color : dim);
            dl.AddRectFilled(new Vector2(x, baseY - h), new Vector2(x + barW, baseY), c, 1f);
        }

        // Reserve layout space so SameLine() lands cleanly after the widget.
        // Dummy is also the hit-target for the tooltip below.
        var totalW = 3 * barW + 2 * gap;
        ImGui.Dummy(new Vector2(totalW, lineH));

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(latencyMs.HasValue
                ? $"Connection latency: {latencyMs.Value} ms"
                : "Connection latency is still being measured.");
        }
    }

    private void DrawLocalPlayerIdentity()
    {
        this.configuration.SyncActiveCharacterProfile();
        var name = GetLocalPlayerDisplayName();
        var jobLine = GetPlayerJobAndLevelLine(name);
        // Custom name color is a supporter perk. Non-premium falls back to
        // plain white so the footer looks the same as everyone else's view.
        var profileColor = this.configuration.IsLocalPremium
            ? Vector4Colors.HexToVector4(this.configuration.UserColor)
            : new Vector4(1f, 1f, 1f, 1f);

        var leftX = ImGui.GetCursorScreenPos().X;
        var topY = ImGui.GetCursorScreenPos().Y;

        // Handle status editing UI
        if (this.editingStatus)
        {
            ImGui.TextColored(Vector4Colors.Header, "Edit Status:");
            ImGui.SetNextItemWidth(200f);
            var statusInput = this.statusEditBuffer;
            if (ImGui.InputText("##StatusInput", ref statusInput, 200, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                // Validate: no links allowed
                if (Utils.ContainsLinks(statusInput))
                {
                    ImGui.TextColored(new Vector4(1, 0.5f, 0.5f, 1), "Links are not allowed in status");
                }
                else
                {
                    this.configuration.UserStatus = statusInput;
                    this.configuration.Save();
                    this.editingStatus = false;
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel##StatusEdit"))
            {
                this.editingStatus = false;
            }
        }
        else
        {
            // Draw player name. Profile / status-edit interactions are gated
            // behind ShowProfileFeature so non-premium-era users don't see a
            // tooltip teasing a feature that doesn't work yet. The const-folded
            // false makes the body unreachable; CS0162 is suppressed so the
            // feature-flag intent stays visible.
            ImGui.TextColored(profileColor, name);
#pragma warning disable CS0162
            if (ShowProfileFeature)
            {
                var localPremium = this.configuration.IsLocalPremium;
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(localPremium
                        ? "Click to view your profile. Double-click to edit your status."
                        : "Profile and status are supporter perks. Open Settings → Profile to link your Discord.");
                }
                if (localPremium && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && ImGui.IsItemHovered())
                {
                    this.editingStatus = true;
                    this.statusEditBuffer = this.configuration.UserStatus;
                }
                else if (localPremium && ImGui.IsItemClicked())
                {
                    this.profileTargetUser = name;
                    this.profileTargetIsSelf = true;
                    this.openProfileModalRequested = true;
                }
            }
#pragma warning restore CS0162

            ImGui.TextColored(Vector4Colors.Gray, jobLine);

            // Display current status if set
            if (!string.IsNullOrEmpty(this.configuration.UserStatus))
            {
                ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1.0f), $"Status: {this.configuration.UserStatus}");
            }
        }

        var textBottomY = ImGui.GetCursorScreenPos().Y;
        var textHeight = textBottomY - topY;

        // All three footer buttons (mic / headphones / cog, plus the optional
        // end-call) render at the same outer frame size so they line up
        // visually. The cog and end-call used to look smaller than mic /
        // headphones because mic / headphones went through ImGui.ImageButton
        // (which adds frame padding around the image), while the icon buttons
        // were a fixed ImGui.Button of slightly smaller size.
        var buttonSize = new Vector2(32, 32);
        var endButtonWidth = this.voiceRoomManager.InRoom ? buttonSize.X : 0f;
        var spacing = 4f;
        var totalWidth = buttonSize.X * 3 + spacing * 2 + endButtonWidth + (this.voiceRoomManager.InRoom ? spacing : 0f);
        var rightX = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        // 15f right-edge inset (was 10f) — nudges the cluster ~5px left so it
        // doesn't sit flush against the window's right border.
        var targetX = rightX - totalWidth - 15f;

        var iconRowY = topY + Math.Max(0f, (textHeight - buttonSize.Y) * 0.5f);
        ImGui.SetCursorScreenPos(new Vector2(targetX, iconRowY));
        DrawInlineAudioActionButtons("footer", buttonSize, spacing);

        ImGui.SetCursorScreenPos(new Vector2(leftX, Math.Max(textBottomY, iconRowY + buttonSize.Y)));
    }

    private void DrawInlineAudioActionButtons(string idSuffix, Vector2 buttonSize, float spacing)
    {
        using var id = ImRaii.PushId($"controls-{idSuffix}");

        if (this.voiceRoomManager.InRoom)
        {
            using var bc = ImRaii.PushColor(ImGuiCol.Button, Vector4Colors.LeaveButton);
            using var bh = ImRaii.PushColor(ImGuiCol.ButtonHovered, Vector4Colors.LeaveButtonHover);
            if (DrawIconButton("end-call", FontAwesomeIcon.Times.ToIconString(), buttonSize))
            {
                this.leaveVoiceRoom.OnNext(Unit.Default);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Leave current voice channel");
            }
            ImGui.SameLine(0, spacing);
        }

        if (DrawImageButton("mic", GetMicrophoneImage(this.audioDeviceController.MuteMic, true), buttonSize))
        {
            this.muteMic.OnNext(!this.audioDeviceController.MuteMic);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(this.audioDeviceController.MuteMic ? "Unmute microphone" : "Mute microphone");
        }

        ImGui.SameLine(0, spacing);

        if (DrawImageButton("headphones", GetHeadphonesImage(this.audioDeviceController.Deafen, true), buttonSize))
        {
            this.deafen.OnNext(!this.audioDeviceController.Deafen);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(this.audioDeviceController.Deafen ? "Undeafen" : "Deafen all incoming audio");
        }

        ImGui.SameLine(0, spacing);

        if (DrawIconButton("settings", FontAwesomeIcon.Cog.ToIconString(), buttonSize))
        {
            this.configWindow.Visible = !this.configWindow.Visible;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Open settings");
        }
    }

    /// <summary>
    /// Draws a Font Awesome glyph centered inside an <c>ImGui.Button</c> of
    /// the requested <paramref name="buttonSize"/>. Returns true on click.
    /// </summary>
    private static bool DrawIconButton(string idSuffix, string iconText, Vector2 buttonSize)
    {
        var clicked = ImGui.Button($"##{idSuffix}", buttonSize);

        using var iconFont = ImRaii.PushFont(UiBuilder.IconFont);
        var iconSize = ImGui.CalcTextSize(iconText);

        var buttonMin = ImGui.GetItemRectMin();
        var buttonRect = ImGui.GetItemRectSize();
        var iconPos = new Vector2(
            buttonMin.X + (buttonRect.X - iconSize.X) * 0.5f,
            buttonMin.Y + (buttonRect.Y - iconSize.Y) * 0.5f);

        ImGui.GetWindowDrawList().AddText(iconPos, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), iconText);
        return clicked;
    }

    /// <summary>
    /// Mirror of <see cref="DrawIconButton"/> but for a texture image: draws
    /// the image centered inside an <c>ImGui.Button</c> of the requested
    /// <paramref name="buttonSize"/>. Unifies the outer frame size with the
    /// FontAwesome icon buttons so the whole footer cluster has consistent
    /// dimensions (ImGui.ImageButton's own frame padding made the image
    /// buttons render visibly larger than the icon buttons).
    /// </summary>
    private static bool DrawImageButton(string idSuffix, ImTextureID image, Vector2 buttonSize)
    {
        var clicked = ImGui.Button($"##{idSuffix}", buttonSize);

        // Inset the image by ~25% of the button size for the same visual
        // weight as the Font Awesome glyphs in DrawIconButton.
        var imageSize = buttonSize * 0.70f;
        var buttonMin = ImGui.GetItemRectMin();
        var buttonRect = ImGui.GetItemRectSize();
        var imagePos = new Vector2(
            buttonMin.X + (buttonRect.X - imageSize.X) * 0.5f,
            buttonMin.Y + (buttonRect.Y - imageSize.Y) * 0.5f);

        ImGui.GetWindowDrawList().AddImage(image, imagePos, imagePos + imageSize);
        return clicked;
    }

    private static string FormatPlayerDisplayName(string playerName)
    {
        // peerIds arrive as "First Last@World". Render them as
        // "First Last, @World" — comma + space + the original @.
        // The previous version inserted ", @" BEFORE the @ rather than
        // replacing it, producing the visible ", @@" double-at.
        var atIndex = playerName.IndexOf('@');
        if (atIndex <= 0) return playerName;
        return playerName.Insert(atIndex, ", ");
    }

    private string GetPlayerJobAndLevelLine(string playerName)
    {
        try
        {
            var player = this.dalamud.ObjectTable.GetPlayers().FirstOrDefault(p => p.GetPlayerFullName() == playerName);

            if (player == null)
            {
                return "Class Unknown - Lv ?";
            }

            var job = player.ClassJob.Value.Abbreviation.ExtractText();
            if (string.IsNullOrWhiteSpace(job))
            {
                job = "Class";
            }

            return $"{job} - Lv {player.Level}";
        }
        catch
        {
            return "Class Unknown - Lv ?";
        }
    }

    private ImTextureID GetMicrophoneImage(bool muted, bool self)
    {
        var imageName = muted ? (self ? "microphone-muted-self.png" : "microphone-muted.png") : "microphone.png";
        return this.dalamud.TextureProvider.GetFromFile(this.dalamud.PluginInterface.GetResourcePath(imageName)).GetWrapOrDefault()?.Handle ?? default;
    }

    private ImTextureID GetHeadphonesImage(bool deafened, bool self)
    {
        var imageName = deafened ? (self ? "headphones-deafen-self.png" : "headphones-deafen.png") : "headphones.png";
        return this.dalamud.TextureProvider.GetFromFile(this.dalamud.PluginInterface.GetResourcePath(imageName)).GetWrapOrDefault()?.Handle ?? default;
    }

    private static (string displayName, string worldName) SplitCharacterName(string characterName)
    {
        if (string.IsNullOrWhiteSpace(characterName))
        {
            return ("Unknown", string.Empty);
        }

        var atIndex = characterName.IndexOf('@');
        if (atIndex <= 0 || atIndex >= characterName.Length - 1)
        {
            return (characterName, string.Empty);
        }

        var displayName = characterName[..atIndex].Trim();
        var worldName = characterName[(atIndex + 1)..].Trim();
        return (string.IsNullOrWhiteSpace(displayName) ? characterName : displayName, string.IsNullOrWhiteSpace(worldName) ? string.Empty : $"@{worldName}");
    }

    private Peer? GetProfileForUser(string user, bool isSelf)
    {
        if (isSelf)
        {
            // Local user's profile is only "visible" (to themselves and others)
            // when they're a supporter. Hiding it for the local user too keeps
            // the inspect card consistent — no name color, no Twitch pill, no
            // pronoun chip when not premium.
            if (!this.configuration.IsLocalPremium)
            {
                return null;
            }
            return new Peer
            {
                PeerId = user,
                IsPremium = true,
                Pronoun = this.configuration.UserPronoun,
                Biography = this.configuration.UserBiography,
                Status = this.configuration.UserStatus,
                Color = this.configuration.UserColor,
                TwitchLink = this.configuration.TwitchLink,
            };
        }

        if (this.voiceRoomManager.Presence != null &&
            this.voiceRoomManager.Presence.Peers.TryGetValue(user, out var peer))
        {
            // Server-stamped IsPremium is authoritative. The server also blanks
            // profile fields for non-premium peers before broadcasting, so this
            // is defense in depth.
            return peer.IsPremium ? peer : null;
        }

        return null;
    }

    private void DrawProfilePopupHeader(string userName, Vector4 nameColor, Peer? profile)
    {
        var (displayName, worldName) = SplitCharacterName(userName);
        var headerLeftX = ImGui.GetCursorPosX();

        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 10f))
        {
            // Keep the top row (name/world and pronoun) isolated from the pill row.
            // This avoids any table clipping/cursor carryover affecting the pills.
            {
                using var headerTable = ImRaii.Table("profile-header", 2, ImGuiTableFlags.SizingFixedFit);
                if (headerTable)
                {
                    ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthStretch, 1f);
                    ImGui.TableSetupColumn("pronoun", ImGuiTableColumnFlags.WidthFixed);
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 2f)))
                    {
                        ImGui.TextColored(nameColor, displayName);
                        if (!string.IsNullOrWhiteSpace(worldName))
                        {
                            ImGui.TextColored(nameColor, worldName);
                        }
                    }

                    ImGui.TableNextColumn();
                    if (profile != null && !string.IsNullOrWhiteSpace(profile.Pronoun) && profile.Pronoun != "Unset")
                    {
                        var pronounText = profile.Pronoun;
                        var pronounWidth = ImGui.CalcTextSize(pronounText).X + 18f;
                        var avail = ImGui.GetContentRegionAvail().X;
                        if (avail > pronounWidth)
                        {
                            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail - pronounWidth));
                        }

                        using var chipBg = ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.26f, 0.27f, 0.30f, 0.95f));
                        using var chipText = ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.92f, 0.92f, 0.92f, 1f));
                        ImGui.Button(pronounText, new Vector2(pronounWidth, 0f));
                    }
                }
            }

            ImGui.SetCursorPosX(headerLeftX);

            var classLevel = GetPlayerJobAndLevelLine(userName).Replace(" - ", " · ");
            var distanceText = string.Empty;
            if (this.voiceRoomManager.TrackedPlayers.TryGetValue(userName, out var tracked) && tracked is not null && !float.IsNaN(tracked.Distance))
            {
                distanceText = $"{tracked.Distance:F1}y";
            }

            DrawTagPill("profile-class-pill", classLevel);
            if (!string.IsNullOrEmpty(distanceText))
            {
                ImGui.SameLine(0f, 8f);
                DrawTagPill("profile-distance-pill", distanceText);
            }
        }
    }

    private static void DrawProfileStatusSection(string? status)
    {
        ImGui.TextColored(new Vector4(0.66f, 0.66f, 0.66f, 1f), "STATUS");
        if (string.IsNullOrWhiteSpace(status))
        {
            ImGui.TextColored(new Vector4(0.72f, 0.72f, 0.72f, 1f), "No status set.");
            return;
        }

        ImGui.TextWrapped(status);
    }

    private static void DrawProfileAboutSection(string? bio)
    {
        ImGui.TextColored(new Vector4(0.66f, 0.66f, 0.66f, 1f), "ABOUT");
        if (string.IsNullOrWhiteSpace(bio))
        {
            ImGui.TextColored(new Vector4(0.72f, 0.72f, 0.72f, 1f), "No bio set.");
            return;
        }

        ImGui.TextWrapped(bio);
    }

    private void DrawProfileTwitchSection(string twitchLink)
    {
        DrawTwitchPill(twitchLink);
    }

    private static void DrawProfileSectionDivider()
    {
        ImGui.Dummy(new Vector2(0f, 12f));
        ImGui.Separator();
        ImGui.Dummy(new Vector2(0f, 12f));
    }

    private static void DrawTagPill(string id, string text)
    {
        var horizontalPadding = 5f;
        using var pillBg = ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.08f, 0.08f, 0.08f, 0.92f));
        using var pillHover = ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.08f, 0.08f, 0.08f, 0.92f));
        using var pillActive = ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.08f, 0.08f, 0.08f, 0.92f));
        using var pillText = ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.93f, 0.93f, 0.93f, 1f));
        using var pillPadding = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(horizontalPadding, 1f));
        var pillWidth = ImGui.CalcTextSize(text).X + (horizontalPadding * 2f);
        ImGui.Button($"{text}##{id}", new Vector2(pillWidth, 20f));
    }

    /// <summary>
    /// Draws a Twitch pill next to a player name.
    /// </summary>
    private void DrawTwitchPill(string twitchLink)
    {
        var pillText = "Twitch";
        var textSize = ImGui.CalcTextSize(pillText);
        var padding = new Vector2(6f, 2f);
        var pillSize = textSize + padding * 2;

        var cursorPos = ImGui.GetCursorScreenPos();
        var pillStart = cursorPos;
        var pillEnd = pillStart + pillSize;

        var drawList = ImGui.GetWindowDrawList();
        var bgColor = ImGui.GetColorU32(new Vector4(0.55f, 0.3f, 0.85f, 0.9f)); // Purple
        var textColor = ImGui.GetColorU32(new Vector4(1.0f, 1.0f, 1.0f, 1.0f));

        drawList.AddRectFilled(pillStart, pillEnd, bgColor, 4f);
        drawList.AddText(pillStart + padding, textColor, pillText);

        ImGui.Dummy(pillSize);

        // Handle click to open Twitch link
        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = twitchLink, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                this.dalamud.Log.Error($"Failed to open Twitch link: {ex.Message}");
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"Click to visit {twitchLink}");
        }
    }
}
