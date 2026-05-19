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
    // Note: MuteMic / Deafen are deliberately NOT in this condition. With
    // WASAPI capture, closing / reopening the device on every mute toggle
    // costs ~100+ ms of init time per cycle, which manifests as the first
    // syllable after unmute being clipped. The new pattern keeps the
    // capture device hot whenever the plugin wants any voice activity at
    // all, and gates outgoing-frame emission at the data callback instead
    // (see OnAudioSourceDataAvailable).
    public bool IsAudioRecordingSourceActive => PlayingBackMicAudio || AudioRecordingIsRequested;
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
                // Persist by WASAPI ID, not integer index — that's what
                // survives reboots / USB hotplug. The legacy int field is
                // kept in sync for one release for backwards compatibility.
                this.configuration.SelectedAudioInputDeviceWasapiId = LookupRecordingWasapiId(value) ?? string.Empty;
                this.configuration.SelectedAudioInputDeviceIndex = value;
                this.configuration.Save();

                // A new device means a clean slate: clear any error from the
                // previous device so the UI banner goes away as soon as the
                // user picks a different mic.
                this.LastMicError = null;

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
    /// <summary>
    /// Target depth of each peer's playback jitter buffer in milliseconds,
    /// chosen per-peer based on the transport that delivered their most
    /// recent frame. UDP-delivered peers get the smaller target because
    /// UDP doesn't have TCP's burst-after-stall pattern that the larger
    /// buffer was sized to absorb; the smaller target also cuts mouth-to-
    /// ear latency by ~120 ms for UDP peers. TCP-fallback peers keep the
    /// larger 200 ms target because their packets can still arrive in
    /// bursts after a transient stall, and the smaller target underruns
    /// during those bursts.
    /// </summary>
    private const int PerPeerJitterBufferTargetUdpMs = 80;
    private const int PerPeerJitterBufferTargetTcpMs = 200;

    /// <summary>
    /// Maximum buffer capacity we'll ever reach — the worst-case bound for
    /// the TCP fallback. The per-frame logic in <see cref="AddPlaybackSample"/>
    /// picks an effective target between
    /// <see cref="PerPeerJitterBufferTargetUdpMs"/> and
    /// <see cref="PerPeerJitterBufferTargetTcpMs"/> based on the most
    /// recent frame's transport.
    /// </summary>
    private const int PerPeerJitterBufferTargetMs = PerPeerJitterBufferTargetTcpMs;
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

    private WasapiAudioRecorder? audioRecordingSource;
    private WaveOutEvent? audioPlaybackSource;
    private Denoiser? denoiser;
    private volatile bool recording;
    private bool playingBack;
    private WaveInEventArgs? lastAudioRecordingSourceData;
    private ISampleProvider? currentSfx;
    private TaskCompletionSource? currentSfxTcs;

    /// <summary>
    /// Parallel to the friendly-name list returned by
    /// <see cref="GetAudioRecordingDevices"/>. Position 0 is null (the
    /// system-default capture device), positions 1+ are stable WASAPI
    /// device IDs. The UI keeps talking in terms of integer indices, but
    /// what we actually persist (and reopen with) is the ID at the matching
    /// position — so plugging / unplugging USB devices across reboots no
    /// longer silently switches the active mic.
    /// </summary>
    private readonly List<string?> audioRecordingDeviceIds = new();

    /// <summary>
    /// Last fatal error from the WASAPI capture path, or null when capture
    /// is healthy. Surfaced by the ConfigWindow as a red banner under the
    /// device dropdown so users actually find out why their mic went silent
    /// instead of just hearing nothing.
    /// </summary>
    public Exception? LastMicError { get; private set; }

    /// <summary>
    /// Peak sample magnitude (0..1) of the most recently captured 20 ms
    /// frame <em>after</em> the <see cref="Configuration.InputBoost"/> gain
    /// has been applied — the level the user is actually transmitting, and
    /// therefore the level the UI meter should reflect. 0 when not
    /// recording. Updated on the capture thread; one-frame lag is invisible
    /// at 50 Hz.
    /// </summary>
    public float MicInputPeak => this.micInputPeakPostBoost;
    private float micInputPeakPostBoost;

    /// <summary>
    /// True when the boosted mic signal saturated (peak &gt;= 0.99) within
    /// the last <see cref="ClipIndicatorHoldMs"/> ms. The ConfigWindow
    /// colours the input-level meter red while this is true so an
    /// over-boosted user notices immediately.
    /// </summary>
    public bool MicInputClipped => Environment.TickCount64 - this.lastClipTickMs < ClipIndicatorHoldMs;
    private const int ClipIndicatorHoldMs = 300;
    private long lastClipTickMs = long.MinValue;

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

        // Eagerly enumerate WASAPI capture devices so we can resolve the
        // persisted WASAPI ID into a UI-friendly position before the first
        // UI frame draws. Without this the UI dropdown defaults to position
        // 0 for one frame, which flicker-clobbers the user's saved device.
        _ = GetAudioRecordingDevices();
        MigrateLegacyRecordingDeviceIndexIfNeeded();
        this.audioRecordingDeviceIndex = ResolveRecordingDeviceIndexFromWasapiId(configuration.SelectedAudioInputDeviceWasapiId);

        this.audioPlaybackDeviceIndex = configuration.SelectedAudioOutputDeviceIndex;

        // Align the field-initialized VAD instance with the persisted user
        // preference. The field default (Aggressive) acts as a fallback for
        // existing configs that don't carry VadSensitivity yet.
        SetVadOperatingMode(configuration.VadSensitivity);

        // Decoupled from WaveOutDesiredLatency: the OS-level audio queue
        // can stay short (low local playback latency) while the per-peer
        // jitter buffer is generous enough to absorb TCP-burst inter-arrival
        // variation on slower / lossier links. See PerPeerJitterBufferTargetMs.
        this.maxPlaybackChannelBufferSize = this.waveFormat.AverageBytesPerSecond * PerPeerJitterBufferTargetMs / 1000;

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

    public void SetVadOperatingMode(int mode)
    {
        // Clamp into WebRtcVadSharp.OperatingMode (Quality=0..VeryAggressive=3).
        // Any out-of-range value falls back to Aggressive (2) -- the historical
        // hardcoded default before this setting existed.
        var clamped = mode is >= 0 and <= 3 ? mode : 2;
        this.selfVoiceActivityDetector.OperatingMode = (WebRtcVadSharp.OperatingMode)clamped;
    }

    public IEnumerable<string> GetAudioRecordingDevices()
    {
        // Enumerate WASAPI capture endpoints directly — full friendly names,
        // stable IDs, and the same view of devices the Windows Sound Control
        // Panel shows. The legacy WinMM index → friendly-name workaround in
        // TryResolveFullDeviceNames is no longer needed for the input side.
        //
        // Rebuild the parallel id list so the persisted WASAPI ID
        // (Configuration.SelectedAudioInputDeviceWasapiId) can be mapped to
        // a UI position on demand.
        this.audioRecordingDeviceIds.Clear();
        this.audioRecordingDeviceIds.Add(null); // position 0 == "Default"

        var names = new List<string> { "Default" };
        try
        {
            foreach (var (friendlyName, id) in WasapiAudioRecorder.EnumerateCaptureEndpoints())
            {
                names.Add(friendlyName);
                this.audioRecordingDeviceIds.Add(id);
            }
        }
        catch (Exception ex)
        {
            // MMDeviceEnumerator can throw on a misconfigured audio service.
            // Logging at Error here so users / maintainers can see why the
            // dropdown is empty instead of silently showing only "Default".
            this.logger.Error("Failed to enumerate WASAPI capture endpoints: {0}", ex);
        }
        return names;
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

    public void AddPlaybackSample(string channelName, WaveInEventArgs sample, bool fromUdp = false)
    {
        lock (this.playbackChannelsLock)
        {
            if (!this.playbackChannels.TryGetValue(channelName, out var channel)) return;
            // Tag the channel with its current transport so the adaptive
            // jitter buffer target picks the right depth. A peer's transport
            // can flip during a session (e.g. UDP went silent → server
            // reverts to TCP); we just trust the most recent frame.
            channel.LastFrameFromUdp = fromUdp;

            // Per-frame effective target: small for UDP (which doesn't
            // burst-after-stall), large for TCP (which does). The
            // BufferedWaveProvider's actual capacity is set wide at channel
            // creation; this is a soft target that drives drop-oldest
            // overflow handling below.
            var effectiveTargetMs = fromUdp ? PerPeerJitterBufferTargetUdpMs : PerPeerJitterBufferTargetTcpMs;
            var effectiveTargetBytes = this.waveFormat.AverageBytesPerSecond * effectiveTargetMs / 1000;

            // Jitter buffer: keep the per-peer playback queue at or below
            // PerPeerJitterBufferTargetMs (~200 ms). When new samples would
            // push past that target, drop the OLDEST bytes from the queue
            // rather than discarding the new sample (which is what the
            // BufferedWaveProvider does on its own with
            // DiscardOnBufferOverflow=true, producing audible gaps) or
            // clearing the entire queue (the old rate-limited behaviour,
            // which the existing codebase comment correctly identified as
            // a source of "roboting").
            //
            // Dropping a small chunk of *old* audio per overflow event
            // means the listener loses maybe 20–40 ms of buffered audio
            // rather than continuing to hear stale content while new
            // packets are silently discarded; the result is brief,
            // localised skips instead of sustained gaps. This is also the
            // mechanism that recovers from sender / receiver clock drift,
            // which the old "clear every 5 s" code was trying to handle.
            var bufferedBytes = channel.BufferedWaveProvider.BufferedBytes;
            var afterAdd = bufferedBytes + sample.BytesRecorded;
            if (afterAdd > effectiveTargetBytes)
            {
                var bytesToDrop = afterAdd - effectiveTargetBytes;
                // Round to a whole sample to avoid splitting a 16-bit value.
                if ((bytesToDrop & 1) == 1) bytesToDrop++;
                // Cap at the currently-buffered amount: if we're somehow
                // asked to drop more than is queued, we just drain what's
                // there and the new sample will fit cleanly.
                if (bytesToDrop > bufferedBytes) bytesToDrop = bufferedBytes;

                var discard = new byte[bytesToDrop];
                int dropped = 0;
                while (dropped < bytesToDrop)
                {
                    var n = channel.BufferedWaveProvider.Read(discard, dropped, bytesToDrop - dropped);
                    if (n <= 0) break;
                    dropped += n;
                }
            }

            channel.BufferedWaveProvider.AddSamples(sample.Buffer, 0, sample.BytesRecorded);
            channel.LastSampleAdded = sample;
            channel.LastSampleAddedTimestampMs = Environment.TickCount;
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

    private WasapiAudioRecorder? GetAudioRecordingSource(bool createIfNull)
    {
        if (this.audioRecordingSource == null && createIfNull)
        {
            // Resolve the persisted WASAPI ID at create-time so device hot-
            // swaps between StartRecording calls are picked up automatically
            // — and so a user who unplugs the saved device after launch
            // falls back to the default cleanly (handled inside the recorder).
            var wasapiId = LookupRecordingWasapiId(this.audioRecordingDeviceIndex);

            try
            {
                this.audioRecordingSource = new WasapiAudioRecorder(wasapiId, this.logger);
            }
            catch (Exception ex)
            {
                // Constructor can throw if the audio service is unavailable
                // or MMDeviceEnumerator fails. Surface this rather than
                // letting it crash UpdateSourceStates() and the calling
                // VoiceRoomManager path.
                this.logger.Error("Failed to create WasapiAudioRecorder: {0}", ex);
                this.LastMicError = ex;
                return null;
            }

            this.audioRecordingSource.RecordingStopped += OnRecordingSourceStopped;
            this.audioRecordingSource.DataAvailable += this.OnAudioSourceDataAvailable;

            this.recording = false;
        }
        return this.audioRecordingSource;
    }

    private void OnRecordingSourceStopped(object? sender, StoppedEventArgs e)
    {
        this.recording = false;
        // Promote the WASAPI error to a property the UI watches. An expected
        // user-initiated stop has Exception == null and leaves LastMicError
        // alone, so we don't flash an error banner when the user disables
        // their mic via the plugin settings.
        if (e.Exception != null)
        {
            this.LastMicError = e.Exception;
        }
    }

    /// <summary>
    /// Tear down and re-create the WASAPI capture source. Wired to the
    /// ConfigWindow's "Retry" button (next to the LastMicError banner) so
    /// users can recover after a transient failure (e.g. they re-enabled
    /// their mic in Windows Sound Control Panel) without restarting the
    /// plugin.
    /// </summary>
    public void RestartMic()
    {
        this.LastMicError = null;
        DisposeAudioRecordingSource();
        UpdateSourceStates();
    }

    /// <summary>
    /// Look up the WASAPI ID parked at a given UI position.
    /// <paramref name="audioRecordingIndex"/> uses the -1-is-Default
    /// convention (i.e. it's <c>AudioRecordingDeviceIndex</c>). Returns null
    /// when the index points to "Default" or out of range; the recorder
    /// treats null as "use system default capture device".
    /// </summary>
    private string? LookupRecordingWasapiId(int audioRecordingIndex)
    {
        var pos = audioRecordingIndex + 1; // -1 → 0, 0 → 1, etc.
        if (pos <= 0 || pos >= this.audioRecordingDeviceIds.Count) return null;
        return this.audioRecordingDeviceIds[pos];
    }

    /// <summary>
    /// Find which UI position holds a given WASAPI ID. Returns the
    /// corresponding <c>AudioRecordingDeviceIndex</c> value (i.e. -1 for
    /// Default, 0+ for a specific device). When the ID isn't in the current
    /// device list — saved device unplugged, audio service rebooted, etc. —
    /// returns -1 so the UI shows "Default" rather than a stale selection.
    /// </summary>
    private int ResolveRecordingDeviceIndexFromWasapiId(string? wasapiId)
    {
        if (string.IsNullOrEmpty(wasapiId)) return -1;
        for (int i = 1; i < this.audioRecordingDeviceIds.Count; i++)
        {
            if (this.audioRecordingDeviceIds[i] == wasapiId) return i - 1;
        }
        return -1;
    }

    /// <summary>
    /// One-time migration from the legacy WinMM integer device index to a
    /// stable WASAPI ID. Runs in the constructor if
    /// <see cref="Configuration.SelectedAudioInputDeviceWasapiId"/> is empty
    /// AND <see cref="Configuration.SelectedAudioInputDeviceIndex"/> points
    /// at a real WinMM device. Matches the truncated WinMM friendly-name
    /// prefix against full WASAPI <c>MMDevice.FriendlyName</c>s — the same
    /// approach the old <c>TryResolveFullDeviceNames</c> used for display.
    /// </summary>
    private void MigrateLegacyRecordingDeviceIndexIfNeeded()
    {
        if (!string.IsNullOrEmpty(this.configuration.SelectedAudioInputDeviceWasapiId)) return;
        var legacyIndex = this.configuration.SelectedAudioInputDeviceIndex;
        if (legacyIndex < 0) return;

        string? winMmName = null;
        try { winMmName = WaveIn.GetCapabilities(legacyIndex).ProductName; }
        catch (Exception ex)
        {
            this.logger.Debug("Legacy WinMM device {0} no longer present for migration: {1}", legacyIndex, ex.Message);
            return;
        }
        if (string.IsNullOrWhiteSpace(winMmName)) return;

        // WinMM names are capped at 31 chars (MAXPNAMELEN-1). A WASAPI
        // friendly name that starts with that prefix is the same device.
        string? matchedId = null;
        string? matchedName = null;
        foreach (var (friendlyName, id) in WasapiAudioRecorder.EnumerateCaptureEndpoints())
        {
            if (friendlyName.StartsWith(winMmName, StringComparison.Ordinal))
            {
                matchedId = id;
                matchedName = friendlyName;
                break;
            }
        }

        if (matchedId != null)
        {
            this.logger.Info("Migrated mic selection: WinMM index {0} ('{1}') -> WASAPI '{2}' (id={3}).",
                legacyIndex, winMmName, matchedName ?? "(unnamed)", matchedId);
            this.configuration.SelectedAudioInputDeviceWasapiId = matchedId;
            this.configuration.Save();
        }
        else
        {
            this.logger.Info("Could not match legacy WinMM device '{0}' to any WASAPI endpoint; falling back to default capture device.", winMmName);
        }
    }

    private void DisposeAudioRecordingSource()
    {
        if (this.audioRecordingSource != null)
        {
            this.audioRecordingSource.Dispose();
            this.audioRecordingSource = null;
        }
        // Reset the "recording" flag so the next UpdateSourceStates call sees
        // "not recording" and re-creates the source. Without this, a mid-
        // session device switch (which calls DisposeAudioRecordingSource +
        // UpdateSourceStates back-to-back while still in a room) leaves
        // recording=true but audioRecordingSource=null — and UpdateSourceStates'
        // start-branch guard (`if (!this.recording)`) then skips creating the
        // new recorder, so no DataAvailable events fire and transmission
        // silently stops. The level meter and pickup indicator look like
        // they're still working because they read the LAST frame's buffer
        // and peak — both frozen at the pre-switch state.
        this.recording = false;
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
        // Same fix as DisposeAudioRecordingSource: clear the playingBack flag
        // so UpdateSourceStates re-creates the output device after a mid-
        // session switch instead of skipping the re-init because it still
        // thinks playback is running.
        this.playingBack = false;
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

        // Pipeline order (chosen for the "boost + cuts out" symptom):
        //
        //   1. RNNoise denoise — operates on the device's natural signal
        //      level, which is what it was trained on. Boosting first
        //      caused RNNoise to over-attenuate (mistaking boosted room
        //      noise for loud noise to suppress), which then made VAD
        //      decide the result wasn't speech and zero the frame.
        //   2. InputBoost gain — brings the cleaned signal up to a real
        //      transmission level *after* denoising. This is the gain that
        //      both the user hears (loopback) and peers hear (wire).
        //   3. VAD gate — sees the boosted, clean signal and reliably
        //      classifies it as speech, so quiet voices stop being false-
        //      negatived into silence.
        //   4. Peak/clip detection — on the final post-gate frame, so the
        //      UI meter shows what's actually leaving the plugin. If the
        //      VAD is over-gating, the meter visibly drops to zero — a
        //      direct diagnostic the user can act on.

        if (this.configuration.SuppressNoise && this.denoiser != null)
        {
            Convert16BitToFloat(e.Buffer, this.denoiserFloatSamples);
            // With incomplete audio data, this method can crash Dalamud
            this.denoiser.Denoise(this.denoiserFloatSamples);
            ConvertFloatTo16Bit(this.denoiserFloatSamples, e.Buffer);
        }

        var inputBoost = this.configuration.InputBoost;
        if (inputBoost != 1.0f)
        {
            ApplyGainInt16(e.Buffer, e.BytesRecorded, inputBoost);
        }

        if (this.configuration.SuppressNoise && this.denoiser != null)
        {
            // Enhanced gate: VAD on the cleaned + boosted frame. Frames the
            // VAD doesn't classify as speech get silenced — Opus DTX then
            // encodes them as ~2-byte comfort-noise packets, so the wire
            // cost is negligible. A SpeechHangoverFrames-long tail of
            // pass-through after the last detected-speech frame avoids
            // clipping word endings.
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

        // Final-frame peak: what the user is actually transmitting. Updated
        // here (not earlier) so the meter reflects post-everything output
        // — boost is visible AND a too-aggressive VAD shows up as the bar
        // dropping to zero during what feels like normal speech.
        var finalPeak = ComputePeakInt16(e.Buffer, e.BytesRecorded);
        this.micInputPeakPostBoost = finalPeak;
        if (finalPeak >= 0.99f)
        {
            this.lastClipTickMs = Environment.TickCount64;
        }

        if (this.audioPlaybackSource != null && this.PlayingBackMicAudio)
        {
            this.micPlaybackWaveProvider.AddSamples(e.Buffer, 0, e.BytesRecorded);
            this.micPlaybackVolumeProvider.LeftVolume = this.micPlaybackVolumeProvider.RightVolume = this.configuration.MasterVolume;
        }
        this.lastAudioRecordingSourceData = e;

        // Gate the wire emission (NOT the loopback above) on mute. Keeping
        // the capture device + denoiser running through a mute means the
        // first syllable after unmute is preserved; the user just stops
        // being transmitted to the room for the duration of the mute. The
        // MuteMic getter already folds in Deafen, so this covers both.
        if (this.MuteMic) return;

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
                var src = GetAudioRecordingSource(true);
                if (src != null)
                {
                    this.logger.Debug("Starting audio recording source from device '{0}'", src.DeviceFriendlyName);
                    lock (this.recordingLock)
                    {
                        src.StartRecording();
                    }
                    // We need a new denoiser here as the previous denoiser may have remaining incomplete audio data
                    // that can cause audio popping the next time it is used.
                    this.denoiser?.Dispose(); this.denoiser = null;
                    this.denoiser = new();
                    this.speechHangoverFramesRemaining = 0;
                    this.recording = true;
                }
            }
        }
        else
        {
            var recordingSource = GetAudioRecordingSource(false);
            if (recordingSource != null)
            {
                this.logger.Debug("Stopping audio recording source from device '{0}'", recordingSource.DeviceFriendlyName);
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

    /// <summary>
    /// In-place gain stage on little-endian 16-bit PCM. Each sample is
    /// multiplied by <paramref name="gain"/> and clamped to the int16 range
    /// so over-boosted signals saturate rather than wrap (which would produce
    /// horrible aliasing). Gain == 1.0 is a no-op and the caller already
    /// short-circuits that case.
    /// </summary>
    private static void ApplyGainInt16(byte[] pcm, int byteCount, float gain)
    {
        var end = byteCount - 1;
        for (int i = 0; i < end; i += 2)
        {
            short s = (short)(pcm[i] | (pcm[i + 1] << 8));
            int boosted = (int)(s * gain);
            if (boosted > short.MaxValue) boosted = short.MaxValue;
            else if (boosted < short.MinValue) boosted = short.MinValue;
            pcm[i] = (byte)(boosted & 0xff);
            pcm[i + 1] = (byte)((boosted >> 8) & 0xff);
        }
    }

    /// <summary>
    /// Maximum absolute sample magnitude across the buffer, normalized to
    /// [0, 1]. Used for both the live mic-level meter and clip detection
    /// (peak &gt;= 0.99 triggers the red-flash on the meter).
    /// </summary>
    private static float ComputePeakInt16(byte[] pcm, int byteCount)
    {
        int peak = 0;
        var end = byteCount - 1;
        for (int i = 0; i < end; i += 2)
        {
            short s = (short)(pcm[i] | (pcm[i + 1] << 8));
            int abs = s < 0 ? -s : s;
            if (abs > peak) peak = abs;
        }
        return peak / 32768f;
    }
}
