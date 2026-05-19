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
using NAudio.CoreAudioApi;
using NAudio.Wave;
using EtheirysProximityVoiceChat.Log;

namespace EtheirysProximityVoiceChat.Audio;

/// <summary>
/// Mic capture built on WASAPI shared mode (the same path Discord / Teams
/// use). Replaces the legacy WinMM-based <c>WaveInEvent</c> wrapper, which
/// had no format-negotiation, fragile integer device indices, and silently
/// swallowed device-open failures — all of which combined to make the plugin
/// unusable for users whose mics didn't natively run at 48 kHz / 16-bit / mono.
///
/// Shape is intentionally close to NAudio's <c>WaveInEvent</c>:
///   • <see cref="DataAvailable"/> fires with a <see cref="WaveInEventArgs"/>
///     containing exactly <see cref="BytesPerFrame"/> bytes of 48 kHz mono
///     16-bit PCM (= 20 ms / 1920 bytes), regardless of what size WASAPI
///     hands us. A ring buffer guarantees frame alignment.
///   • <see cref="RecordingStopped"/> fires when WASAPI capture stops, with
///     an exception if the stop was caused by an error (e.g. device unplugged
///     mid-session, exclusive-mode contention).
///   • <see cref="CurrentPeak"/> exposes the most recent frame's peak sample
///     magnitude in the [0, 1] range, suitable for a live UI mic-level meter.
///
/// Format strategy: request 48 kHz / 16-bit / mono from WASAPI shared mode.
/// In shared mode the Windows audio engine does any resampling /
/// channel-mixing transparently between the device's native format and the
/// app's requested format, so this works for the overwhelming majority of
/// devices. If the device truly cannot deliver our format (extremely rare
/// in shared mode), capture fails with an exception that is surfaced via
/// <see cref="RecordingStopped"/> — <see cref="AudioDeviceController"/>
/// turns that into a red error banner in the UI with a Retry button, which
/// is far better than the old WinMM "silent failure → user has no idea
/// what's wrong" behaviour.
/// </summary>
public sealed class WasapiAudioRecorder : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;
    public const int FrameMilliseconds = 20;

    // 48000 Hz × 2 bytes/sample × 1 ch × 20 ms / 1000 = 1920 bytes per frame.
    public const int BytesPerFrame = SampleRate * (BitsPerSample / 8) * Channels * FrameMilliseconds / 1000;

    /// <summary>The fixed format every <see cref="DataAvailable"/> frame is delivered in.</summary>
    public WaveFormat WaveFormat { get; } = new(SampleRate, BitsPerSample, Channels);

    /// <summary>
    /// Friendly device name resolved at construction. Useful for error
    /// reporting ("Microphone (Arctis Nova 7) failed to start") and for
    /// confirming via /xllog that the user's intended device is in use.
    /// </summary>
    public string DeviceFriendlyName { get; } = "Default";

    /// <summary>
    /// Peak sample magnitude (0..1) of the most recently captured frame.
    /// Updated each time <see cref="DataAvailable"/> fires. The UI mic-level
    /// meter reads this on the framework thread; volatile semantics aren't
    /// needed because a one-frame lag is invisible at 50 Hz.
    /// </summary>
    public float CurrentPeak { get; private set; }

    /// <summary>Fires when a full 20 ms / 1920-byte frame is ready.</summary>
    public event EventHandler<WaveInEventArgs>? DataAvailable;

    /// <summary>
    /// Fires when capture stops. If <see cref="StoppedEventArgs.Exception"/>
    /// is non-null, the stop was involuntary (device error / unplug). The
    /// controller surfaces this to the UI as a red banner with a Retry
    /// button so users actually find out why their mic went silent.
    /// </summary>
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    private readonly ILogger logger;
    private readonly WasapiCapture capture;
    private readonly object captureLock = new();

    /// <summary>Accumulator for partial frames between callbacks.</summary>
    private readonly byte[] frameBuffer = new byte[BytesPerFrame];

    /// <summary>How many bytes of the current partial frame we've accumulated.</summary>
    private int frameBufferFill;

    private bool started;
    private bool disposed;

    /// <summary>
    /// Builds a recorder bound to a specific WASAPI capture endpoint, or to
    /// the system-default capture device when <paramref name="wasapiDeviceId"/>
    /// is null or empty.
    /// </summary>
    public WasapiAudioRecorder(string? wasapiDeviceId, ILogger logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

        MMDevice device;
        using (var enumerator = new MMDeviceEnumerator())
        {
            if (string.IsNullOrEmpty(wasapiDeviceId))
            {
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            }
            else
            {
                try
                {
                    device = enumerator.GetDevice(wasapiDeviceId);
                }
                catch (Exception ex)
                {
                    // Saved device is gone (e.g. user unplugged it). Fall
                    // back to default rather than throwing, so the plugin
                    // still has a working mic.
                    this.logger.Warn("WASAPI device id '{0}' not found ({1}); falling back to default capture device.",
                        wasapiDeviceId, ex.Message);
                    device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                }
            }
        }

        try { this.DeviceFriendlyName = device.FriendlyName; }
        catch { this.DeviceFriendlyName = "Unknown device"; }

        // WASAPI shared mode + our preferred format. The Windows audio
        // engine handles any resampling / channel-mixing between this and
        // the device's native format transparently, which is the whole
        // reason we moved off WinMM.
        this.capture = new WasapiCapture(device)
        {
            ShareMode = AudioClientShareMode.Shared,
            WaveFormat = this.WaveFormat,
        };
        this.capture.DataAvailable += OnCaptureDataAvailable;
        this.capture.RecordingStopped += OnCaptureRecordingStopped;
    }

    public void StartRecording()
    {
        lock (this.captureLock)
        {
            if (this.disposed) throw new ObjectDisposedException(nameof(WasapiAudioRecorder));
            if (this.started) return;
            this.frameBufferFill = 0;
            this.CurrentPeak = 0f;
            this.capture.StartRecording();
            this.started = true;
            this.logger.Info(
                "WasapiCapture started: device='{0}' format={1}Hz/{2}/{3}ch",
                this.DeviceFriendlyName,
                this.WaveFormat.SampleRate,
                this.WaveFormat.BitsPerSample,
                this.WaveFormat.Channels);
        }
    }

    public void StopRecording()
    {
        lock (this.captureLock)
        {
            if (!this.started) return;
            this.started = false;
            try { this.capture.StopRecording(); }
            catch (Exception ex) { this.logger.Debug("StopRecording threw: {0}", ex.Message); }
        }
    }

    public void Dispose()
    {
        lock (this.captureLock)
        {
            if (this.disposed) return;
            this.disposed = true;
            this.capture.DataAvailable -= OnCaptureDataAvailable;
            this.capture.RecordingStopped -= OnCaptureRecordingStopped;
            try { this.capture.Dispose(); }
            catch (Exception ex) { this.logger.Debug("WasapiCapture dispose threw: {0}", ex.Message); }
        }
    }

    private void OnCaptureDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (args.BytesRecorded <= 0) return;
        AccumulateAndEmit(args.Buffer, args.BytesRecorded);
    }

    /// <summary>
    /// Append the incoming bytes into <see cref="frameBuffer"/> and emit a
    /// <see cref="DataAvailable"/> event every time we have a full
    /// <see cref="BytesPerFrame"/> bytes ready. Anything past the last full
    /// frame stays buffered for the next callback. This is what guarantees
    /// the downstream RNNoise / VAD / Opus chain always sees exactly
    /// 1920-byte frames, regardless of the variable buffer sizes WASAPI
    /// may deliver (typically 10 ms, but it can vary).
    /// </summary>
    private void AccumulateAndEmit(byte[] source, int byteCount)
    {
        int offset = 0;
        while (offset < byteCount)
        {
            int need = BytesPerFrame - this.frameBufferFill;
            int take = Math.Min(need, byteCount - offset);
            Buffer.BlockCopy(source, offset, this.frameBuffer, this.frameBufferFill, take);
            this.frameBufferFill += take;
            offset += take;

            if (this.frameBufferFill == BytesPerFrame)
            {
                // Compute peak from the pristine capture before any
                // downstream subscriber (RNNoise, in particular) mutates
                // the buffer in-place — that way the UI meter shows real
                // mic activity, not post-denoise residue.
                this.CurrentPeak = ComputePeakInt16(this.frameBuffer);

                // Copy out: subscribers may hold onto the buffer past this
                // callback, and we want to reuse frameBuffer for the next
                // partial accumulate.
                var frame = new byte[BytesPerFrame];
                Buffer.BlockCopy(this.frameBuffer, 0, frame, 0, BytesPerFrame);
                this.frameBufferFill = 0;

                try
                {
                    this.DataAvailable?.Invoke(this, new WaveInEventArgs(frame, BytesPerFrame));
                }
                catch (Exception ex)
                {
                    this.logger.Error("DataAvailable subscriber threw: {0}", ex);
                }
            }
        }
    }

    private void OnCaptureRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (this.captureLock) { this.started = false; }
        if (e.Exception != null)
        {
            this.logger.Error("WasapiCapture stopped with exception on device '{0}': {1}",
                this.DeviceFriendlyName, e.Exception);
        }
        try { this.RecordingStopped?.Invoke(this, e); }
        catch (Exception ex) { this.logger.Debug("RecordingStopped subscriber threw: {0}", ex.Message); }
    }

    /// <summary>
    /// Returns the maximum absolute sample magnitude in <paramref name="pcm16"/>
    /// normalized to [0, 1]. Used to drive the UI mic-level meter.
    /// </summary>
    private static float ComputePeakInt16(byte[] pcm16)
    {
        int peak = 0;
        for (int i = 0; i + 1 < pcm16.Length; i += 2)
        {
            short s = (short)(pcm16[i] | (pcm16[i + 1] << 8));
            int abs = s < 0 ? -s : s;
            if (abs > peak) peak = abs;
        }
        return peak / 32768f;
    }

    /// <summary>
    /// Enumerate active WASAPI capture endpoints. Returns
    /// <c>(friendlyName, id)</c> tuples; callers prepend a "Default" entry
    /// themselves. Static so callers can build a device list without first
    /// constructing a recorder.
    /// </summary>
    public static System.Collections.Generic.IEnumerable<(string FriendlyName, string Id)> EnumerateCaptureEndpoints()
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            string name;
            string id;
            try { name = dev.FriendlyName; }
            catch { continue; }
            try { id = dev.ID; }
            catch { continue; }
            yield return (name, id);
        }
    }
}
