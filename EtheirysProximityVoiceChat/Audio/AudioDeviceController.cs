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
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using EtheirysProximityVoiceChat.Log;
using RNNoise.NET;
using WebRtcVadSharp;

namespace EtheirysProximityVoiceChat.Audio;

public sealed class AudioDeviceController : IAudioDeviceController, IDisposable
{
    public bool IsAudioRecordingSourceActive => PlayingBackMicAudio || (!MuteMic && !Deafen && AudioRecordingIsRequested);
    public bool IsAudioPlaybackSourceActive => PlayingBackMicAudio || (!Deafen && AudioPlaybackIsRequested);

    public bool MuteMic
    {
        get => this.muteMic || this.Deafen;
        set
        {
            this.muteMic = this.configuration.MuteMic = value;
            if (!this.muteMic)
            {
                this.deafen = this.configuration.Deafen = false;
            }
            this.configuration.Save();
            UpdateSourceStates();
        }
    }
    private bool muteMic;

    public bool Deafen
    {
        get => this.deafen;
        set
        {
            this.deafen = this.configuration.Deafen = value;
            this.configuration.Save();
            UpdateSourceStates();
        }
    }
    private bool deafen;

    public bool PlayingBackMicAudio
    {
        get => this.playingBackMicAudio;
        set
        {
            this.playingBackMicAudio = value;
            ClearPlaybackBuffers(false);
            UpdateSourceStates();
        }
    }
    private bool playingBackMicAudio;

    public bool AudioRecordingIsRequested
    {
        get => this.audioRecordingIsRequested;
        set
        {
            this.audioRecordingIsRequested = value;
            UpdateSourceStates();
        }
    }
    private bool audioRecordingIsRequested;

    public bool AudioPlaybackIsRequested
    {
        get => this.audioPlaybackIsRequested;
        set
        {
            this.audioPlaybackIsRequested = value;
            UpdateSourceStates();
        }
    }
    private bool audioPlaybackIsRequested;

    public int AudioRecordingDeviceIndex
    {
        get => this.audioRecordingDeviceIndex;
        set
        {
            if (this.audioRecordingDeviceIndex != value)
            {
                this.audioRecordingDeviceIndex = value;
                this.configuration.SelectedAudioInputDeviceIndex = value;
                this.configuration.Save();

                DisposeAudioRecordingSource();
                UpdateSourceStates();
            }
        }
    }
    private int audioRecordingDeviceIndex;

    public int AudioPlaybackDeviceIndex
    {
        get => this.audioPlaybackDeviceIndex;
        set
        {
            if (this.audioPlaybackDeviceIndex != value)
            {
                this.audioPlaybackDeviceIndex = value;
                this.configuration.SelectedAudioOutputDeviceIndex = value;
                this.configuration.Save();

                DisposeAudioPlaybackSource();
                UpdateSourceStates();
            }
        }
    }
    private int audioPlaybackDeviceIndex;

    public event EventHandler<WaveInEventArgs>? OnAudioRecordingSourceDataAvailable;
    public bool RecordingDataHasActivity => this.lastAudioRecordingSourceData != null &&
        (this.configuration.SuppressNoise ?
            this.selfVoiceActivityDetector.HasSpeech(this.lastAudioRecordingSourceData.Buffer) :
            this.lastAudioRecordingSourceData.Buffer.Any(b => b != default));

    private const int SampleRate = 48000; // RNNoise frequency
    private const int FrameLength = 20; // 20 ms, for max compatibility
    private const int WaveOutDesiredLatency = 100;
    private const int WaveOutNumberOfBuffers = 5;
    private const int MinimumBufferClearIntervalMs = 5000;
    /// <summary>
    /// Number of consecutive non-speech frames the VAD must see after speech ends
    /// before we mute outgoing audio. At 20 ms/frame, 15 frames = 300 ms — long
    /// enough to preserve the tail of the last word but short enough to actually
    /// suppress background room noise between utterances.
    /// </summary>
    private const int SpeechHangoverFrames = 15;

