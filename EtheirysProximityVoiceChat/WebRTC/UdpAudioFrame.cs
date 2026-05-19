/*
 * Copyright (c) 2026 Noah Dolph
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * See the GNU Affero General Public License for more details.
 */
using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EtheirysProximityVoiceChat.WebRTC;

/// <summary>
/// Wire-format encode/decode for v3 UDP audio packets.
///
/// Format (29 bytes fixed overhead + N bytes ciphertext + 16 bytes tag):
/// <code>
///   offset  size  field
///   0       1     version       (0x03 for this revision)
///   1       8     session_id    server-issued, opaque
///   9       4     seq           big-endian uint32; monotonic per sender
///   13      N     ciphertext    AES-128-GCM-encrypted Opus packet (or empty for hello / keepalive)
///   13+N    16    auth_tag      AES-128-GCM 128-bit auth tag
/// </code>
///
/// Crypto:
///   • Cipher: AES-128-GCM.
///   • Per-session key: 16 bytes, issued by the server during the WSS handshake.
///   • Per-packet nonce (12 bytes), derived deterministically so it doesn't
///     need to ride on the wire:
///       <c>nonce = session_id[0..7] ++ seq_be32</c>
///     The (session_id, seq) pair is unique per packet, which guarantees
///     nonce uniqueness for the session's lifetime (2^32 frames at 50 Hz
///     = ~22 hours, far longer than any plausible session).
///   • AAD (authenticated, not encrypted): the 13-byte plaintext header.
///     Protects against header tampering (e.g. an on-path attacker rewriting
///     <c>seq</c> to forge frame ordering).
///
/// All helpers here are static and intentionally allocation-light on the
/// hot path. Buffers are caller-owned; we never retain a reference.
/// </summary>
internal static class UdpAudioFrame
{
    public const byte Version = 0x03;
    public const int HeaderBytes = 13;            // version + session_id + seq
    public const int TagBytes = 16;               // AES-GCM 128-bit tag
    public const int FixedOverheadBytes = HeaderBytes + TagBytes; // 29
    public const int SessionIdBytes = 8;
    public const int NonceBytes = 12;

    /// <summary>
    /// Encode one outbound packet into <paramref name="destination"/> using
    /// the supplied session key. Returns the number of bytes written.
    /// The destination buffer must have room for
    /// <see cref="FixedOverheadBytes"/> + <paramref name="payload"/>.Length.
    /// </summary>
    /// <param name="sessionKey">16-byte AES-128 key issued by the server.</param>
    /// <param name="sessionId">8-byte opaque session identifier.</param>
    /// <param name="seq">Monotonic per-sender 32-bit sequence number.</param>
    /// <param name="payload">Plaintext (Opus packet) to encrypt. May be empty
    ///   for hello / keepalive frames.</param>
    /// <param name="destination">Output buffer. Must be at least
    ///   <c>FixedOverheadBytes + payload.Length</c> bytes long.</param>
    /// <returns>Number of bytes written to <paramref name="destination"/>.</returns>
    public static int Encode(
        ReadOnlySpan<byte> sessionKey,
        ReadOnlySpan<byte> sessionId,
        uint seq,
        ReadOnlySpan<byte> payload,
        Span<byte> destination)
    {
        if (sessionKey.Length != 16) throw new ArgumentException("sessionKey must be 16 bytes", nameof(sessionKey));
        if (sessionId.Length != SessionIdBytes) throw new ArgumentException("sessionId must be 8 bytes", nameof(sessionId));
        var totalLen = FixedOverheadBytes + payload.Length;
        if (destination.Length < totalLen) throw new ArgumentException("destination too small", nameof(destination));

        // Header (plaintext, also serves as AAD).
        destination[0] = Version;
        sessionId.CopyTo(destination.Slice(1, SessionIdBytes));
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(9, 4), seq);

        // Derive nonce deterministically from (sessionId, seq) — see class summary.
        Span<byte> nonce = stackalloc byte[NonceBytes];
        BuildNonce(sessionId, seq, nonce);

