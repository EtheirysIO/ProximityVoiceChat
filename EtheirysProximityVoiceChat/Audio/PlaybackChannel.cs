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
using NAudio.Wave.SampleProviders;
using System;
using WebRtcVadSharp;

namespace EtheirysProximityVoiceChat.Audio;

public sealed class PlaybackChannel : IDisposable
{
    public required MonoToStereoSampleProvider MonoToStereoSampleProvider { get; set; }
    public required BufferedWaveProvider BufferedWaveProvider { get; set; }
    public WaveInEventArgs? LastSampleAdded { get; set; }
    public int LastSampleAddedTimestampMs { get; set; }
    public int BufferClearedEventTimestampMs { get; set; }

    /// <summary>
    /// True if the most recent inbound audio frame for this peer arrived
    /// over the UDP transport. Drives the adaptive jitter-buffer target:
    /// 80 ms for UDP-delivered peers, 200 ms for TCP-delivered peers,
    /// because UDP doesn't have TCP's burst-after-stall pattern that the
    /// larger buffer was sized to absorb.
    /// </summary>
    public bool LastFrameFromUdp { get; set; }
    public WebRtcVad VoiceActivityDetector { get; set; } = new()
    {
        FrameLength = FrameLength.Is20ms,
        SampleRate = SampleRate.Is48kHz,
    };

    public void Dispose()
    {
        this.VoiceActivityDetector.Dispose();
    }
}