    private readonly DalamudServices dalamud;
    private readonly Configuration configuration;
    private readonly ILogger logger;

    private readonly WaveFormat waveFormat = new(rate: 48000, bits: 16, channels: 1);
    private readonly Dictionary<string, PlaybackChannel> playbackChannels = [];
    private readonly int maxPlaybackChannelBufferSize;
    private readonly BufferedWaveProvider micPlaybackWaveProvider;
    private readonly MonoToStereoSampleProvider micPlaybackVolumeProvider;
    private readonly MixingSampleProvider outputSampleProvider;
    private readonly float[] denoiserFloatSamples = new float[GetSampleSize(SampleRate, FrameLength, 1) / 2];
    private readonly WebRtcVad selfVoiceActivityDetector = new()
    {
        FrameLength = WebRtcVadSharp.FrameLength.Is20ms,
        SampleRate = WebRtcVadSharp.SampleRate.Is48kHz,
        OperatingMode = WebRtcVadSharp.OperatingMode.Aggressive,
    };
    private readonly Lock recordingLock = new();
    private readonly Lock playbackChannelsLock = new();

    // Count of remaining "pass-through" frames after the VAD last detected speech.
    // Decremented each silent frame; reset whenever speech is detected.
    private int speechHangoverFramesRemaining;

    private WaveInEvent? audioRecordingSource;
    private WaveOutEvent? audioPlaybackSource;
    private Denoiser? denoiser;
    private volatile bool recording;
    private bool playingBack;
    private WaveInEventArgs? lastAudioRecordingSourceData;
    private ISampleProvider? currentSfx;
    private TaskCompletionSource? currentSfxTcs;

