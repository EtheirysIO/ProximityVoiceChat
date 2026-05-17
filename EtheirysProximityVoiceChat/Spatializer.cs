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
using AsyncAwaitBestPractices;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using EtheirysProximityVoiceChat.Audio;
using EtheirysProximityVoiceChat.Extensions;
using EtheirysProximityVoiceChat.Input;
using EtheirysProximityVoiceChat.Log;
using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace EtheirysProximityVoiceChat;

public sealed class Spatializer : IDisposable
{
    private readonly DalamudServices dalamud;
    private readonly Configuration configuration;
    private readonly VoiceRoomManager voiceRoomManager;
    private readonly IAudioDeviceController audioDeviceController;
    private readonly ILogger logger;

    private readonly PeriodicTimer updateTimer = new(TimeSpan.FromMilliseconds(100));
    private readonly SemaphoreSlim frameworkThreadSemaphore = new(1, 1);

    private bool isDisposed;

    public Spatializer(
        DalamudServices dalamud,
        Configuration configuration,
        VoiceRoomManager voiceRoomManager,
        IAudioDeviceController audioDeviceController,
        ILogger logger)
    {
        this.dalamud = dalamud;
        this.configuration = configuration;
        this.voiceRoomManager = voiceRoomManager;
        this.audioDeviceController = audioDeviceController;
        this.logger = logger;
    }

    public void StartUpdateLoop()
    {
        Task.Run(async delegate
        {
            while (await this.updateTimer.WaitForNextTickAsync())
            {
                await frameworkThreadSemaphore.WaitAsync();
                if (isDisposed)
                {
                    return;
                }
                this.dalamud.Framework.Run(UpdatePlayerVolumes).SafeFireAndForget(ex => this.logger.Error(ex.ToString()));
            }
        }).SafeFireAndForget(ex => this.logger.Error(ex.ToString()));
    }

    public void Dispose()
    {
        isDisposed = true;
        this.updateTimer.Dispose();
    }

    private void UpdatePlayerVolumes()
    {
        try
        {
            // Use polling to set individual channel volumes.
            // The search is done by iterating through all GameObjects and finding any connected players out of them,
            // so we reset all players volumes before calculating any volumes in case the players cannot be found.
            var defaultVolume = (this.voiceRoomManager.InPublicRoom || this.configuration.MuteOutOfMapPlayers) ? 0.0f : 1.0f;
            this.audioDeviceController.ResetAllChannelsVolume(defaultVolume * this.configuration.MasterVolume);
            foreach (var tp in this.voiceRoomManager.TrackedPlayers.Values)
            {
                tp.Distance = float.NaN;
                tp.Volume = defaultVolume;
            }

            // Conditions where volume is impossible/unnecessary to calculate
            if (!this.dalamud.PlayerState.IsLoaded)
            {
                return;
            }
            if (this.voiceRoomManager.Presence == null)
            {
                return;
            }
            if (this.voiceRoomManager.Presence.Peers.Count == 0)
            {
                return;
            }

            var thisTick = Environment.TickCount;

            foreach (var player in this.dalamud.ObjectTable.GetPlayers())
            {
                var playerName = player.GetPlayerFullName();
                if (playerName != null &&
                    this.voiceRoomManager.Presence.Peers.TryGetValue(playerName, out var peer))
                {
                    var trackedPlayer = this.voiceRoomManager.TrackedPlayers.TryGetValue(playerName, out var tp) ? tp : null;
                    CalculateSpatialValues(player, trackedPlayer, thisTick,
                       out var leftVolume, out var rightVolume, out var distance, out var volume);

                    leftVolume *= this.configuration.MasterVolume;
                    rightVolume *= this.configuration.MasterVolume;
                    this.audioDeviceController.SetChannelVolume(peer.PeerId, leftVolume, rightVolume);

                    if (trackedPlayer != null)
                    {
                        trackedPlayer.Distance = distance;
                        trackedPlayer.Volume = volume;
                    }
                }
            }
        }
        finally
        {
            this.frameworkThreadSemaphore.Release();
        }
    }

