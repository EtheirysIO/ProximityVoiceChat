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
using System.Linq;
using System.Numerics;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using EtheirysProximityVoiceChat.Audio;
using EtheirysProximityVoiceChat.Input;
using EtheirysProximityVoiceChat.Log;
using EtheirysProximityVoiceChat.Premium;
using EtheirysProximityVoiceChat.UI.Util;
using Reactive.Bindings;
using WindowsInput.Events;

namespace EtheirysProximityVoiceChat.UI.View;

public sealed class ConfigWindow : Window, IPluginUIView, IDisposable
{
    private readonly WindowSystem windowSystem;

    /// <summary>
    /// Independent visibility for the standalone Settings window. Toggled by
    /// the main window's footer gear button and by Dalamud's plugin-tile
    /// "Configuration" button (wired in <see cref="PluginUIContainer"/>).
    /// Mirrors <see cref="Window.IsOpen"/> so either path stays in sync.
    /// </summary>
    private bool visible;
    public bool Visible
    {
        get => this.visible;
        set
        {
            this.visible = value;
            IsOpen = value;
        }
    }

    public IReactiveProperty<bool> PlayingBackMicAudio { get; } = new ReactiveProperty<bool>();
    public IReactiveProperty<bool> PushToTalk { get; } = new ReactiveProperty<bool>();
    public IReactiveProperty<bool> SuppressNoise { get; } = new ReactiveProperty<bool>();
    public IReactiveProperty<int> VadSensitivity { get; } = new ReactiveProperty<int>();
    public IReactiveProperty<Keybind> KeybindBeingEdited { get; } = new ReactiveProperty<Keybind>();
    public IObservable<Keybind> ClearKeybind => clearKeybind.AsObservable();
    private readonly Subject<Keybind> clearKeybind = new();

    public IReactiveProperty<float> MasterVolume { get; } = new ReactiveProperty<float>();
    public IReactiveProperty<float> InputBoost { get; } = new ReactiveProperty<float>();
    public IReactiveProperty<AudioFalloffModel.FalloffType> AudioFalloffType { get; } = new ReactiveProperty<AudioFalloffModel.FalloffType>();
    public IReactiveProperty<float> AudioFalloffMinimumDistance { get; } = new ReactiveProperty<float>();
    public IReactiveProperty<float> AudioFalloffMaximumDistance { get; } = new ReactiveProperty<float>();
    public IReactiveProperty<float> AudioFalloffFactor { get; } = new ReactiveProperty<float>();
    public IReactiveProperty<bool> EnableSpatialization { get; } = new ReactiveProperty<bool>();
    public IReactiveProperty<bool> MuteDeadPlayers { get; } = new ReactiveProperty<bool>();
    public IReactiveProperty<int> MuteDeadPlayersDelayMs { get; } = new ReactiveProperty<int>();
    public IReactiveProperty<bool> UnmuteAllIfDead { get; } = new ReactiveProperty<bool>();
    public IReactiveProperty<bool> MuteOutOfMapPlayers { get; } = new ReactiveProperty<bool>();

    public IReactiveProperty<bool> PlayRoomJoinAndLeaveSounds { get; } = new ReactiveProperty<bool>();
    public IReactiveProperty<bool> KeybindsRequireGameFocus { get; } = new ReactiveProperty<bool>();
    public IReactiveProperty<bool> ShowFullPlayerNames { get; } = new ReactiveProperty<bool>();
    public IReactiveProperty<bool> ShowNameplateIcons { get; } = new ReactiveProperty<bool>();
    public IReactiveProperty<bool> PrintLogsToChat { get; } = new ReactiveProperty<bool>();
    public IReactiveProperty<int> MinimumVisibleLogLevel { get; } = new ReactiveProperty<int>();

    // Profile Settings
    public IReactiveProperty<string> UserBiography { get; } = new ReactiveProperty<string>(string.Empty);
    public IReactiveProperty<string> UserPronoun { get; } = new ReactiveProperty<string>("Unset");
    public IReactiveProperty<string> UserColor { get; } = new ReactiveProperty<string>("FFFFFF");
    public IReactiveProperty<string> TwitchLink { get; } = new ReactiveProperty<string>(string.Empty);
    public IReactiveProperty<string> UserStatus { get; } = new ReactiveProperty<string>(string.Empty);

    private string profileSaveStatusMessage = string.Empty;
    private Vector4 profileSaveStatusColor = new Vector4(0.55f, 0.90f, 0.55f, 1f);

    public IObservable<Unit> ResetDeviceSettings => resetDeviceSettings.AsObservable();
    private readonly Subject<Unit> resetDeviceSettings = new();
    public IObservable<Unit> ResetFalloffSettings => resetFalloffSettings.AsObservable();
    private readonly Subject<Unit> resetFalloffSettings = new();

    private string[]? inputDevices;
    private string[]? outputDevices;
    private string loadedProfileKey = string.Empty;
    /// <summary>
    /// Timestamp (<see cref="Environment.TickCount64"/>) of the most recent
    /// frame where <see cref="IAudioDeviceController.RecordingDataHasActivity"/>
    /// was true. Used by the inline pickup indicator next to the VAD slider so
    /// the dot doesn't flicker during natural phoneme gaps (the underlying VAD
    /// oscillates per 20ms frame).
    /// </summary>
    private long lastPickupTickMs;
    /// <summary>
    /// Hold window for the pickup indicator. Matches
    /// <c>NameplateVoiceOverlay.SpeakingHoldMs</c> so the visual cadence is
    /// consistent across the plugin.
    /// </summary>
    private const long PickupIndicatorHoldMs = 220;
    private readonly Vector4[] colorPalette = new[]
    {
        new Vector4(1.0f, 0.0f, 0.0f, 1.0f), // Red
        new Vector4(1.0f, 0.5f, 0.0f, 1.0f), // Orange
        new Vector4(1.0f, 1.0f, 0.0f, 1.0f), // Yellow
        new Vector4(0.0f, 1.0f, 0.0f, 1.0f), // Green
        new Vector4(0.0f, 1.0f, 1.0f, 1.0f), // Cyan
        new Vector4(0.0f, 0.0f, 1.0f, 1.0f), // Blue
        new Vector4(1.0f, 0.0f, 1.0f, 1.0f), // Magenta
        new Vector4(1.0f, 1.0f, 1.0f, 1.0f), // White
    };

