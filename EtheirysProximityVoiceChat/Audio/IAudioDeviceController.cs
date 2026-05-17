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
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace EtheirysProximityVoiceChat.Audio;

public interface IAudioDeviceController
{
    public bool IsAudioRecordingSourceActive { get; }
    public bool IsAudioPlaybackSourceActive { get; }

    public bool MuteMic { get; set; }
    public bool Deafen { get; set; }

    public bool PlayingBackMicAudio { get; set; }

    public bool AudioRecordingIsRequested { get; set; }
    public bool AudioPlaybackIsRequested { get; set; }

    public int AudioRecordingDeviceIndex { get; set; }
    public int AudioPlaybackDeviceIndex { get; set; }

    public event EventHandler<WaveInEventArgs>? OnAudioRecordingSourceDataAvailable;
    public bool RecordingDataHasActivity { get; }

    IEnumerable<string> GetAudioRecordingDevices();
    IEnumerable<string> GetAudioPlaybackDevices();

    void CreateAudioPlaybackChannel(string channelName);
    void RemoveAudioPlaybackChannel(string channelName);

    void AddPlaybackSample(string channelName, WaveInEventArgs sample);

    void ResetAllChannelsVolume(float volume);
    void SetChannelVolume(string channelName, float leftVolume, float rightVolume);

    bool ChannelHasActivity(string channelName);

    Task PlaySfx(CachedSound sound);
}
