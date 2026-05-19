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
using System.Reactive.Linq;
using AsyncAwaitBestPractices;
using Dalamud.Plugin.Services;
using EtheirysProximityVoiceChat.Audio;
using EtheirysProximityVoiceChat.Extensions;
using EtheirysProximityVoiceChat.Input;
using EtheirysProximityVoiceChat.Log;
using EtheirysProximityVoiceChat.UI.View;
using Reactive.Bindings;

namespace EtheirysProximityVoiceChat.UI.Presenter;

public class MainWindowPresenter(
    MainWindow view,
    Configuration configuration,
    DalamudServices dalamud,
    IAudioDeviceController audioDeviceController,
    VoiceRoomManager voiceRoomManager,
    ILogger logger) : IPluginUIPresenter
{
    public IPluginUIView View => this.view;

    private readonly MainWindow view = view;
    private readonly Configuration configuration = configuration;
    private readonly DalamudServices dalamud = dalamud;
    private readonly IAudioDeviceController audioDeviceController = audioDeviceController;
    private readonly VoiceRoomManager voiceRoomManager = voiceRoomManager;
    private readonly ILogger logger = logger;

    public void SetupBindings()
    {
        BindVariables();
        BindActions();
        // ConfigWindowPresenter.SetupBindings is now invoked by PluginUIContainer's
        // foreach over IPluginUIPresenter[] — no need to cascade from here.
    }

    private void BindVariables()
    {
        Bind(this.view.PublicRoom,
            b =>
            {
                this.configuration.PublicRoom = b; this.configuration.Save();
                this.voiceRoomManager.SignalingChannel?.ClearLatestError();
            },
            this.configuration.PublicRoom);
        Bind(this.view.RoomName,
            s => { this.configuration.RoomName = s; this.configuration.Save(); }, this.configuration.RoomName);
        Bind(this.view.RoomPassword,
            s => { this.configuration.RoomPassword = s; this.configuration.Save(); }, this.configuration.RoomPassword);
        Bind(this.view.RoomUnlisted,
            b => { this.configuration.RoomUnlisted = b; this.configuration.Save(); }, this.configuration.RoomUnlisted);
    }

    private void BindActions()
    {
        this.view.MuteMic.Subscribe(b =>
        {
            this.audioDeviceController.MuteMic = b;
            this.voiceRoomManager.PushPlayerAudioState();
        });
        this.view.Deafen.Subscribe(b =>
        {
            this.audioDeviceController.Deafen = b;
            this.voiceRoomManager.PushPlayerAudioState();
        });

        this.view.JoinVoiceRoom.Subscribe(_ =>
        {
            this.voiceRoomManager.SignalingChannel?.ClearLatestError();
            if (this.view.PublicRoom.Value)
            {
                this.voiceRoomManager.JoinPublicVoiceRoom();
            }
            else
            {
                // v4 private rooms are free-form. The user types whatever they
                // want; we don't autofill with the character name any more.
                // The server validates length / blocklist and rejects with
                // SignalingChannelError.InvalidPrivateRoomName on failure.
                var roomName = (this.view.RoomName.Value ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(roomName))
                {
                    this.logger.Info("Private room name is empty, ignoring Join click.");
                    return;
                }
                var currentWorld = GetCurrentWorldName();
                if (string.IsNullOrWhiteSpace(currentWorld))
                {
                    this.logger.Error("Current world is not available — try again after the game finishes loading.");
                    return;
                }
                var listed = !this.view.RoomUnlisted.Value;
                this.voiceRoomManager.JoinPrivateVoiceRoom(
                    roomName,
                    this.view.RoomPassword.Value ?? string.Empty,
                    listed,
                    currentWorld);
            }
        });

        this.view.LeaveVoiceRoom.Subscribe(_ => this.voiceRoomManager.LeaveVoiceRoom(false).SafeFireAndForget(ex => this.logger.Error(ex.ToString())));

        this.view.SetPeerVolume.Subscribe(pv =>
        {
            if (pv.volume == 1.0f)
            {
                this.configuration.PeerVolumes.Remove(pv.playerName);
            }
            else
            {
                this.configuration.PeerVolumes[pv.playerName] = pv.volume;
            }
            this.configuration.Save();
        });
    }

    /// <summary>
    /// v4 private rooms need the *current* world (where the character is
    /// physically standing, not their home world). Used to suffix the
    /// user-typed room name with <c>@World</c> on the server side. Returns
    /// empty string if the world isn't available yet (e.g. mid-loading).
    /// </summary>
    private string GetCurrentWorldName()
    {
        try
        {
            var world = this.dalamud.PlayerState.CurrentWorld;
            return world.IsValid ? world.Value.Name.ExtractText() ?? string.Empty : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
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
                    this.logger.Error("Failed to persist setting update: {0}", ex);
                }
            });
    }
}