        // AES-GCM in one shot. AAD is the 13-byte header we just wrote.
        using var gcm = new AesGcm(sessionKey, TagBytes);
        gcm.Encrypt(
            nonce,
            payload,
            ciphertext: destination.Slice(HeaderBytes, payload.Length),
            tag: destination.Slice(HeaderBytes + payload.Length, TagBytes),
            associatedData: destination.Slice(0, HeaderBytes));

        return totalLen;
    }

    /// <summary>
    /// Decode one inbound packet. Verifies the AEAD tag and writes the
    /// decrypted payload (Opus packet) into <paramref name="payloadOut"/>.
    /// Returns the payload length, or -1 if the packet is malformed /
    /// authentication failed (bad MAC, truncated, wrong version, etc.).
    /// </summary>
    /// <param name="sessionKey">16-byte AES-128 key for the sender's session.
    ///   On the server this is the session key paired with the
    ///   <c>session_id</c> in the packet header.</param>
    /// <param name="packet">Full received UDP datagram.</param>
    /// <param name="sessionId">Receives the 8-byte session id parsed from
    ///   the header. Caller must allocate at least 8 bytes.</param>
    /// <param name="seq">Receives the 32-bit sequence number.</param>
    /// <param name="payloadOut">Receives the decrypted payload. Must be at
    ///   least <c>packet.Length - FixedOverheadBytes</c> bytes long.</param>
    /// <returns>Payload byte count, or -1 on any failure.</returns>
    public static int TryDecode(
        ReadOnlySpan<byte> sessionKey,
        ReadOnlySpan<byte> packet,
        Span<byte> sessionId,
        out uint seq,
        Span<byte> payloadOut)
    {
        seq = 0;
        if (sessionKey.Length != 16) return -1;
        if (packet.Length < FixedOverheadBytes) return -1;
        if (packet[0] != Version) return -1;
        if (sessionId.Length < SessionIdBytes) return -1;

        var payloadLen = packet.Length - FixedOverheadBytes;
        if (payloadOut.Length < payloadLen) return -1;

        packet.Slice(1, SessionIdBytes).CopyTo(sessionId);
        seq = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(9, 4));

        Span<byte> nonce = stackalloc byte[NonceBytes];
        BuildNonce(packet.Slice(1, SessionIdBytes), seq, nonce);

        try
        {
            using var gcm = new AesGcm(sessionKey, TagBytes);
            gcm.Decrypt(
                nonce,
                ciphertext: packet.Slice(HeaderBytes, payloadLen),
                tag: packet.Slice(HeaderBytes + payloadLen, TagBytes),
                plaintext: payloadOut.Slice(0, payloadLen),
                associatedData: packet.Slice(0, HeaderBytes));
        }
        catch (AuthenticationTagMismatchException)
        {
            // Bad MAC — could be a corrupt packet, a session-key mismatch
            // (stale credentials), or an injection attempt. Caller logs and
            // drops; we don't have enough context here to distinguish.
            return -1;
        }
        catch (CryptographicException)
        {
            return -1;
        }

        return payloadLen;
    }

    /// <summary>
    /// Read just the <c>session_id</c> from a packet header without
    /// decrypting. Useful on the server fast path: route by session_id,
    /// look up the right key, then call <see cref="TryDecode"/>.
    /// </summary>
    public static bool TryReadSessionId(ReadOnlySpan<byte> packet, Span<byte> sessionIdOut)
    {
        if (packet.Length < HeaderBytes) return false;
        if (packet[0] != Version) return false;
        if (sessionIdOut.Length < SessionIdBytes) return false;
        packet.Slice(1, SessionIdBytes).CopyTo(sessionIdOut);
        return true;
    }

    /// <summary>
    /// Build the 12-byte AES-GCM nonce from <c>(session_id[0..7], seq_be32)</c>.
    /// Constant-time and side-effect-free; suitable for the hot path.
    /// </summary>
    private static void BuildNonce(ReadOnlySpan<byte> sessionId, uint seq, Span<byte> nonceOut)
    {
        sessionId.Slice(0, SessionIdBytes).CopyTo(nonceOut.Slice(0, SessionIdBytes));
        BinaryPrimitives.WriteUInt32BigEndian(nonceOut.Slice(SessionIdBytes, 4), seq);
    }
}
