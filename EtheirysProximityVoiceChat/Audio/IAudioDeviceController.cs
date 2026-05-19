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

    /// <summary>
    /// Most recent fatal error from the capture path, or null when capture
    /// is healthy. The ConfigWindow renders this as a red banner under the
    /// mic dropdown with a Retry button so users actually see why their
    /// mic stopped working.
    /// </summary>
    Exception? LastMicError { get; }

    /// <summary>
    /// Peak sample magnitude (0..1) of the most recently captured frame
    /// <em>after</em> the <see cref="Configuration.InputBoost"/> gain stage,
    /// or 0 when capture is idle. Drives the live mic-input meter in the
    /// ConfigWindow.
    /// </summary>
    float MicInputPeak { get; }

    /// <summary>
    /// True when the boosted mic signal saturated within the last few
    /// hundred ms. The ConfigWindow flashes the input-level meter red so
    /// the user notices an over-boosted setting.
    /// </summary>
    bool MicInputClipped { get; }

    /// <summary>
    /// Tear down and re-create the capture device. Used as the "Retry"
    /// action under the <see cref="LastMicError"/> banner.
    /// </summary>
    void RestartMic();

    /// <summary>
    /// Update the WebRTC VAD operating mode on the live self-VAD instance.
    /// Mode is clamped to [0, 3]; out-of-range values fall back to 2 (Aggressive).
    /// </summary>
    void SetVadOperatingMode(int mode);

    IEnumerable<string> GetAudioRecordingDevices();
    IEnumerable<string> GetAudioPlaybackDevices();

    void CreateAudioPlaybackChannel(string channelName);
    void RemoveAudioPlaybackChannel(string channelName);

    /// <summary>
    /// Hand one decoded peer audio frame to the per-peer playback channel.
    /// <paramref name="fromUdp"/> tells the jitter buffer which transport
    /// delivered the frame so it can pick the appropriate target depth
    /// (smaller for UDP because UDP doesn't have TCP's burst-after-stall
    /// pattern; larger for the Socket.IO fallback).
    /// </summary>
    void AddPlaybackSample(string channelName, WaveInEventArgs sample, bool fromUdp = false);

    void ResetAllChannelsVolume(float volume);
    void SetChannelVolume(string channelName, float leftVolume, float rightVolume);

    bool ChannelHasActivity(string channelName);

    Task PlaySfx(CachedSound sound);
}