    private void CalculateSpatialValues(
        IPlayerCharacter otherPlayer,
        TrackedPlayer? otherTrackedPlayer,
        int thisTick,
        out float leftVolume,
        out float rightVolume,
        out float distance,
        out float volume)
    {
        Vector3 toTarget;
        if (this.dalamud.ObjectTable.LocalPlayer != null)
        {
            toTarget = otherPlayer.Position - this.dalamud.ObjectTable.LocalPlayer.Position;
        }
        else
        {
            toTarget = Vector3.Zero;
        }
        distance = toTarget.Length();
        var deathMute = this.configuration.MuteDeadPlayers;

        if (this.configuration.UnmuteAllIfDead && (this.dalamud.ObjectTable.LocalPlayer?.IsDead ?? false))
        {
            deathMute = false;
        }
        else if (otherTrackedPlayer != null)
        {
            if (!otherPlayer.IsDead)
            {
                otherTrackedPlayer.LastTickFoundAlive = thisTick;
                deathMute = false;
            }
            else if (deathMute &&
                otherTrackedPlayer.LastTickFoundAlive.HasValue &&
                thisTick - otherTrackedPlayer.LastTickFoundAlive < this.configuration.MuteDeadPlayersDelayMs)
            {
                deathMute = false;
            }
        }
        else
        {
            deathMute = deathMute && otherPlayer.IsDead;
        }

        if (deathMute)
        {
            volume = leftVolume = rightVolume = 0;
            //this.logger.Debug("Player {0} is dead, setting volume to {1}", peer.PeerId, volume);
        }
        else
        {
            volume = leftVolume = rightVolume = CalculateVolume(distance);
            //this.logger.Debug("Player {0} is {1} units away, setting volume to {2}", peer.PeerId, distance, volume);

            if (this.configuration.EnableSpatialization)
            {
                SpatializeVolume(volume, toTarget, out leftVolume, out rightVolume);
            }
        }
    }

    private float CalculateVolume(float distance)
    {
        var minDistance = this.configuration.FalloffModel.MinimumDistance;
        var maxDistance = this.configuration.FalloffModel.MaximumDistance;
        var falloffFactor = this.configuration.FalloffModel.FalloffFactor;
        float volume;
        try
        {
            float scale;
            switch (this.configuration.FalloffModel.Type)
            {
                case AudioFalloffModel.FalloffType.None:
                    volume = 1.0f;
                    break;
                case AudioFalloffModel.FalloffType.InverseDistance:
                    distance = Math.Clamp(distance, minDistance, maxDistance);
                    scale = MathF.Pow((maxDistance - distance) / (maxDistance - minDistance), distance / maxDistance);
                    volume = minDistance / (minDistance + falloffFactor * (distance - minDistance)) * scale;
                    break;
                case AudioFalloffModel.FalloffType.ExponentialDistance:
                    distance = Math.Clamp(distance, minDistance, maxDistance);
                    scale = MathF.Pow((maxDistance - distance) / (maxDistance - minDistance), distance / maxDistance);
                    volume = MathF.Pow(distance / minDistance, -falloffFactor) * scale;
                    break;
                case AudioFalloffModel.FalloffType.LinearDistance:
                    distance = Math.Clamp(distance, minDistance, maxDistance);
                    volume = 1 - falloffFactor * (distance - minDistance) / (maxDistance - minDistance);
                    break;
                default:
                    volume = 1.0f;
                    break;
            }
        }
        catch (Exception e) when (e is DivideByZeroException or ArgumentException)
        {
            volume = 1.0f;
        }
        volume = Math.Clamp(volume, 0.0f, 1.0f);
        return volume;
    }

    private void SpatializeVolume(float volume, Vector3 toTarget, out float leftVolume, out float rightVolume)
    {
        leftVolume = rightVolume = volume;
        var distance = toTarget.Length();
        if (volume == 0 || distance == 0)
        {
            return;
        }

        var lookAtVector = Vector3.Zero;
        unsafe
        {
            // https://github.com/NotNite/Linkpearl/blob/main/Linkpearl/Plugin.cs
            var renderingCamera = *FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager.Instance()->CurrentCamera;
            lookAtVector = new Vector3(renderingCamera.ViewMatrix.M13, renderingCamera.ViewMatrix.M23, renderingCamera.ViewMatrix.M33);
        }

        if (lookAtVector.LengthSquared() == 0)
        {
            return;
        }

        var toTargetHorizontal = Vector3.Normalize(new Vector3(toTarget.X, 0, toTarget.Z));
        var cameraForwardHorizontal = Vector3.Normalize(new Vector3(lookAtVector.X, 0, lookAtVector.Z));
        var dot = Vector3.Dot(toTargetHorizontal, cameraForwardHorizontal);
        var cross = Vector3.Dot(Vector3.Cross(toTargetHorizontal, cameraForwardHorizontal), Vector3.UnitY);
        var cosine = -dot;
        var sine = cross;
        var angle = MathF.Acos(cosine);
        if (float.IsNaN(angle)) { angle = 0f; }
        if (sine < 0) { angle = -angle; }

        var minDistance = this.configuration.FalloffModel.MinimumDistance;
        float pan = 1;
        if (minDistance > 0 && distance < minDistance)
        {
            // Linear pan dropoff
            pan = distance / minDistance;
            // Arc pan dropoff
            //var d = distance / minDistance;
            //pan = 1 - MathF.Sqrt(1 - d * d);
        }
        pan = Math.Abs(pan * MathF.Sin(angle));
        if (angle > 0)
        {
            // Left of player
            rightVolume *= 1 - pan;
        }
        else
        {
            // Right of player
            leftVolume *= 1 - pan;
        }
    }
}