    public static byte[] ConvertAudioSampleToByteArray(WaveInEventArgs args)
    {
        var newArray = new byte[args.Buffer.Length + sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(newArray, (ushort)args.BytesRecorded);
        args.Buffer.CopyTo(newArray, sizeof(ushort));
        return newArray;
    }

    public static bool TryParseAudioSampleBytes(byte[] bytes, out WaveInEventArgs? args)
    {
        args = null;
        if (bytes.Length < sizeof(ushort))
        {
            return false;
        }
        Span<byte> bytesSpan = bytes;
        if (!BinaryPrimitives.TryReadUInt16BigEndian(bytesSpan[..sizeof(ushort)], out var bytesRecorded))
        {
            return false;
        }
        args = new WaveInEventArgs(bytesSpan[sizeof(ushort)..].ToArray(), bytesRecorded);
        return true;
    }

    public AudioDeviceController(DalamudServices dalamud, Configuration configuration, ILogger logger)
    {
        this.dalamud = dalamud;
        this.configuration = configuration;
        this.logger = logger;

        this.muteMic = configuration.MuteMic;
        this.deafen = configuration.Deafen;
        this.audioRecordingDeviceIndex = configuration.SelectedAudioInputDeviceIndex;
        this.audioPlaybackDeviceIndex = configuration.SelectedAudioOutputDeviceIndex;

        // This is how buffer size is calculated in WaveOutEvent
        this.maxPlaybackChannelBufferSize = this.waveFormat.ConvertLatencyToByteSize((WaveOutDesiredLatency + WaveOutNumberOfBuffers - 1) / WaveOutNumberOfBuffers) * WaveOutNumberOfBuffers;

        this.micPlaybackWaveProvider = new(this.waveFormat);
        this.micPlaybackVolumeProvider = new(this.micPlaybackWaveProvider.ToSampleProvider());
        this.outputSampleProvider = new([this.micPlaybackVolumeProvider])
        {
            ReadFully = true,
        };
        this.outputSampleProvider.MixerInputEnded += OnMixerInputEnded;
    }

    public void Dispose()
    {
        DisposeAudioRecordingSource();
        DisposeAudioPlaybackSource();
        this.outputSampleProvider.MixerInputEnded -= OnMixerInputEnded;
        this.currentSfxTcs?.TrySetCanceled();
        this.denoiser?.Dispose();
        this.selfVoiceActivityDetector.Dispose();
        lock (this.playbackChannelsLock)
        {
            foreach (var channel in this.playbackChannels.Values)
            {
                channel.Dispose();
            }
            this.playbackChannels.Clear();
        }
    }

    public IEnumerable<string> GetAudioRecordingDevices()
    {
        // The truncated names from WinMM (e.g. "Microphone (2- Arctis Nova 7 Ge")
        // would render the dropdown unreadable, so resolve full friendly names
        // from WASAPI and key them by the WinMM index that WaveInEvent uses.
        var fullNames = TryResolveFullDeviceNames(DataFlow.Capture, WaveIn.DeviceCount, GetWaveInProductName);

        for (int n = -1; n < WaveIn.DeviceCount; n++)
        {
            if (n == -1)
            {
                yield return "Default";
                continue;
            }
            yield return fullNames[n];
        }
    }

    public IEnumerable<string> GetAudioPlaybackDevices()
    {
        var fullNames = TryResolveFullDeviceNames(DataFlow.Render, WaveOut.DeviceCount, GetWaveOutProductName);

        for (int n = -1; n < WaveOut.DeviceCount; n++)
        {
            if (n == -1)
            {
                yield return "Default";
                continue;
            }
            yield return fullNames[n];
        }
    }

    /// <summary>
    /// Maps the legacy WinMM device index range [0, count) to full Windows
    /// "friendly" device names obtained from WASAPI's <c>MMDeviceEnumerator</c>.
    ///
    /// Why we can't just use one API: <c>WaveInEvent</c> / <c>WaveOutEvent</c>
    /// take a WinMM integer device index for actual capture/playback, so the
    /// rest of the plugin is locked to that addressing scheme. WinMM's
    /// <c>WaveInCapabilities.ProductName</c> is, however, capped at 31 chars
    /// (Windows' <c>MAXPNAMELEN</c>), which truncates real device names like
    /// "Microphone (2- Arctis Nova 7 Gen 2)" mid-string.
    ///
    /// WASAPI's <c>MMDevice.FriendlyName</c> returns the full untruncated
    /// name, so we enumerate active endpoints there and match each WinMM
    /// index to a WASAPI device by prefix: the truncated WinMM name is
    /// always a prefix of the full friendly name. The match is best-effort
    /// — if a device can't be matched (WASAPI unavailable, name collision,
    /// etc.) we fall back to whatever WinMM gave us, so the dropdown stays
    /// functional even when truncated.
    /// </summary>
    private static string[] TryResolveFullDeviceNames(DataFlow dataFlow, int waveDeviceCount, Func<int, string?> getWinMmName)
    {
        var result = new string[waveDeviceCount];
        var winMmNames = new string[waveDeviceCount];
        for (int n = 0; n < waveDeviceCount; n++)
        {
            winMmNames[n] = getWinMmName(n) ?? "<Unknown device>";
            // Seed the result with the (possibly truncated) WinMM name so any
            // device we fail to enrich below still has a usable label.
            result[n] = winMmNames[n];
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var endpoints = enumerator.EnumerateAudioEndPoints(dataFlow, DeviceState.Active);

            // Track which WASAPI endpoints we've already claimed so two WinMM
            // devices with the same 31-char prefix don't both bind to the same
            // friendly name (rare but possible on systems with duplicate USB
            // mics from the same manufacturer).
            var claimed = new bool[endpoints.Count];

            for (int n = 0; n < waveDeviceCount; n++)
            {
                var winMmName = winMmNames[n];
                if (string.IsNullOrEmpty(winMmName) || winMmName == "<Unknown device>") continue;

                for (int e = 0; e < endpoints.Count; e++)
                {
                    if (claimed[e]) continue;
                    string friendly;
                    try { friendly = endpoints[e].FriendlyName; }
                    catch { continue; }
                    if (string.IsNullOrEmpty(friendly)) continue;

                    // WinMM truncates at MAXPNAMELEN-1 (31 chars). If the
                    // friendly name starts with that exact prefix, it's the
                    // same device.
                    if (friendly.StartsWith(winMmName, StringComparison.Ordinal))
                    {
                        result[n] = friendly;
                        claimed[e] = true;
                        break;
                    }
                }
            }
        }
        catch
        {
            // WASAPI unavailable or threw — keep the WinMM names we seeded.
        }

        return result;
    }

    private static string? GetWaveInProductName(int index)
    {
        try { return WaveIn.GetCapabilities(index).ProductName; }
        catch (MmException) { return null; }
    }

    private static string? GetWaveOutProductName(int index)
    {
        try { return WaveOut.GetCapabilities(index).ProductName; }
        catch (MmException) { return null; }
    }

    /// <summary>
    /// Create the playback channel for a peer if it doesn't already exist.
    /// Idempotent — safe to call from presence-event handlers AND lazily on
    /// the first audio frame from a peer we haven't seen yet.
    /// </summary>
    public void CreateAudioPlaybackChannel(string channelName)
    {
        lock (this.playbackChannelsLock)
        {
            if (this.playbackChannels.ContainsKey(channelName)) return;
            var bfp = new BufferedWaveProvider(this.waveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferLength = this.waveFormat.AverageBytesPerSecond * 1,
            };
            var mssp = new MonoToStereoSampleProvider(bfp.ToSampleProvider());
            this.outputSampleProvider.AddMixerInput(mssp);
            this.playbackChannels.Add(channelName, new()
            {
                MonoToStereoSampleProvider = mssp,
                BufferedWaveProvider = bfp,
            });
        }
    }

    public void RemoveAudioPlaybackChannel(string channelName)
    {
        lock (this.playbackChannelsLock)
        {
            if (!this.playbackChannels.TryGetValue(channelName, out var channel)) return;
            channel.BufferedWaveProvider.ClearBuffer();
            this.outputSampleProvider.RemoveMixerInput(channel.MonoToStereoSampleProvider);
            this.playbackChannels.Remove(channelName);
            channel.Dispose();
        }
    }

    public void AddPlaybackSample(string channelName, WaveInEventArgs sample)
    {
        lock (this.playbackChannelsLock)
        {
            if (!this.playbackChannels.TryGetValue(channelName, out var channel)) return;
            var now = Environment.TickCount;
            // If the output device cannot read from the playback buffer as fast as it is filled,
            // then the playback buffer can get filled and introduce audio latency.
            // This can occur during high system load.
            // To remove this latency, we ensure the playback buffer never goes above the expected buffer size,
            // calculated from the intended output device latency and buffer count.
            if (channel.BufferedWaveProvider.BufferedBytes + sample.BytesRecorded > this.maxPlaybackChannelBufferSize)
            {
                // However, don't clear too often as this can cause audio "roboting"
                var timeSinceLastBufferClear = now - channel.BufferClearedEventTimestampMs;
                if (timeSinceLastBufferClear > MinimumBufferClearIntervalMs)
                {
                    channel.BufferedWaveProvider.ClearBuffer();
                    channel.BufferClearedEventTimestampMs = now;
                }
            }
            channel.BufferedWaveProvider.AddSamples(sample.Buffer, 0, sample.BytesRecorded);
            channel.LastSampleAdded = sample;
            channel.LastSampleAddedTimestampMs = now;
        }
    }

    public void ResetAllChannelsVolume(float volume)
    {
        if (this.Deafen || this.PlayingBackMicAudio)
        {
            volume = 0.0f;
        }
        lock (this.playbackChannelsLock)
        {
            foreach (var channel in this.playbackChannels)
            {
                var adjustedVolume = volume;
                if (adjustedVolume != 0.0f && this.configuration.PeerVolumes.TryGetValue(channel.Key, out var peerVolume))
                {
                    adjustedVolume *= peerVolume;
                }
                channel.Value.MonoToStereoSampleProvider.LeftVolume = adjustedVolume;
                channel.Value.MonoToStereoSampleProvider.RightVolume = adjustedVolume;
            }
        }
    }

    public void SetChannelVolume(string channelName, float leftVolume, float rightVolume)
    {
        if (this.Deafen || this.PlayingBackMicAudio)
        {
            leftVolume = rightVolume = 0.0f;
        }
        if ((leftVolume > 0.0f || rightVolume > 0.0f) && this.configuration.PeerVolumes.TryGetValue(channelName, out var peerVolume))
        {
            leftVolume *= peerVolume;
            rightVolume *= peerVolume;
        }
        lock (this.playbackChannelsLock)
        {
            if (!this.playbackChannels.TryGetValue(channelName, out var channel)) return;
            channel.MonoToStereoSampleProvider.LeftVolume = leftVolume;
            channel.MonoToStereoSampleProvider.RightVolume = rightVolume;
        }
    }

    public bool ChannelHasActivity(string channelName)
    {
        lock (this.playbackChannelsLock)
        {
            if (!this.playbackChannels.TryGetValue(channelName, out var channel))
            {
                return false;
            }
            if (channel.MonoToStereoSampleProvider.LeftVolume == 0.0f &&
                channel.MonoToStereoSampleProvider.RightVolume == 0.0f)
            {
                return false;
            }
            if (channel.LastSampleAdded == null)
            {
                return false;
            }
            // Recording the timestamp of the last added sample allows us to keep the sample valid
            // for activity purposes for longer.
            // This patches an issue where a buffer read would clear the buffer and indicate no
            // channel activity until the next sample was added (this would manifest as a rapidly
            // blinking activity indicator if the read/write buffers were small enough)
            if (channel.LastSampleAddedTimestampMs + 100 < Environment.TickCount &&
                channel.BufferedWaveProvider.BufferedBytes == 0)
            {
                return false;
            }
            return channel.VoiceActivityDetector.HasSpeech(channel.LastSampleAdded.Buffer);
        }
    }

    /// <returns>A Task that completes when the sound fully finished playing, and cancels if the sound is interrupted.</returns>
    public Task PlaySfx(CachedSound sound)
    {
        if (this.currentSfx != null)
        {
            this.outputSampleProvider.RemoveMixerInput(this.currentSfx);
            this.currentSfxTcs?.TrySetCanceled();
        }
        this.currentSfx = new MonoToStereoSampleProvider(new CachedSoundSampleProvider(sound));
        this.outputSampleProvider.AddMixerInput(this.currentSfx);
        this.currentSfxTcs = new TaskCompletionSource();
        return this.currentSfxTcs.Task;
    }

    private WaveInEvent? GetAudioRecordingSource(bool createIfNull)
    {
        if (this.audioRecordingSource == null && createIfNull)
        {
            if (this.AudioRecordingDeviceIndex >= WaveIn.DeviceCount)
            {
                // Avoid callbacks
                this.audioRecordingDeviceIndex = -1;
            }

            this.audioRecordingSource = new WaveInEvent
            {
                DeviceNumber = this.AudioRecordingDeviceIndex,
                WaveFormat = this.waveFormat,
                BufferMilliseconds = 20, // 20 ms for max compatibility
            };

            this.audioRecordingSource.RecordingStopped += (object? sender, StoppedEventArgs e) =>
            {
                this.recording = false;
            };
            this.audioRecordingSource.DataAvailable += this.OnAudioSourceDataAvailable;

            this.recording = false;
        }
        return this.audioRecordingSource;
    }

    private void DisposeAudioRecordingSource()
    {
        if (this.audioRecordingSource != null)
        {
            this.audioRecordingSource.Dispose();
            this.audioRecordingSource = null;
        }
    }

    private WaveOutEvent? GetAudioPlaybackSource(bool createIfNull)
    {
        if (this.audioPlaybackSource == null && createIfNull)
        {
            if (this.AudioPlaybackDeviceIndex >= WaveOut.DeviceCount)
            {
                // Avoid callbacks
                this.audioPlaybackDeviceIndex = -1;
            }

            this.audioPlaybackSource = new WaveOutEvent
            {
                DeviceNumber = this.AudioPlaybackDeviceIndex,
                DesiredLatency = WaveOutDesiredLatency,
                NumberOfBuffers = WaveOutNumberOfBuffers,
            };
            this.audioPlaybackSource.PlaybackStopped += (object? sender, StoppedEventArgs e) =>
            {
                this.playingBack = false;
            };
            this.audioPlaybackSource.Init(this.outputSampleProvider);

            this.playingBack = false;
        }
        return this.audioPlaybackSource;
    }

    private void DisposeAudioPlaybackSource()
    {
        if (this.audioPlaybackSource != null)
        {
            this.audioPlaybackSource.Dispose();
            this.audioPlaybackSource = null;
        }
    }

    private void OnMixerInputEnded(object? sender, SampleProviderEventArgs e)
    {
        if (e.SampleProvider == this.currentSfx)
        {
            this.currentSfxTcs?.TrySetResult();
            this.currentSfx = null;
            this.currentSfxTcs = null;
        }
    }

    private void OnAudioSourceDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!this.recording) { return; }
        //this.logger.Trace("Audio data received from recording device: {0} bytes recorded, {1}", e.BytesRecorded, e.Buffer);
        if (this.configuration.SuppressNoise && this.denoiser != null)
        {
            Convert16BitToFloat(e.Buffer, this.denoiserFloatSamples);
            // With incomplete audio data, this method can crash Dalamud
            this.denoiser.Denoise(this.denoiserFloatSamples);
            ConvertFloatTo16Bit(this.denoiserFloatSamples, e.Buffer);

            // Enhanced gate: after RNNoise removes spectral noise, run the VAD on the
            // cleaned frame. Frames the VAD doesn't classify as speech get silenced —
            // Opus DTX then encodes them as ~2-byte comfort-noise packets, so the
            // wire cost is negligible. A SpeechHangoverFrames-long tail of pass-through
            // after the last detected-speech frame avoids clipping word endings.
            if (this.selfVoiceActivityDetector.HasSpeech(e.Buffer))
            {
                this.speechHangoverFramesRemaining = SpeechHangoverFrames;
            }
            else if (this.speechHangoverFramesRemaining > 0)
            {
                this.speechHangoverFramesRemaining--;
            }
            else
            {
                System.Array.Clear(e.Buffer, 0, e.BytesRecorded);
            }
        }
        if (this.audioPlaybackSource != null && this.PlayingBackMicAudio)
        {
            this.micPlaybackWaveProvider.AddSamples(e.Buffer, 0, e.BytesRecorded);
            this.micPlaybackVolumeProvider.LeftVolume = this.micPlaybackVolumeProvider.RightVolume = this.configuration.MasterVolume;
        }
        this.lastAudioRecordingSourceData = e;
        this.OnAudioRecordingSourceDataAvailable?.Invoke(this, e);
    }

    private void ClearPlaybackBuffers(bool clearAll)
    {
        if (clearAll || this.PlayingBackMicAudio)
        {
            lock (this.playbackChannelsLock)
            {
                foreach (var channel in this.playbackChannels.Values)
                {
                    channel.BufferedWaveProvider.ClearBuffer();
                }
            }
        }
        if (clearAll || !this.PlayingBackMicAudio)
        {
            this.micPlaybackWaveProvider.ClearBuffer();
        }
    }

    private void UpdateSourceStates()
    {
        if (this.IsAudioRecordingSourceActive)
        {
            if (!this.recording)
            {
                this.logger.Debug("Starting audio recording source from device {0}", GetAudioRecordingSource(true)!.DeviceNumber);
                lock (this.recordingLock)
                {
                    GetAudioRecordingSource(true)!.StartRecording();
                }
                // We need a new denoiser here as the previous denoiser may have remaining incomplete audio data
                // that can cause audio popping the next time it is used.
                this.denoiser?.Dispose(); this.denoiser = null;
                this.denoiser = new();
                this.speechHangoverFramesRemaining = 0;
                this.recording = true;
            }
        }
        else
        {
            var recordingSource = GetAudioRecordingSource(false);
            if (recordingSource != null)
            {
                this.logger.Debug("Stopping audio recording source from device {0}", recordingSource.DeviceNumber);
                // This lock seems to fix rare crashes caused by native NAudio operations when stopping recording.
                lock (this.recordingLock)
                {
                    recordingSource.StopRecording();
                }
            }
            this.lastAudioRecordingSourceData = null;
            this.denoiser?.Dispose(); this.denoiser = null;
            this.recording = false;
        }

        if (this.IsAudioPlaybackSourceActive)
        {
            if (!this.playingBack)
            {
                this.logger.Debug("Starting audio playback source from device {0}", GetAudioPlaybackSource(true)!.DeviceNumber);
                ClearPlaybackBuffers(true);
                GetAudioPlaybackSource(true)!.Play();
                this.playingBack = true;
            }
        }
        else
        {
            var playbackSource = GetAudioPlaybackSource(false);
            if (playbackSource != null)
            {
                this.logger.Debug("Stopping audio playback source from device {0}", playbackSource.DeviceNumber);
                playbackSource.Stop();
            }
            if (this.currentSfx != null)
            {
                this.outputSampleProvider.RemoveMixerInput(this.currentSfx);
                this.currentSfxTcs?.TrySetCanceled();
                this.currentSfx = null;
                this.currentSfxTcs = null;
            }
            this.playingBack = false;
        }
    }

    // Utility methods taken from https://github.com/realcoloride/OpenVoiceSharp/blob/master/VoiceUtilities.cs

    /// <summary>
    /// Gets the sample size for a frame.
    /// </summary>
    /// <param name="channels">Set 1 for mono and 2 for stereo</param>
    /// <param name="float32">Float32 size is half</param>
    /// <returns></returns>
    private static int GetSampleSize(int sampleRate, int timeLengthMs, int channels)
        => ((int)(sampleRate * 16f / 8f * (timeLengthMs / 1000f) * channels));

    /// <summary>
    /// Converts 16 bit PCM data into float 32.
    /// Note that the float array must be half the size of the byte array.
    /// </summary>
    /// <param name="input">The 16 bit PCM data according to your needs.</param>
    /// <param name="output">The output data in which the result will be returned.</param>
    /// <returns>The 16 bit byte array.</returns>
    private static void Convert16BitToFloat(byte[] input, float[] output)
    {
        int outputIndex = 0;
        short sample;

        for (int n = 0; n < output.Length; n++)
        {
            sample = BitConverter.ToInt16(input, n * 2);
            output[outputIndex++] = sample / 32768f;
        }
    }

    /// <summary>
    /// Converts float 32 PCM data into 16 bit.
    /// Note that the byte array must be double the size of the float array.
    /// </summary>
    /// <param name="input">The float 32 PCM data according to your needs.</param>
    /// <param name="output">The output data in which the result will be returned.</param>
    /// <returns>The float32 PCM array.</returns>
    private static void ConvertFloatTo16Bit(float[] input, byte[] output)
    {
        int sampleIndex = 0, pcmIndex = 0;

        while (sampleIndex < input.Length)
        {
            // Math.Clamp solution found from https://github.com/mumble-voip/mumble/pull/5363
            short outsample = (short)Math.Clamp(input[sampleIndex] * short.MaxValue, short.MinValue, short.MaxValue);
            output[pcmIndex] = (byte)(outsample & 0xff);
            output[pcmIndex + 1] = (byte)((outsample >> 8) & 0xff);

            sampleIndex++;
            pcmIndex += 2;
        }
    }
}