    private readonly IAudioDeviceController audioDeviceController;
    private readonly VoiceRoomManager voiceRoomManager;
    private readonly Configuration configuration;
    private readonly PremiumLinker premiumLinker;
    private readonly string[] falloffTypes;
    private readonly string[] allLoggingLevels;

    // Direct application logic is being placed into this UI script because this is debug UI
    public ConfigWindow(
        WindowSystem windowSystem,
        PushToTalkController pushToTalkController,
        VoiceRoomManager voiceRoomManager,
        Configuration configuration,
        PremiumLinker premiumLinker) : base($"{PluginInitializer.Name} Settings", ImGuiWindowFlags.NoCollapse)
    {
        this.windowSystem = windowSystem;
        this.audioDeviceController = pushToTalkController;
        this.voiceRoomManager = voiceRoomManager;
        this.configuration = configuration;
        this.premiumLinker = premiumLinker;
        this.falloffTypes = Enum.GetNames<AudioFalloffModel.FalloffType>();
        this.allLoggingLevels = [.. LogLevel.AllLoggingLevels.Select(l => l.Name)];

        // ConfigWindow has to be wide enough to fit common Windows audio
        // device names in the Audio Devices combos (e.g.
        // "Microphone (2- Arctis Nova 7 Gen 2)"). ImGui locks combo popup
        // width to combo widget width, and combo widget width is bounded
        // by the window, so the window itself is the only lever.
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(540, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(560, 520);
        SizeCondition = ImGuiCond.FirstUseEver;

        LoadProfileValuesFromConfiguration();
        windowSystem.AddWindow(this);
    }

    public void Dispose()
    {
        this.windowSystem.RemoveWindow(this);
    }

    public override void Draw()
    {
        if (this.configuration.SyncActiveCharacterProfile())
        {
            this.loadedProfileKey = string.Empty;
        }

        var activeProfileKey = this.configuration.GetActiveCharacterKey();
        if (!string.Equals(this.loadedProfileKey, activeProfileKey, StringComparison.Ordinal))
        {
            LoadProfileValuesFromConfiguration();
        }

        if (!this.visible)
        {
            KeybindBeingEdited.Value = Keybind.None;
            return;
        }

        // We manage our own Begin/End (matching MainWindow's pattern) so the
        // titlebar X button writes back through the same `visible` field that
        // PluginUIContainer toggles via Visible — keeping both paths in sync.
        if (ImGui.Begin(WindowName, ref this.visible, Flags))
        {
            // Also propagate to IsOpen so anything reading the Window base
            // (e.g. a future migration to WindowSystem.Draw) sees a consistent
            // value when the user clicks the title-bar close button.
            IsOpen = this.visible;
            DrawContents();
        }
        ImGui.End();
    }

    private void LoadProfileValuesFromConfiguration()
    {
        this.UserBiography.Value = this.configuration.UserBiography;
        this.UserPronoun.Value = this.configuration.UserPronoun ?? string.Empty;
        this.UserColor.Value = Utils.NormalizeHexColor(this.configuration.UserColor);
        this.TwitchLink.Value = this.configuration.TwitchLink;
        this.UserStatus.Value = this.configuration.UserStatus;
        this.loadedProfileKey = this.configuration.GetActiveCharacterKey();
        this.profileSaveStatusMessage = string.Empty;
    }

    // Feature flag: while the Premium gate is still being plumbed (no Discord
    // OAuth credentials are live on the signaling server yet, no users have
    // premium yet) we hide the Profile tab entirely. Flip to true when the
    // gate goes live to surface the tab again.
    private const bool ShowProfileFeature = false;

    private void DrawContents()
    {
        using var tabs = ImRaii.TabBar("pvc-config-tabs");
        if (!tabs) return;

        // ShowProfileFeature is a compile-time const false until the premium
        // profile gate goes live — see field declaration above. The const-fold
        // makes the body unreachable, so suppress CS0162 to keep the gate
        // visible in source without compiler noise.
#pragma warning disable CS0162
        if (ShowProfileFeature) DrawProfileTab();
#pragma warning restore CS0162
        DrawDeviceTab();
        DrawFalloffTab();
        DrawMiscTab();
    }

    // ── Audio Devices ────────────────────────────────────────────────────────
    private void DrawDeviceTab()
    {
        using var deviceTab = ImRaii.TabItem("Audio Devices");
        if (!deviceTab) return;

        var sectionColor = new Vector4(0.6f, 0.8f, 1.0f, 1.0f);
        float indent = ImGui.GetFontSize();
        float contentWidth = ImGui.GetContentRegionAvail().X;

        // Audio Input
        ImGui.Spacing();
        ImGui.TextColored(sectionColor, "Audio Input");
        ImGui.Separator();
        ImGui.Spacing();

        using (ImRaii.PushIndent(indent))
        {
            this.inputDevices ??= [.. this.audioDeviceController.GetAudioRecordingDevices()];
            var inputDeviceIndex = this.audioDeviceController.AudioRecordingDeviceIndex + 1;
            if (DrawDeviceCombo("##InputDevice", ref inputDeviceIndex, this.inputDevices))
            {
                this.audioDeviceController.AudioRecordingDeviceIndex = inputDeviceIndex - 1;
            }

            // Live mic-input level meter. Updates each capture frame (50 Hz),
            // smoothed by ImGui's progress-bar rendering. Lets users confirm
            // "Windows is actually delivering my voice to the plugin" without
            // having to enable mic playback or coordinate with another peer.
            // Flashes red when the post-boost signal clips.
            DrawMicInputLevelMeter();

            // Input boost slider — multiplicative gain applied to the raw
            // captured PCM before RNNoise/VAD/Opus. Positioned directly
            // under the meter so users can dial it up while watching the
            // bar grow (and turn red on overshoot).
            DrawInputBoostSlider();

            // Error banner: only shown when the capture path has a live
            // exception. The Retry button restarts the capture device so
            // users can recover (e.g. after re-enabling the mic in Windows
            // Sound Control Panel) without a plugin reload.
            DrawMicErrorBanner();

            ImGui.Spacing();

            var suppressNoise = this.SuppressNoise.Value;
            if (ImGui.Checkbox("Noise Suppression", ref suppressNoise))
            {
                this.SuppressNoise.Value = suppressNoise;
            }

            // Pickup sensitivity slider + live indicator. Slider maps directly
            // to WebRtcVadSharp.OperatingMode (0..3). Format string is computed
            // each frame from VadStopName so dragging the slider updates the
            // displayed name in place of the raw integer.
            var vadMode = this.VadSensitivity.Value;
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.55f);
            if (ImGui.SliderInt("##VadSensitivity", ref vadMode, 0, 3, VadStopName(vadMode)))
            {
                this.VadSensitivity.Value = vadMode;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Quality (0): picks up quieter speech, lets more through.\n" +
                    "Aggressive (2): default, balanced.\n" +
                    "Very Aggressive (3): rejects more background noise, requires louder speech.\n" +
                    "\n" +
                    "Requires Noise Suppression to be enabled.\n" +
                    "Push-to-Talk overrides voice-activated pickup.");
            }
            ImGui.SameLine();
            DrawPickupIndicator();
            ImGui.SameLine();
            ImGui.TextUnformatted("Pickup sensitivity");

            var pushToTalk = this.PushToTalk.Value;
            if (ImGui.Checkbox("Push to Talk", ref pushToTalk))
            {
                this.PushToTalk.Value = pushToTalk;
            }
            if (pushToTalk)
            {
                ImGui.SameLine();
                DrawKeybindEdit(Keybind.PushToTalk, this.configuration.PushToTalkBinding, "Keybind");

                // Release delay: when the PTT key comes up, keep transmitting
                // for this many ms before going silent. Preserves the trailing
                // consonant of the last word; matches the SpeechHangoverFrames
                // tail on the VAD side. Honoured by PushToTalkController.
                var releaseDelay = this.configuration.PushToTalkReleaseDelayMs;
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.55f);
                if (ImGui.SliderInt("##PttReleaseDelay", ref releaseDelay, 0, 500, "%d ms"))
                {
                    this.configuration.PushToTalkReleaseDelayMs = Math.Clamp(releaseDelay, 0, 2000);
                    this.configuration.Save();
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        "How long to keep the mic open after the PTT key is released.\n" +
                        "Prevents the trailing sound of your last word from being clipped.\n" +
                        "Set to 0 for an immediate cutoff.");
                }
                ImGui.SameLine();
                ImGui.TextUnformatted("PTT release delay");
            }
            else if (this.KeybindBeingEdited.Value == Keybind.PushToTalk)
            {
                this.KeybindBeingEdited.Value = Keybind.None;
            }

            // ── Network / audio transport ─────────────────────────────
            ImGui.Spacing();
            DrawAudioTransportControls();
        }

        ImGui.Spacing();

        // Audio Output
        ImGui.TextColored(sectionColor, "Audio Output");
        ImGui.Separator();
        ImGui.Spacing();

        using (ImRaii.PushIndent(indent))
        {
            this.outputDevices ??= [.. this.audioDeviceController.GetAudioPlaybackDevices()];
            var outputDeviceIndex = this.audioDeviceController.AudioPlaybackDeviceIndex + 1;
            if (DrawDeviceCombo("##OutputDevice", ref outputDeviceIndex, this.outputDevices))
            {
                this.audioDeviceController.AudioPlaybackDeviceIndex = outputDeviceIndex - 1;
            }

            ImGui.Spacing();

            if (ImGui.Button(this.PlayingBackMicAudio.Value ? "Stop Mic Playback" : "Test Mic Playback"))
            {
                this.PlayingBackMicAudio.Value = !this.PlayingBackMicAudio.Value;
            }
        }

        ImGui.Spacing();

        // Keybinds
        ImGui.TextColored(sectionColor, "Keybinds");
        ImGui.SameLine(); Common.HelpMarker("Right click a button to clear a keybind.");
        ImGui.Separator();
        ImGui.Spacing();

        using (ImRaii.PushIndent(indent))
        {
            DrawKeybindEdit(Keybind.MuteMic, this.configuration.MuteMicBinding, "Mute Microphone");
            DrawKeybindEdit(Keybind.Deafen, this.configuration.DeafenBinding, "Deafen");
        }

        // Reset
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (Common.DrawResetButton("Reset to Defaults##devices"))
        {
            this.resetDeviceSettings.OnNext(Unit.Default);
            // Force a re-query of audio device names next frame in case the
            // presenter's reset wants to re-pull the device list.
            this.inputDevices = null;
            this.outputDevices = null;
        }
    }

    /// <summary>
    /// Draws an audio-device combo. ImGui locks combo popup width to combo
    /// widget width, so the widget is told to fill the section content region
    /// — <c>ConfigWindow</c>'s min-width constraint guarantees that region is
    /// wide enough for typical Windows device names. Hover tooltips on each
    /// item and on the closed combo cover names longer than even the widened
    /// window (the user can always drag the window wider too).
    /// </summary>
    private static bool DrawDeviceCombo(string id, ref int currentIndex, string[] items)
    {
        if (items.Length == 0) return false;
        // Clamp in case the device list shrank between frames.
        if (currentIndex < 0 || currentIndex >= items.Length) currentIndex = 0;

        var preview = items[currentIndex];
        ImGui.SetNextItemWidth(-1);

        bool changed = false;
        if (ImGui.BeginCombo(id, preview))
        {
            for (int i = 0; i < items.Length; i++)
            {
                bool isSelected = i == currentIndex;
                if (ImGui.Selectable(items[i], isSelected))
                {
                    currentIndex = i;
                    changed = true;
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(items[i]);
                }
                if (isSelected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(preview);
        }
        return changed;
    }

    private void DrawKeybindEdit(Keybind keybind, KeyBinding currentBinding, string label)
    {
        using var id = ImRaii.PushId($"{keybind} Keybind");

        // Keybind values like "Not set" / "F8" / "Ctrl+Shift+G" render as a
        // flat label without ImGui's default button shading, so we force a
        // visible border + a slightly lighter button tint to make them read
        // as clickable. Width is sized for the worst-case combo label
        // ("Ctrl+Shift+Alt+F12") so longer combos don't truncate.
        var isRecording = this.KeybindBeingEdited.Value == keybind;
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 1f))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 4f))
        using (ImRaii.PushColor(ImGuiCol.Border, new Vector4(0.55f, 0.55f, 0.60f, 0.85f)))
        using (ImRaii.PushColor(ImGuiCol.Button,
            isRecording ? new Vector4(0.45f, 0.30f, 0.30f, 1.0f) : new Vector4(0.20f, 0.22f, 0.27f, 1.0f)))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.30f, 0.34f, 0.42f, 1.0f)))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.15f, 0.17f, 0.22f, 1.0f)))
        {
            if (ImGui.Button(isRecording ?
                    "Recording..." :
                    currentBinding.ToDisplayString(),
                new Vector2(10 * ImGui.GetFontSize(), 0)))
            {
                this.KeybindBeingEdited.Value = isRecording ? Keybind.None : keybind;
            }
        }
        if (ImGui.IsItemHovered() && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
        {
            this.clearKeybind.OnNext(keybind);
        }
        ImGui.SameLine();
        ImGui.Text(label);
    }

    // ── Audio Falloff ────────────────────────────────────────────────────────
    private void DrawFalloffTab()
    {
        using var falloffTab = ImRaii.TabItem("Audio Falloff");
        if (!falloffTab) return;

        var sectionColor = new Vector4(0.6f, 0.8f, 1.0f, 1.0f);
        float indent = ImGui.GetFontSize();
        float labelColWidth = 130f;
        float controlColWidth = 160f;

        // Volume
        ImGui.Spacing();
        ImGui.TextColored(sectionColor, "Volume");
        ImGui.Separator();
        ImGui.Spacing();

        using (ImRaii.PushIndent(indent))
        {
            using var table = ImRaii.Table("FalloffVolumeTable", 2);
            if (table)
            {
                ImGui.TableSetupColumn("Col1", ImGuiTableColumnFlags.WidthFixed, labelColWidth);
                ImGui.TableSetupColumn("Col2", ImGuiTableColumnFlags.WidthFixed, controlColWidth);

                ImGui.TableNextRow(); ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.Text("Master Volume"); ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(ImGui.GetColumnWidth());
                var masterVolume = this.MasterVolume.Value * 100.0f;
                if (ImGui.SliderFloat("##MasterVolume", ref masterVolume, 0.0f, 500.0f, "%1.0f%%"))
                {
                    this.MasterVolume.Value = masterVolume / 100.0f;
                }
            }
        }

        ImGui.Spacing();

        // Distance Falloff
        ImGui.TextColored(sectionColor, "Distance Falloff");
        ImGui.Separator();
        ImGui.Spacing();

        using (ImRaii.PushIndent(indent))
        {
            using var table = ImRaii.Table("FalloffDistanceTable", 2);
            if (table)
            {
                ImGui.TableSetupColumn("Col1", ImGuiTableColumnFlags.WidthFixed, labelColWidth);
                ImGui.TableSetupColumn("Col2", ImGuiTableColumnFlags.WidthFixed, controlColWidth);

                ImGui.TableNextRow(); ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.Text("Falloff Type"); ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(ImGui.GetColumnWidth());
                var falloffType = (int)this.AudioFalloffType.Value;
                if (ImGui.Combo("##AudioFalloffType", ref falloffType, this.falloffTypes, this.falloffTypes.Length))
                {
                    this.AudioFalloffType.Value = (AudioFalloffModel.FalloffType)falloffType;
                }

                ImGui.TableNextRow(); ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.Text("Min Distance");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Volume is at maximum below this distance (yalms)");
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(ImGui.GetColumnWidth());
                var minDistance = this.AudioFalloffMinimumDistance.Value;
                if (ImGui.InputFloat("##AudioFalloffMinimumDistance", ref minDistance, 0.1f, 1.0f, "%.1f y"))
                {
                    this.AudioFalloffMinimumDistance.Value = minDistance;
                }

                ImGui.TableNextRow(); ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.Text("Max Distance");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Volume reaches zero at this distance (yalms)");
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(ImGui.GetColumnWidth());
                var maxDistance = this.AudioFalloffMaximumDistance.Value;
                if (ImGui.InputFloat("##AudioFalloffMaximumDistance", ref maxDistance, 0.1f, 1.0f, "%.1f y"))
                {
                    this.AudioFalloffMaximumDistance.Value = maxDistance;
                }

                ImGui.TableNextRow(); ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.Text("Falloff Factor");
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Higher = faster volume drop over distance.\nNot used for linear falloff.");
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(ImGui.GetColumnWidth());
                var falloffFactor = this.AudioFalloffFactor.Value;
                if (ImGui.InputFloat("##AudioFalloffFactor", ref falloffFactor, 0.1f, 1.0f, "%.1f"))
                {
                    this.AudioFalloffFactor.Value = falloffFactor;
                }
            }

            ImGui.Spacing();
            var enableSpatialization = this.EnableSpatialization.Value;
            if (ImGui.Checkbox("Spatial Audio", ref enableSpatialization))
            {
                this.EnableSpatialization.Value = enableSpatialization;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Pan incoming audio based on camera facing direction");
        }

        ImGui.Spacing();

        // Player Behavior
        ImGui.TextColored(sectionColor, "Player Behavior");
        ImGui.Separator();
        ImGui.Spacing();

        using (ImRaii.PushIndent(indent))
        {
            var muteDeadPlayers = this.MuteDeadPlayers.Value;
            if (ImGui.Checkbox("Mute Dead Players", ref muteDeadPlayers))
            {
                this.MuteDeadPlayers.Value = muteDeadPlayers;
            }
            if (muteDeadPlayers)
            {
                using (ImRaii.PushIndent(indent))
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text("Mute Delay");
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Delay (ms) before a just-died player is muted");
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(70);
                    var muteDeadPlayersDelayMs = this.MuteDeadPlayersDelayMs.Value;
                    if (ImGui.InputInt("ms##MuteDelay", ref muteDeadPlayersDelayMs, 0))
                    {
                        this.MuteDeadPlayersDelayMs.Value = muteDeadPlayersDelayMs;
                    }
                }
            }

            ImGui.Spacing();

            var unmuteAllIfDead = this.UnmuteAllIfDead.Value;
            if (ImGui.Checkbox("Unmute All If Dead", ref unmuteAllIfDead))
            {
                this.UnmuteAllIfDead.Value = unmuteAllIfDead;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Hear everyone when you are dead — reduces loneliness on wipes");

            ImGui.Spacing();

            using (ImRaii.Disabled(this.voiceRoomManager.InPublicRoom))
            {
                var muteOutOfMapPlayers = this.voiceRoomManager.InPublicRoom || this.MuteOutOfMapPlayers.Value;
                if (ImGui.Checkbox("Mute Out-of-Map Players", ref muteOutOfMapPlayers))
                {
                    this.MuteOutOfMapPlayers.Value = muteOutOfMapPlayers;
                }
            }
            ImGui.SameLine(); Common.HelpMarker("Can only disable in private rooms");
        }

        // Reset
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (Common.DrawResetButton("Reset to Defaults##falloff"))
        {
            this.resetFalloffSettings.OnNext(Unit.Default);
        }
    }

    // ── Misc ─────────────────────────────────────────────────────────────────
    private void DrawMiscTab()
    {
        using var miscTab = ImRaii.TabItem("Misc");
        if (!miscTab) return;

        var playRoomJoinAndLeaveSounds = this.PlayRoomJoinAndLeaveSounds.Value;
        if (ImGui.Checkbox("Play room join and leave sounds", ref playRoomJoinAndLeaveSounds))
        {
            this.PlayRoomJoinAndLeaveSounds.Value = playRoomJoinAndLeaveSounds;
        }

        var keybindsRequireGameFocus = this.KeybindsRequireGameFocus.Value;
        if (ImGui.Checkbox("Keybinds require game focus", ref keybindsRequireGameFocus))
        {
            this.KeybindsRequireGameFocus.Value = keybindsRequireGameFocus;
        }

        var showFullPlayerNames = this.ShowFullPlayerNames.Value;
        if (ImGui.Checkbox("Show full player names (otherwise initials)", ref showFullPlayerNames))
        {
            this.ShowFullPlayerNames.Value = showFullPlayerNames;
        }

        var showNameplateIcons = this.ShowNameplateIcons.Value;
        if (ImGui.Checkbox("Show voice icons on nameplates", ref showNameplateIcons))
        {
            this.ShowNameplateIcons.Value = showNameplateIcons;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Display a microphone icon above player nameplates while in a voice channel");

        var printLogsToChat = this.PrintLogsToChat.Value;
        if (ImGui.Checkbox("Print logs to chat", ref printLogsToChat))
        {
            this.PrintLogsToChat.Value = printLogsToChat;
        }

        if (printLogsToChat)
        {
            ImGui.SameLine();
            var minLogLevel = this.MinimumVisibleLogLevel.Value;
            ImGui.SetNextItemWidth(70);
            if (ImGui.Combo("Min log level", ref minLogLevel, allLoggingLevels, allLoggingLevels.Length))
            {
                this.MinimumVisibleLogLevel.Value = minLogLevel;
            }
        }

        ImGui.Spacing();

        ImGui.AlignTextToFramePadding();
        ImGui.Text("Bugs or suggestions?");
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Button, Vector4Colors.DiscordButton))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, Vector4Colors.DiscordButtonHover))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, Vector4Colors.DiscordButtonActive))
        {
            if (ImGui.Button("Discord"))
            {
                Process.Start(new ProcessStartInfo { FileName = "https://discord.gg/bgWX5PZE", UseShellExecute = true });
            }
        }

        ImGui.SameLine();
        ImGui.Text("|");
        ImGui.SameLine();

        using (ImRaii.PushColor(ImGuiCol.Button, Vector4Colors.KofiButton))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, Vector4Colors.KofiButtonHover))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, Vector4Colors.KofiButtonActive))
        {
            if (ImGui.Button("Support on Ko-fi"))
            {
                Process.Start(new ProcessStartInfo { FileName = "https://ko-fi.com/phys1ks", UseShellExecute = true });
            }
        }
    }

    // ── Profile ──────────────────────────────────────────────────────────────
    private void DrawProfileTab()
    {
        using var profileTab = ImRaii.TabItem("Profile");
        if (!profileTab) return;

        var sectionColor = new Vector4(0.6f, 0.8f, 1.0f, 1.0f);
        float indent = ImGui.GetFontSize();

        var isPremium = this.configuration.IsLocalPremium;
        DrawPremiumLinkSection(isPremium, indent);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // The entire profile UI is inert when the local player is not premium.
        // We still render it (so users can see what the perk looks like) but
        // every widget below is disabled and the Save button no-ops.
        using var disabled = ImRaii.Disabled(!isPremium);

        // Pronoun
        ImGui.Spacing();
        ImGui.TextColored(sectionColor, "Pronoun");
        ImGui.Separator();
        ImGui.Spacing();

        using (ImRaii.PushIndent(indent))
        {
            ImGui.TextWrapped("Optional. Up to 24 characters.");
            ImGui.SetNextItemWidth(220f);
            var pronoun = this.UserPronoun.Value ?? string.Empty;
            if (ImGui.InputText("##UserPronoun", ref pronoun, 24))
            {
                this.UserPronoun.Value = pronoun;
            }
        }

        ImGui.Spacing();

        // Color Picker
        ImGui.TextColored(sectionColor, "Your Color");
        ImGui.Separator();
        ImGui.Spacing();

        using (ImRaii.PushIndent(indent))
        {
            ImGui.TextWrapped("Choose a color to display next to your name in the voice channel list.");
            ImGui.Spacing();

            // Display current color preview
            var currentHex = this.UserColor.Value;
            var currentColor = Vector4Colors.HexToVector4(currentHex);
            var buttonSize = new Vector2(40, 40);
            ImGui.GetWindowDrawList().AddRectFilled(
                ImGui.GetCursorScreenPos(),
                ImGui.GetCursorScreenPos() + buttonSize,
                ImGui.GetColorU32(currentColor)
            );
            ImGui.Dummy(buttonSize);
            ImGui.SameLine();

            ImGui.BeginGroup();
            ImGui.AlignTextToFramePadding();
            ImGui.Text("Current Color:");
            ImGui.SetNextItemWidth(120f);
            var hexInput = this.UserColor.Value;
            if (ImGui.InputText("##HexColor", ref hexInput, 7, ImGuiInputTextFlags.CharsHexadecimal))
            {
                if (Utils.IsValidHexColor(hexInput))
                {
                    this.UserColor.Value = Utils.NormalizeHexColor(hexInput);
                }
            }
            ImGui.EndGroup();

            ImGui.Spacing();
            ImGui.TextColored(sectionColor, "Color Palette");
            ImGui.Spacing();

            for (int i = 0; i < this.colorPalette.Length; i++)
            {
                var color = this.colorPalette[i];
                var btnSize = new Vector2(30, 30);
                if (ImGui.ColorButton($"##color{i}", color, ImGuiColorEditFlags.NoAlpha, btnSize))
                {
                    var hex = $"{(int)(color.X * 255):X2}{(int)(color.Y * 255):X2}{(int)(color.Z * 255):X2}";
                    this.UserColor.Value = hex;
                }

                if ((i + 1) % 4 != 0)
                    ImGui.SameLine();
            }
        }

        ImGui.Spacing();

        // Biography
        ImGui.TextColored(sectionColor, "Biography");
        ImGui.Separator();
        ImGui.Spacing();

        using (ImRaii.PushIndent(indent))
        {
            ImGui.TextWrapped("A short bio about yourself (no links allowed). This will be visible when others click on you.");
            ImGui.Spacing();

            var bio = this.UserBiography.Value;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputTextMultiline("##Biography", ref bio, 500, new Vector2(-1, 80)))
            {
                // Reject if links detected
                if (Utils.ContainsLinks(bio))
                {
                    ImGui.TextColored(new Vector4(1, 0.5f, 0.5f, 1), "Links are not allowed in biography");
                }
                else
                {
                    this.UserBiography.Value = bio;
                }
            }
        }

        ImGui.Spacing();

        // Twitch Link
        ImGui.TextColored(sectionColor, "Twitch Link");
        ImGui.Separator();
        ImGui.Spacing();

        using (ImRaii.PushIndent(indent))
        {
            ImGui.TextWrapped("Your Twitch channel link. Must be in format: https://twitch.tv/yourusername");
            ImGui.Spacing();

            var twitchLink = this.TwitchLink.Value;
            ImGui.SetNextItemWidth(300f);
            if (ImGui.InputText("##TwitchLink", ref twitchLink, 255))
            {
                if (string.IsNullOrWhiteSpace(twitchLink))
                {
                    this.TwitchLink.Value = string.Empty;
                }
                else if (Utils.IsValidTwitchLink(twitchLink))
                {
                    this.TwitchLink.Value = twitchLink;
                }
                else
                {
                    ImGui.TextColored(new Vector4(1, 0.5f, 0.5f, 1), "Invalid Twitch link format");
                }
            }

            if (!string.IsNullOrWhiteSpace(this.TwitchLink.Value))
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.5f, 1.0f, 0.5f, 1), "✓");
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        using (ImRaii.PushIndent(indent))
        {
            if (ImGui.Button("Save Profile"))
            {
                var twitchLink = this.TwitchLink.Value?.Trim() ?? string.Empty;
                var biography = this.UserBiography.Value?.Trim() ?? string.Empty;
                var status = this.UserStatus.Value?.Trim() ?? string.Empty;
                var pronoun = string.IsNullOrWhiteSpace(this.UserPronoun.Value) ? "Unset" : this.UserPronoun.Value.Trim();
                var color = Utils.NormalizeHexColor(this.UserColor.Value);

                if (Utils.ContainsLinks(biography))
                {
                    this.profileSaveStatusMessage = "Save failed: biography cannot contain links.";
                    this.profileSaveStatusColor = new Vector4(0.95f, 0.45f, 0.45f, 1f);
                }
                else if (Utils.ContainsLinks(status))
                {
                    this.profileSaveStatusMessage = "Save failed: status cannot contain links.";
                    this.profileSaveStatusColor = new Vector4(0.95f, 0.45f, 0.45f, 1f);
                }
                else if (!string.IsNullOrWhiteSpace(twitchLink) && !Utils.IsValidTwitchLink(twitchLink))
                {
                    this.profileSaveStatusMessage = "Save failed: Twitch link must be twitch.tv only.";
                    this.profileSaveStatusColor = new Vector4(0.95f, 0.45f, 0.45f, 1f);
                }
                else
                {
                    this.configuration.UserBiography = biography;
                    this.configuration.UserPronoun = pronoun;
                    this.configuration.UserColor = color;
                    this.configuration.TwitchLink = twitchLink;
                    this.configuration.UserStatus = status;
                    this.configuration.Save();
                    this.voiceRoomManager.PushPlayerAudioState();

                    this.profileSaveStatusMessage = "Profile saved successfully.";
                    this.profileSaveStatusColor = new Vector4(0.55f, 0.90f, 0.55f, 1f);
                }
            }

            if (!string.IsNullOrWhiteSpace(this.profileSaveStatusMessage))
            {
                ImGui.SameLine();
                ImGui.TextColored(this.profileSaveStatusColor, this.profileSaveStatusMessage);
            }
        }
    }

    // ── Premium link banner shown at the top of the Profile tab ──────────────
    private void DrawPremiumLinkSection(bool isPremium, float indent)
    {
        var linker = this.premiumLinker;
        if (isPremium)
        {
            var headerColor = new Vector4(0.55f, 0.90f, 0.55f, 1f);
            ImGui.TextColored(headerColor, "Supporter linked");
            ImGui.Separator();
            ImGui.Spacing();
            using (ImRaii.PushIndent(indent))
            {
                var who = string.IsNullOrWhiteSpace(this.configuration.PremiumLinkedDiscordUsername)
                    ? "your Discord account"
                    : this.configuration.PremiumLinkedDiscordUsername;
                ImGui.TextWrapped($"Signed in as {who}. Profile customization is unlocked.");
                ImGui.Spacing();
                if (ImGui.Button("Sign out"))
                {
                    _ = linker.SignOutAsync();
                }
            }
            return;
        }

        var lockColor = new Vector4(1.0f, 0.78f, 0.35f, 1f);
        ImGui.TextColored(lockColor, "Supporter perk");
        ImGui.Separator();
        ImGui.Spacing();
        using (ImRaii.PushIndent(indent))
        {
            ImGui.TextWrapped("Profile customization (color, pronoun, biography, status, Twitch link) is a supporter perk. Link your Discord account to unlock it.");
            ImGui.Spacing();

            using (ImRaii.Disabled(linker.InProgress))
            {
                if (ImGui.Button("Link Discord to unlock"))
                {
                    linker.StartFlow();
                }
            }

            if (linker.InProgress)
            {
                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                {
                    linker.CancelFlow();
                }
            }

            if (!string.IsNullOrWhiteSpace(linker.StatusMessage))
            {
                ImGui.Spacing();
                Vector4 col;
                if (linker.State == PremiumLinker.LinkState.Failed)
                {
                    col = new Vector4(0.95f, 0.45f, 0.45f, 1f);
                }
                else if (linker.State == PremiumLinker.LinkState.Complete)
                {
                    col = new Vector4(0.55f, 0.90f, 0.55f, 1f);
                }
                else
                {
                    col = new Vector4(0.75f, 0.85f, 1.00f, 1f);
                }
                ImGui.TextWrapped(""); // ensures wrapping width is initialized
                ImGui.TextColored(col, linker.StatusMessage);
            }
        }
    }

    /// <summary>
    /// Human-readable label for each WebRTC VAD operating mode position.
    /// Out-of-range values fall back to the historical Aggressive default so
    /// the slider never renders a blank label.
    /// </summary>
    private static string VadStopName(int mode) => mode switch
    {
        0 => "Quality",
        1 => "Low Bitrate",
        2 => "Aggressive",
        3 => "Very Aggressive",
        _ => "Aggressive",
    };

    /// <summary>
    /// Draws a small filled circle next to the VAD slider: green when the
    /// self-VAD is currently flagging speech, gray otherwise. Smoothed by a
    /// 220ms hold (<see cref="PickupIndicatorHoldMs"/>) because the underlying
    /// WebRTC VAD oscillates per 20ms frame during natural speech and would
    /// otherwise produce a flickering dot.
    /// </summary>
    /// <summary>
    /// Horizontal mic-input level meter drawn under the input-device combo.
    /// Reads <see cref="IAudioDeviceController.MicInputPeak"/> each frame
    /// (post-boost, already 0..1) and renders it as a progress bar — green
    /// by default, red while <see cref="IAudioDeviceController.MicInputClipped"/>
    /// is true so an over-boosted user notices immediately. Lets a user
    /// confirm "Windows is delivering my voice to the plugin" without
    /// having to enable mic playback or coordinate with another peer.
    /// </summary>
    private void DrawMicInputLevelMeter()
    {
        var peak = Math.Clamp(this.audioDeviceController.MicInputPeak, 0f, 1f);
        var clipped = this.audioDeviceController.MicInputClipped;

        // Stretch over the same width as the device combo above it (which
        // uses GetContentRegionAvail by default). Use a fixed short height
        // so the meter reads as a "meter" rather than a button.
        var width = ImGui.GetContentRegionAvail().X;
        var size = new Vector2(width, ImGui.GetFontSize() * 0.45f);

        // Red while the boosted signal saturated recently; green otherwise.
        // ProgressBar's default text overlay would be empty here so we
        // suppress the overlay with an empty string.
        var barColor = clipped
            ? new Vector4(0.90f, 0.30f, 0.30f, 1f)
            : new Vector4(0.30f, 0.85f, 0.30f, 1f);
        using var c = ImRaii.PushColor(ImGuiCol.PlotHistogram, barColor);
        ImGui.ProgressBar(peak, size, string.Empty);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(clipped
                ? $"Mic input level: {(int)(peak * 100)}%% — clipping! Lower the Input Boost slider."
                : $"Mic input level: {(int)(peak * 100)}%%");
        }
    }

    /// <summary>
    /// Input-boost slider (0..500 %) drawn under the live level meter, so
    /// the meter and the control that drives it sit side-by-side. Defaults
    /// to 100 % which means the captured signal is passed through unchanged.
    /// </summary>
    private void DrawInputBoostSlider()
    {
        var boostPct = this.InputBoost.Value * 100f;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.55f);
        if (ImGui.SliderFloat("##InputBoost", ref boostPct, 0f, 500f, "%1.0f%%"))
        {
            this.InputBoost.Value = boostPct / 100f;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Boost the mic signal before it's sent to other peers.\n" +
                "100% = unchanged. Raise this if your mic is too quiet.\n" +
                "Watch the level meter above — it turns red when the boost is too high.");
        }
        ImGui.SameLine();
        ImGui.TextUnformatted("Input boost");
    }

    /// <summary>
    /// Red banner + Retry button shown only when the capture path has a
    /// live exception (mic unplugged, device-driver crashed, audio service
    /// stopped). The Retry button calls
    /// <see cref="IAudioDeviceController.RestartMic"/> which tears down and
    /// re-creates the WASAPI capture source.
    /// </summary>
    /// <summary>
    /// Renders the "Prefer UDP audio" checkbox + a live "Audio transport:
    /// UDP / TCP" status line so users (and bug reports) can see which
    /// transport is actually in use. The checkbox controls
    /// <see cref="Configuration.PreferUdpAudio"/> — flipping it off forces
    /// the Socket.IO path on the next session, which is useful for users
    /// behind UDP-blocking firewalls who don't want to wait through the
    /// 5 s hello-ack timeout on every join.
    /// </summary>
    private void DrawAudioTransportControls()
    {
        var preferUdp = this.configuration.PreferUdpAudio;
        if (ImGui.Checkbox("Prefer UDP audio", ref preferUdp))
        {
            this.configuration.PreferUdpAudio = preferUdp;
            this.configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "When on (default), the plugin uses a low-latency UDP audio channel\n" +
                "with automatic fallback to the regular Socket.IO transport if UDP\n" +
                "can't be established (e.g. behind a corporate firewall).\n" +
                "Turn off to force the Socket.IO transport globally.");
        }

        // Live transport indicator. Pulls directly from VoiceRoomManager so
        // every join updates it without explicit wiring.
        var transport = this.voiceRoomManager.ActiveAudioTransport;
        var reason = this.voiceRoomManager.AudioTransportReason;
        string label;
        Vector4 color;
        switch (transport)
        {
            case VoiceRoomManager.AudioTransport.Udp:
                label = "Audio transport: UDP";
                color = new Vector4(0.55f, 0.90f, 0.55f, 1f);
                break;
            case VoiceRoomManager.AudioTransport.Tcp:
                label = string.IsNullOrEmpty(reason)
                    ? "Audio transport: TCP"
                    : $"Audio transport: TCP ({reason})";
                color = new Vector4(0.95f, 0.80f, 0.45f, 1f);
                break;
            default:
                label = "Audio transport: not connected";
                color = new Vector4(0.70f, 0.70f, 0.70f, 1f);
                break;
        }
        ImGui.TextColored(color, label);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Which network path your captured audio is using right now.\n" +
                "UDP = lowest latency, packet-loss tolerant.\n" +
                "TCP = Socket.IO fallback when UDP isn't available.\n" +
                "Include this line in bug reports about choppy or robotic audio.");
        }
    }

    private void DrawMicErrorBanner()
    {
        var err = this.audioDeviceController.LastMicError;
        if (err == null) return;

        ImGui.Spacing();
        using (var c = ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.95f, 0.45f, 0.45f, 1f)))
        {
            ImGui.TextWrapped($"Mic error: {err.Message}");
        }
        if (ImGui.Button("Retry##mic-retry"))
        {
            this.audioDeviceController.RestartMic();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Try to reopen the selected capture device.");
        }
    }

    private void DrawPickupIndicator()
    {
        var nowMs = Environment.TickCount64;
        if (this.audioDeviceController.RecordingDataHasActivity)
        {
            this.lastPickupTickMs = nowMs;
        }
        var active = nowMs - this.lastPickupTickMs < PickupIndicatorHoldMs;
        var color = active
            ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.20f, 0.80f, 0.20f, 1f))
            : ImGui.ColorConvertFloat4ToU32(new Vector4(0.38f, 0.38f, 0.38f, 1f));

        var radius = ImGui.GetFontSize() * 0.35f;
        var center = ImGui.GetCursorScreenPos();
        center.X += radius;
        center.Y += ImGui.GetTextLineHeight() * 0.5f;
        ImGui.GetWindowDrawList().AddCircleFilled(center, radius, color);
        // Reserve layout space so any following SameLine() positions correctly.
        ImGui.Dummy(new Vector2(radius * 2f + 2f, ImGui.GetTextLineHeight()));
    }
}

