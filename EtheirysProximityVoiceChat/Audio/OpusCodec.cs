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
using System.Collections.Concurrent;
using Concentus;
using Concentus.Enums;
using EtheirysProximityVoiceChat.Log;

namespace EtheirysProximityVoiceChat.Audio;

/// <summary>
/// Wraps Concentus Opus encoder + per-peer decoders.
///
/// Format must match what <see cref="AudioDeviceController"/> captures and plays back:
/// 48 kHz mono, 16-bit signed PCM, 20 ms frames (960 samples per frame).
///
/// Per-peer decoders are required because Opus is a stateful codec — feeding a single decoder
/// with interleaved frames from multiple senders produces scrambled audio. Each decoder is
/// also single-threaded internally, so we guard each one with its own lock to defend against
/// Socket.IO dispatching back-to-back frames from the same peer on different worker threads.
/// </summary>
public sealed class OpusCodec : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 1;
    public const int FrameSizeMs = 20;
    public const int SamplesPerFrame = SampleRate * FrameSizeMs / 1000; // 960
    public const int BytesPerFrame = SamplesPerFrame * sizeof(short);   // 1920

    // Max size of a single Opus packet at our settings; 1275 is the Opus spec limit per frame.
    // 4000 gives ample headroom and matches the upstream reference.
    private const int MaxPacketBytes = 4000;

    private sealed class DecoderEntry
    {
        public required IOpusDecoder Decoder { get; init; }
        public readonly object Lock = new();
    }

    private readonly IOpusEncoder encoder;
    private readonly ConcurrentDictionary<string, DecoderEntry> decoders = new();
    private readonly ILogger logger;
    private readonly byte[] encodeScratch = new byte[MaxPacketBytes];
    private readonly object encodeLock = new();
    private bool disposed;

    /// <summary>
    /// Create a codec session.
    /// </summary>
    /// <param name="bitrateBps">Target encoder bitrate. 24 kbps is the bare minimum for
    /// intelligible voice; 32 kbps is the sweet spot for clear conversational audio; 48 kbps
    /// approaches transparency. We default to 32 — bandwidth is still trivial vs the v1 PCM mesh.</param>
    /// <param name="packetLossPercent">Hint to the encoder about expected wire packet loss
    /// (0–100). Drives how much FEC overhead it budgets. 5 is a reasonable default for the
    /// open internet without being wasteful.</param>
    public OpusCodec(ILogger logger, int bitrateBps = 32000, int packetLossPercent = 5)
    {
        this.logger = logger;
        this.encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
        this.encoder.Bitrate = bitrateBps;
        this.encoder.UseInbandFEC = true;           // generate redundant coded data for resilience
        this.encoder.PacketLossPercent = Math.Clamp(packetLossPercent, 0, 100);
        this.encoder.UseDTX = true;                 // silence suppression on the wire
        this.encoder.Complexity = 10;               // max quality; voice frames are tiny so CPU cost is irrelevant
    }

    /// <summary>
    /// Encode one 20 ms frame of 16-bit PCM. Input length must equal <see cref="BytesPerFrame"/>.
    /// Returned array is sized exactly to the Opus packet length.
    /// </summary>
    public byte[]? Encode(byte[] pcmBytes, int byteCount)
    {
        if (this.disposed) return null;
        if (byteCount != BytesPerFrame)
        {
            // Capture buffer didn't deliver a full 20 ms frame — skip rather than corrupt encoder state.
            return null;
        }

        // Convert byte[] PCM → short[] samples. NAudio gives us little-endian 16-bit.
        Span<short> samples = stackalloc short[SamplesPerFrame];
        for (int i = 0; i < SamplesPerFrame; i++)
        {
            samples[i] = (short)(pcmBytes[i * 2] | (pcmBytes[i * 2 + 1] << 8));
        }

        try
        {
            int written;
            lock (this.encodeLock)
            {
                written = this.encoder.Encode(samples, SamplesPerFrame, this.encodeScratch.AsSpan(), MaxPacketBytes);
            }
            // Per Opus spec: when DTX is on and the encoder decides silence, the return
            // value is 1 byte (or 0). That byte is a "drop this packet, do PLC on the
            // receiver" indicator — it MUST NOT be transmitted as if it were audio.
            // Real Opus packets at our settings start at ~3 bytes; anything smaller is
            // a DTX indicator that the receiver's decoder would reject as malformed.
            if (written < 3) return null;
            var packet = new byte[written];
            Buffer.BlockCopy(this.encodeScratch, 0, packet, 0, written);
            return packet;
        }
        catch (Exception ex)
        {
            this.logger.Error("Opus encode failed: {0}", ex);
            return null;
        }
    }

    /// <summary>
    /// Decode one Opus packet from the given peer into a 16-bit PCM byte buffer
    /// of exactly <see cref="BytesPerFrame"/> bytes. Returns null on failure.
    /// </summary>
    public byte[]? Decode(string peerId, byte[] opusPacket)
    {
        if (this.disposed) return null;
        // Defensive: a peer running an older build may be transmitting Opus DTX
        // indicator packets (1 byte) over the wire instead of dropping them.
        // Those packets are NOT decodable as a 20 ms audio frame and would
        // throw ArgumentOutOfRange inside Concentus. Drop them quietly.
        if (opusPacket == null || opusPacket.Length < 3) return null;

        var entry = this.decoders.GetOrAdd(peerId, _ => new DecoderEntry
        {
            Decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels),
        });

        Span<short> samples = stackalloc short[SamplesPerFrame];
        int decoded;
        try
        {
            // Per-peer lock: Concentus decoders are not thread-safe. Two concurrent Decode calls
            // on the same decoder corrupt its internal state and produce static / scrambled audio.
            lock (entry.Lock)
            {
                decoded = entry.Decoder.Decode(opusPacket.AsSpan(), samples, SamplesPerFrame, decode_fec: false);
            }
            if (decoded != SamplesPerFrame)
            {
                // Mismatched frame size — packet is malformed for our config.
                return null;
            }
        }
        catch (Exception ex)
        {
            // Logged at Debug level intentionally: peers on mismatched builds can produce
            // a constant stream of these (one per frame ≈ 50/sec), which would drown /xllog.
            this.logger.Debug("Opus decode failed for peer {0}: {1}", peerId, ex.Message);
            return null;
        }

        var pcm = new byte[BytesPerFrame];
        for (int i = 0; i < SamplesPerFrame; i++)
        {
            short s = samples[i];
            pcm[i * 2] = (byte)(s & 0xff);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xff);
        }
        return pcm;
    }

    /// <summary>
    /// Drop the decoder state for a peer that has left.
    /// </summary>
    public void RemovePeer(string peerId)
    {
        this.decoders.TryRemove(peerId, out _);
    }

    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;
        this.decoders.Clear();
        // Concentus encoders/decoders don't expose IDisposable in the public interfaces.
        // Just drop references and let the GC reclaim them.
    }
}
