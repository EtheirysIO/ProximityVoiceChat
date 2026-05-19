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
using Dalamud.Plugin.Services;
using EtheirysProximityVoiceChat.Audio;
using EtheirysProximityVoiceChat.Input;
using EtheirysProximityVoiceChat.Log;
using EtheirysProximityVoiceChat.UI.View;
using Reactive.Bindings;
using System;
using System.Reactive.Linq;
using WindowsInput.Events;

namespace EtheirysProximityVoiceChat.UI.Presenter;

public class ConfigWindowPresenter(
    ConfigWindow view,
    Configuration configuration,
    IFramework framework,
    PushToTalkController pushToTalkController,
    VoiceRoomManager voiceRoomManager,
    InputEventSource inputEventSource,
    InputManager inputManager,
    ILogger logger) : IPluginUIPresenter
{
    public IPluginUIView View => this.view;

    private readonly ConfigWindow view = view;
    private readonly Configuration configuration = configuration;
    private readonly IFramework framework = framework;
    private readonly PushToTalkController pushToTalkController = pushToTalkController;
    private readonly IAudioDeviceController audioDeviceController = pushToTalkController;
    private readonly VoiceRoomManager voiceRoomManager = voiceRoomManager;
    private readonly InputEventSource inputEventSource = inputEventSource;
    private readonly InputManager inputManager = inputManager;
    private readonly ILogger logger = logger;

    private bool keyDownListenerSubscribed;

    public void SetupBindings()
    {
        BindVariables();
        BindActions();
    }

    private void BindVariables()
    {
        Bind(this.view.PlayingBackMicAudio,
            b => 
            {
                this.audioDeviceController.PlayingBackMicAudio = b;
                this.voiceRoomManager.PushPlayerAudioState();
            },
            this.audioDeviceController.PlayingBackMicAudio);
        Bind(this.view.PushToTalk,
            b => 
            {
                this.configuration.PushToTalk = b;
                this.configuration.Save();
                this.pushToTalkController.UpdateListeners();
            },
            this.configuration.PushToTalk);
        Bind(this.view.SuppressNoise,
            b => { this.configuration.SuppressNoise = b; this.configuration.Save(); }, this.configuration.SuppressNoise);
        Bind(this.view.VadSensitivity,
            v =>
            {
                this.configuration.VadSensitivity = v;
                this.configuration.Save();
                this.audioDeviceController.SetVadOperatingMode(v);
            }, this.configuration.VadSensitivity);

        Bind(this.view.MasterVolume,
            f => { this.configuration.MasterVolume = f; this.configuration.Save(); }, this.configuration.MasterVolume);
        Bind(this.view.InputBoost,
            f => { this.configuration.InputBoost = f; this.configuration.Save(); }, this.configuration.InputBoost);
        Bind(this.view.AudioFalloffType,
            t => { this.configuration.FalloffModel.Type = t; this.configuration.Save(); }, this.configuration.FalloffModel.Type);
        Bind(this.view.AudioFalloffMinimumDistance,
            f => { this.configuration.FalloffModel.MinimumDistance = f; this.configuration.Save(); }, this.configuration.FalloffModel.MinimumDistance);
        Bind(this.view.AudioFalloffMaximumDistance,
            f => { this.configuration.FalloffModel.MaximumDistance = f; this.configuration.Save(); }, this.configuration.FalloffModel.MaximumDistance);
        Bind(this.view.AudioFalloffFactor,
            f => { this.configuration.FalloffModel.FalloffFactor = f; this.configuration.Save(); }, this.configuration.FalloffModel.FalloffFactor);
        Bind(this.view.EnableSpatialization,
            b => { this.configuration.EnableSpatialization = b; this.configuration.Save(); }, this.configuration.EnableSpatialization);
        Bind(this.view.MuteDeadPlayers,
            b => { this.configuration.MuteDeadPlayers = b; this.configuration.Save(); }, this.configuration.MuteDeadPlayers);
        Bind(this.view.MuteDeadPlayersDelayMs,
            f => { this.configuration.MuteDeadPlayersDelayMs = f; this.configuration.Save(); }, this.configuration.MuteDeadPlayersDelayMs);
        Bind(this.view.UnmuteAllIfDead,
            b => { this.configuration.UnmuteAllIfDead = b; this.configuration.Save(); }, this.configuration.UnmuteAllIfDead);
        Bind(this.view.MuteOutOfMapPlayers,
            b => { this.configuration.MuteOutOfMapPlayers = b; this.configuration.Save(); }, this.configuration.MuteOutOfMapPlayers);

        Bind(this.view.PlayRoomJoinAndLeaveSounds,
            b => { this.configuration.PlayRoomJoinAndLeaveSounds = b; this.configuration.Save(); }, this.configuration.PlayRoomJoinAndLeaveSounds);
        Bind(this.view.KeybindsRequireGameFocus,
            b => { this.configuration.KeybindsRequireGameFocus = b; this.configuration.Save(); }, this.configuration.KeybindsRequireGameFocus);
        Bind(this.view.ShowFullPlayerNames,
            b => { this.configuration.ShowFullPlayerNames = b; this.configuration.Save(); }, this.configuration.ShowFullPlayerNames);
        Bind(this.view.ShowNameplateIcons,
            b => { this.configuration.ShowNameplateIcons = b; this.configuration.Save(); }, this.configuration.ShowNameplateIcons);
        Bind(this.view.PrintLogsToChat,
            b => { this.configuration.PrintLogsToChat = b; this.configuration.Save(); }, this.configuration.PrintLogsToChat);
        Bind(this.view.MinimumVisibleLogLevel,
            i => { this.configuration.MinimumVisibleLogLevel = i; this.configuration.Save(); }, this.configuration.MinimumVisibleLogLevel);
    }

    private void BindActions()
    {
        this.view.KeybindBeingEdited.Subscribe(k => 
        {
            if (k != Keybind.None && !this.keyDownListenerSubscribed)
            {
                this.inputEventSource.SubscribeToKeyDown(OnInputKeyDown);
                this.keyDownListenerSubscribed = true;
            }
            else if (k == Keybind.None && this.keyDownListenerSubscribed)
            {
                this.inputEventSource.UnsubscribeToKeyDown(OnInputKeyDown);
                this.keyDownListenerSubscribed = false;
            }
        });
        this.view.ClearKeybind.Subscribe(k =>
        {
            switch(k)
            {
                case Keybind.PushToTalk:
                    this.configuration.PushToTalkBinding = new KeyBinding();
                    break;
                case Keybind.MuteMic:
                    this.configuration.MuteMicBinding = new KeyBinding();
                    break;
                case Keybind.Deafen:
                    this.configuration.DeafenBinding = new KeyBinding();
                    break;
                default:
                    return;
            }
            this.configuration.Save();
            this.inputManager.UpdateListeners();
        });

        // Reset buttons on each ConfigWindow tab. Push values back through the
        // reactive properties so the existing Bind() subscribers persist them
        // and the UI controls visually update at the same time.
        this.view.ResetDeviceSettings.Subscribe(_ =>
        {
            this.audioDeviceController.AudioRecordingDeviceIndex = -1;
            this.audioDeviceController.AudioPlaybackDeviceIndex = -1;

            this.view.SuppressNoise.Value = true;
            this.view.VadSensitivity.Value = 3;
            this.view.PushToTalk.Value = false;
            this.view.InputBoost.Value = 1.0f;

            this.configuration.PushToTalkBinding = new KeyBinding();
            this.configuration.MuteMicBinding = new KeyBinding();
            this.configuration.DeafenBinding = new KeyBinding();
            this.configuration.Save();
            this.inputManager.UpdateListeners();
        });

        this.view.ResetFalloffSettings.Subscribe(_ =>
        {
            var defaults = new AudioFalloffModel();
            this.view.MasterVolume.Value = 2.0f;
            this.view.AudioFalloffType.Value = defaults.Type;
            this.view.AudioFalloffMinimumDistance.Value = defaults.MinimumDistance;
            this.view.AudioFalloffMaximumDistance.Value = defaults.MaximumDistance;
            this.view.AudioFalloffFactor.Value = defaults.FalloffFactor;
            this.view.EnableSpatialization.Value = true;
            this.view.MuteDeadPlayers.Value = true;
            this.view.MuteDeadPlayersDelayMs.Value = 2000;
            this.view.UnmuteAllIfDead.Value = true;
            this.view.MuteOutOfMapPlayers.Value = false;
        });
    }

    private void Bind<T>(
        IReactiveProperty<T> reactiveProperty,
        Action<T> dataUpdateAction,
        T initialValue)
    {
        if (initialValue != null)
        {
            reactiveProperty.Value = initialValue;
        }
        // ReactiveProperty emits the current value immediately on subscribe.
        // Skipping that first emission avoids an eager config save during
        // plugin startup that races Dalamud's storage layer and intermittently
        // surfaces as "database is locked". Wrap the user-supplied action in
        // try/catch so a single bad write can't tear down the subscription.
        reactiveProperty
            .Skip(1)
            .Subscribe(value =>
            {
                try
                {
                    dataUpdateAction(value);
                }
                catch (Exception ex)
                {
                    this.logger.Error("Failed to persist config update: {0}", ex);
                }
            });
    }

    private void OnInputKeyDown(KeyDown k)
    {
        // Snapshot the modifier state on the hook thread (where this callback
        // is invoked). GetAsyncKeyState reads kernel-maintained state and is
        // accurate at this instant; if we deferred the read into framework.Run
        // the user may have released the modifier by then.
        var shift = InputEventSource.IsShiftDown();
        var ctrl = InputEventSource.IsCtrlDown();
        var alt = InputEventSource.IsAltDown();
        var key = k.Key;

        // This callback can be called from a non-framework thread, and UI values
        // should only be modified on the framework thread (or else the game can
        // crash).
        this.framework.Run(() =>
        {
            var editedKeybind = this.view.KeybindBeingEdited.Value;
            if (editedKeybind == Keybind.None) return;

            // Pure modifier presses don't finalize a binding — we wait for the
            // user to press the non-modifier "main" key, so Shift+G binds as
            // "Shift+G" instead of just "Shift". The button stays in
            // "Recording..." until a non-modifier arrives.
            if (InputEventSource.IsModifierKey(key)) return;

            this.view.KeybindBeingEdited.Value = Keybind.None;

            var combo = new KeyBinding(key, shift: shift, ctrl: ctrl, alt: alt);

            switch (editedKeybind)
            {
                case Keybind.PushToTalk:
                    this.configuration.PushToTalkBinding = combo;
                    break;
                case Keybind.MuteMic:
                    this.configuration.MuteMicBinding = combo;
                    break;
                case Keybind.Deafen:
                    this.configuration.DeafenBinding = combo;
                    break;
                default:
                    return;
            }
            this.configuration.Save();
            this.inputManager.UpdateListeners();
        });
    }
}
