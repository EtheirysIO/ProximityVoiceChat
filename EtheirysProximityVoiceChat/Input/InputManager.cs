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
using EtheirysProximityVoiceChat.Audio;
using WindowsInput.Events;

namespace EtheirysProximityVoiceChat.Input;

public class InputManager
{
    private readonly Configuration configuration;
    private readonly InputEventSource inputEventSource;
    private readonly IAudioDeviceController audioDeviceController;
    private readonly VoiceRoomManager voiceRoomManager;

    private bool listenerSubscribed;

    public InputManager(
        Configuration configuration,
        InputEventSource inputEventSource,
        IAudioDeviceController audioDeviceController,
        VoiceRoomManager voiceRoomManager)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.inputEventSource = inputEventSource ?? throw new ArgumentNullException(nameof(inputEventSource));
        this.audioDeviceController = audioDeviceController ?? throw new ArgumentNullException(nameof(audioDeviceController));
        this.voiceRoomManager = voiceRoomManager ?? throw new ArgumentNullException(nameof(voiceRoomManager));

        UpdateListeners();
    }

    public void UpdateListeners()
    {
        if (ShouldListenToInput())
        {
            if (!this.listenerSubscribed)
            {
                this.inputEventSource.SubscribeToKeyDown(OnInputKeyDown);
                this.listenerSubscribed = true;
            }
        }
        else
        {
            if (this.listenerSubscribed)
            {
                this.inputEventSource.UnsubscribeToKeyDown(OnInputKeyDown);
                this.listenerSubscribed = false;
            }
        }
    }

    private bool ShouldListenToInput()
    {
        // Push-to-talk is handled in PushToTalkController.cs
        return !this.configuration.MuteMicBinding.IsEmpty() ||
            !this.configuration.DeafenBinding.IsEmpty();
    }

    private void OnInputKeyDown(KeyDown k)
    {
        var triggered = false;

        var mute = this.configuration.MuteMicBinding;
        if (!mute.IsEmpty() && Matches(k.Key, mute))
        {
            this.audioDeviceController.MuteMic = !this.audioDeviceController.MuteMic;
            triggered = true;
        }
        var deafen = this.configuration.DeafenBinding;
        if (!deafen.IsEmpty() && Matches(k.Key, deafen))
        {
            this.audioDeviceController.Deafen = !this.audioDeviceController.Deafen;
            triggered = true;
        }

        if (triggered)
        {
            this.voiceRoomManager.PushPlayerAudioState();
        }
    }

    /// <summary>
    /// Returns true iff the incoming key plus the current global Shift/Ctrl/Alt
    /// state matches the configured combo. Modifier state is queried from the
    /// OS at the moment of the key event, so it works from the hook thread.
    /// </summary>
    private static bool Matches(KeyCode pressedKey, KeyBinding binding)
    {
        if (pressedKey != binding.Key) return false;
        if (binding.Shift != InputEventSource.IsShiftDown()) return false;
        if (binding.Ctrl != InputEventSource.IsCtrlDown()) return false;
        if (binding.Alt != InputEventSource.IsAltDown()) return false;
        return true;
    }
}
