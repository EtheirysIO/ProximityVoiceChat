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
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EtheirysProximityVoiceChat.Log;

namespace EtheirysProximityVoiceChat.WebRTC;

/// <summary>
/// Server-issued UDP session credentials. Populated from the
/// <c>udpCredentials</c> Socket.IO event emitted right after a successful
/// <c>ready</c> handshake. The server is authoritative — the client trusts
/// these values directly.
/// </summary>
internal sealed class UdpCredentials
{
    public required byte[] SessionIdBytes { get; init; }   // 8 bytes
    public required byte[] SessionKey { get; init; }       // 16 bytes
    public required string UdpHost { get; init; }
    public required int UdpPort { get; init; }
    public int TtlSeconds { get; init; }

    /// <summary>Hex-encoded session id for logs / diagnostics.</summary>
    public string SessionIdHex => Convert.ToHexString(SessionIdBytes);
}

/// <summary>
/// UDP audio transport for the v3 protocol. Runs alongside
/// <see cref="SignalingChannel"/> — the WSS signaling stays in charge of
/// room state, presence, mute, latency probe, etc.; this class owns ONLY
/// the high-rate Opus audio firehose to and from the server's relay.
///
/// Lifecycle:
///   1. Caller receives <c>udpCredentials</c> over WSS and constructs this
///      class with them.
///   2. Caller awaits <see cref="StartAsync"/>. It opens the UDP socket,
///      sends a zero-payload "hello" datagram (also serves as NAT pinhole
///      punch), and waits up to <see cref="HelloTimeoutMs"/> for the
///      server to reply (any UDP packet from the server is enough — usually
///      the first relayed audio frame from a peer, or the server's
///      hello-ack which is just a zero-payload datagram).
///   3. If the hello completes, <see cref="Ready"/> flips to true and the
///      caller starts pushing audio frames via <see cref="SendAudioFrame"/>.
///   4. If <see cref="HelloTimeoutMs"/> elapses with no reply, the caller's
///      Task completes with <see cref="Ready"/> still false; <see cref="LastError"/>
///      carries a <see cref="TimeoutException"/>. The caller falls back to
///      Socket.IO audio for this session.
///   5. Inbound frames fire <see cref="OnAudioFrame"/>(senderPeerId, opusBytes)
///      on a background task. Subscribers are responsible for getting the
///      handler back onto the right thread.
///   6. <see cref="Stop"/> / <see cref="Dispose"/> close the socket and join
///      the receive loop.
/// </summary>
internal sealed class UdpAudioChannel : IDisposable
{
    /// <summary>How long to wait for any UDP packet from the server after sending the hello.</summary>
    public const int HelloTimeoutMs = 5_000;

    /// <summary>How often to send a zero-payload keepalive when no real audio has been emitted.</summary>
    public const int KeepaliveIntervalMs = 15_000;

    /// <summary>Maximum decrypted payload size we'll accept (Opus 20 ms @ 24 kbps ≈ 60 B; 2 KB matches server's MAX_FRAME_BYTES).</summary>
    public const int MaxDecryptedPayloadBytes = 2048;

    private readonly UdpCredentials creds;
    private readonly ILogger logger;
    private readonly EndPoint serverEndpoint;
    private readonly Socket socket;

    private readonly CancellationTokenSource lifecycleCts = new();
    private Task? receiveLoopTask;
    private Task? keepaliveTask;

    /// <summary>
    /// Outbound session-level packet counter, used purely for AEAD nonce
    /// uniqueness via <c>(session_id, seq)</c>. Increments on every outbound
    /// packet including hello and keepalive.
    /// </summary>
    private uint outboundSeq;

    /// <summary>
    /// Outbound application-level audio-frame counter, written into the
    /// encrypted payload of audio packets so receivers can detect gaps
    /// across the wire. Does NOT increment on hello / keepalive, so a long
    /// silent pause followed by a single audio frame doesn't show up as a
    /// false-positive gap on the listener side.
    /// </summary>
    private uint outboundAudioFrameSeq;

    /// <summary>Time of the most recent real audio emit; drives the keepalive heuristic.</summary>
    private long lastSendTickMs;

    /// <summary>Set on first valid UDP packet from the server (hello-ack or relayed audio).</summary>
    private volatile bool ready;

    /// <summary>Set while StartAsync is running but Ready hasn't flipped yet.</summary>
    private volatile bool connecting;

    private bool disposed;

    // Diagnostics — readable from any thread; updates are racy but informational only.
    private long packetsSent;
    private long packetsReceived;
    private long packetsDropped;

    public UdpAudioChannel(UdpCredentials credentials, ILogger logger)
    {
        this.creds = credentials ?? throw new ArgumentNullException(nameof(credentials));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (credentials.SessionIdBytes is null || credentials.SessionIdBytes.Length != UdpAudioFrame.SessionIdBytes)
            throw new ArgumentException("sessionId must be 8 bytes", nameof(credentials));
        if (credentials.SessionKey is null || credentials.SessionKey.Length != 16)
            throw new ArgumentException("sessionKey must be 16 bytes", nameof(credentials));

        // Resolve the server endpoint up front. For now: synchronous DNS so
        // a bad host fails fast at construction rather than on first send.
        // The voice server is normally an A record; the synchronous resolve
        // is ~ms.
        IPAddress? addr = null;
        try
        {
            if (IPAddress.TryParse(credentials.UdpHost, out var parsed))
            {
                addr = parsed;
            }
            else
            {
                var entries = Dns.GetHostAddresses(credentials.UdpHost);
                foreach (var e in entries)
                {
                    if (e.AddressFamily == AddressFamily.InterNetwork) { addr = e; break; }
                }
                if (addr == null && entries.Length > 0) addr = entries[0];
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to resolve UDP host '{credentials.UdpHost}': {ex.Message}", ex);
        }
        if (addr == null) throw new InvalidOperationException($"No address records for UDP host '{credentials.UdpHost}'");

        this.serverEndpoint = new IPEndPoint(addr, credentials.UdpPort);

        this.socket = new Socket(addr.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
        {
            // Don't queue more than one packet's worth in the kernel send buffer.
            // For real-time voice we want failed sends to be visible immediately,
            // not stalled behind backlogged packets.
            Blocking = false,
        };
    }

    /// <summary>True once a valid UDP packet has been received from the server.</summary>
    public bool Ready => this.ready;

    /// <summary>True while the hello handshake is in flight (after StartAsync, before Ready or timeout).</summary>
    public bool Connecting => this.connecting;

    /// <summary>Most recent fatal-ish error; cleared on a successful hello-ack.</summary>
    public Exception? LastError { get; private set; }

    public long PacketsSent => System.Threading.Interlocked.Read(ref this.packetsSent);
    public long PacketsReceived => System.Threading.Interlocked.Read(ref this.packetsReceived);
    public long PacketsDropped => System.Threading.Interlocked.Read(ref this.packetsDropped);

    /// <summary>
    /// Fires when an inbound audio frame from a peer has been decrypted.
    /// Args: <c>(senderPeerId, senderSeq, opusPayloadBytes)</c>.
    /// <c>senderSeq</c> is the sender's application-level monotonic seq
    /// (per-sender, not per-receiver-session) and is used downstream to
    /// detect packet loss and enable Opus FEC recovery. The bytes buffer
    /// is freshly allocated per frame; subscribers may retain it without
    /// copying.
    /// </summary>
    public event Action<string, uint, byte[]>? OnAudioFrame;

    /// <summary>
    /// Open the socket, send the hello, and wait for the server's first
    /// response or <see cref="HelloTimeoutMs"/>, whichever comes first.
    /// Returns once <see cref="Ready"/> is true or the timeout has expired.
    /// </summary>
    public async Task StartAsync()
    {
        if (this.disposed) throw new ObjectDisposedException(nameof(UdpAudioChannel));
        this.connecting = true;
        try
        {
            // Bind to an ephemeral local port. We don't need a specific one;
            // the server records whatever source address our hello arrives from.
            this.socket.Bind(new IPEndPoint(IPAddress.Any, 0));

            // Start the receive loop BEFORE sending the hello so the
            // server's hello-ack (which can arrive in microseconds on LAN)
            // never lands while we're still hooking up the receiver.
            this.receiveLoopTask = Task.Run(() => ReceiveLoopAsync(this.lifecycleCts.Token));

            // Send the hello: zero-payload AEAD frame at seq=0. The server
            // uses this both to verify the session key AND to learn our
            // public source IP:port (NAT pinhole). Hello MUST use seq=0
            // explicitly — the auto-incrementing send path starts at seq=1,
            // and a nonce collision between hello and the first audio frame
            // would be catastrophic for AES-GCM.
            SendInternal(ReadOnlySpan<byte>.Empty, seq: 0);

            // Wait for Ready (set by the receive loop on first valid reply)
            // or the timeout.
            var deadline = Environment.TickCount64 + HelloTimeoutMs;
            while (!this.ready && Environment.TickCount64 < deadline && !this.lifecycleCts.IsCancellationRequested)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }

            if (!this.ready)
            {
                this.LastError = new TimeoutException(
                    $"UDP hello-ack timeout after {HelloTimeoutMs} ms (sessionId={this.creds.SessionIdHex}).");
                this.logger.Info("UDP audio: hello timed out for sessionId={0}; caller should fall back to TCP.",
                    this.creds.SessionIdHex);
                // Leave the socket open so the caller can inspect Ready/LastError;
                // it'll be torn down via Stop()/Dispose() shortly.
                return;
            }

            this.logger.Info("UDP audio: session {0} ready ({1}:{2})",
                this.creds.SessionIdHex, this.creds.UdpHost, this.creds.UdpPort);

            // Start the keepalive once we know UDP is working both ways.
            this.keepaliveTask = Task.Run(() => KeepaliveLoopAsync(this.lifecycleCts.Token));
        }
        catch (Exception ex)
        {
            this.LastError = ex;
            this.logger.Error("UDP audio StartAsync failed: {0}", ex);
        }
        finally
        {
            this.connecting = false;
        }
    }

    /// <summary>
    /// Encrypt and send one Opus audio frame. Safe to call before
    /// <see cref="Ready"/> is true — the packet will leave the wire but
    /// the server will drop it until the session is hello-established. In
    /// practice callers should gate on <see cref="Ready"/> to avoid wasting
    /// uplink during fallback.
    ///
    /// Wire payload for audio packets:
    /// <c>[4 bytes audioFrameSeq BE32][N bytes Opus]</c>. The server reads
    /// the audioFrameSeq, prepends it (with the sender's peerId) into the
    /// receiver-side payload, and re-encrypts for each listener.
    /// </summary>
    public void SendAudioFrame(byte[] opusPacket)
    {
        if (this.disposed) return;
        if (opusPacket == null || opusPacket.Length == 0) return;

        var audioSeq = System.Threading.Interlocked.Increment(ref this.outboundAudioFrameSeq);
        var payload = ArrayPool<byte>.Shared.Rent(4 + opusPacket.Length);
        try
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), audioSeq);
            Buffer.BlockCopy(opusPacket, 0, payload, 4, opusPacket.Length);
            SendInternal(new ReadOnlySpan<byte>(payload, 0, 4 + opusPacket.Length));
            this.lastSendTickMs = Environment.TickCount64;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }
    }

    private void SendInternal(ReadOnlySpan<byte> payload)
    {
        var seq = System.Threading.Interlocked.Increment(ref this.outboundSeq);
        // outboundSeq starts at 0 and we Increment first → first real packet seq=1.
        // We deliberately want seq=0 for the hello, so the hello is sent via
        // a separate code path that bypasses Interlocked.Increment.
        SendInternal(payload, seq);
    }

    private void SendInternal(ReadOnlySpan<byte> payload, uint seq)
    {
        var totalLen = UdpAudioFrame.FixedOverheadBytes + payload.Length;
        var buf = ArrayPool<byte>.Shared.Rent(totalLen);
        try
        {
            UdpAudioFrame.Encode(
                this.creds.SessionKey,
                this.creds.SessionIdBytes,
                seq,
                payload,
                buf.AsSpan(0, totalLen));

            int written;
            try
            {
                written = this.socket.SendTo(buf, 0, totalLen, SocketFlags.None, this.serverEndpoint);
            }
            catch (SocketException ex) when (
                ex.SocketErrorCode == SocketError.WouldBlock ||
                ex.SocketErrorCode == SocketError.NoBufferSpaceAvailable)
            {
                // Kernel send buffer full / nonblocking would-block. Drop
                // the frame rather than queue — same drop-vs-queue trade-off
                // the server's volatile.emit uses for the same reason.
                System.Threading.Interlocked.Increment(ref this.packetsDropped);
                return;
            }
            catch (Exception ex)
            {
                System.Threading.Interlocked.Increment(ref this.packetsDropped);
                this.logger.Debug("UDP send failed (seq={0}): {1}", seq, ex.Message);
                return;
            }

            if (written == totalLen)
            {
                System.Threading.Interlocked.Increment(ref this.packetsSent);
            }
            else
            {
                System.Threading.Interlocked.Increment(ref this.packetsDropped);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var localBuf = new byte[2 + UdpAudioFrame.FixedOverheadBytes + MaxDecryptedPayloadBytes];
        var sessionIdBuf = new byte[UdpAudioFrame.SessionIdBytes];

        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult res;
            try
            {
                res = await this.socket.ReceiveFromAsync(
                    localBuf,
                    SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex)
            {
                // ICMP unreachable etc. — log and keep looping; the socket
                // is still usable for subsequent packets in most cases.
                this.logger.Debug("UDP receive: {0}", ex.Message);
                continue;
            }

            // Only accept packets from the server we sent the hello to.
            // Defends against off-path noise + reflects the "server is the
            // only legitimate peer" pinning.
            if (!res.RemoteEndPoint.Equals(this.serverEndpoint))
            {
                System.Threading.Interlocked.Increment(ref this.packetsDropped);
                continue;
            }

            if (res.ReceivedBytes < UdpAudioFrame.FixedOverheadBytes)
            {
                System.Threading.Interlocked.Increment(ref this.packetsDropped);
                continue;
            }

            // Parse + decrypt.
            var packetSpan = localBuf.AsSpan(0, res.ReceivedBytes);

            // The server may relay either an audio frame OR a control packet.
            // For now there's exactly one kind of control packet: a zero-
            // payload "hello-ack" the server sends so we know the round-trip
            // is alive. The discriminator is just "payload length == 0".
            var payloadLen = res.ReceivedBytes - UdpAudioFrame.FixedOverheadBytes;
            var payloadOut = payloadLen > 0 ? new byte[payloadLen] : Array.Empty<byte>();
            var decoded = UdpAudioFrame.TryDecode(
                this.creds.SessionKey,
                packetSpan,
                sessionIdBuf,
                out _,
                payloadOut);
            if (decoded < 0)
            {
                System.Threading.Interlocked.Increment(ref this.packetsDropped);
                continue;
            }

            System.Threading.Interlocked.Increment(ref this.packetsReceived);

            // First valid packet flips us to Ready.
            if (!this.ready)
            {
                this.ready = true;
                this.LastError = null;
            }

            if (decoded == 0)
            {
                // Hello-ack or keepalive from the server. No fanout needed.
                continue;
            }

            // Audio frame: payload framing is
            //   [1 byte: peerIdLen]
            //   [peerIdLen bytes: senderPeerId UTF-8]
            //   [4 bytes: senderSeq BE32]    ← per-sender app seq, NOT the
            //                                    header's per-receiver session seq.
            //                                    Used downstream for FEC gap-recovery.
            //   [N bytes: Opus packet]
            // The server is responsible for prepending the peerId + sender
            // seq before re-encrypting for each receiver — see udp-relay.js.
            if (!TrySplitSenderPrefixedFrame(payloadOut, out var senderPeerId, out var senderSeq, out var opus))
            {
                System.Threading.Interlocked.Increment(ref this.packetsDropped);
                continue;
            }

            try
            {
                this.OnAudioFrame?.Invoke(senderPeerId, senderSeq, opus);
            }
            catch (Exception ex)
            {
                this.logger.Debug("OnAudioFrame subscriber threw: {0}", ex.Message);
            }
        }
    }

    /// <summary>
    /// Split a server-relayed audio payload into
    /// <c>(senderPeerId, senderSeq, opusBytes)</c>. Wire shape is
    /// <c>[1 byte: peerIdLen] [peerIdLen bytes: UTF-8] [4 bytes: senderSeq BE32] [N bytes: opus]</c>.
    /// </summary>
    private static bool TrySplitSenderPrefixedFrame(ReadOnlySpan<byte> payload, out string senderPeerId, out uint senderSeq, out byte[] opus)
    {
        senderPeerId = string.Empty;
        senderSeq = 0;
        opus = Array.Empty<byte>();
        if (payload.Length < 1) return false;
        int idLen = payload[0];
        if (idLen <= 0 || idLen > 64) return false;            // matches server MAX_PEERID_BYTES
        var headerLen = 1 + idLen + 4;
        if (payload.Length < headerLen) return false;
        senderPeerId = Encoding.UTF8.GetString(payload.Slice(1, idLen));
        senderSeq = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(1 + idLen, 4));
        opus = payload.Slice(headerLen).ToArray();
        return true;
    }

    private async Task KeepaliveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(KeepaliveIntervalMs, ct).ConfigureAwait(false);
                var sinceLastSend = Environment.TickCount64 - this.lastSendTickMs;
                if (sinceLastSend < KeepaliveIntervalMs - 500) continue;
                // Empty payload keepalive. Reuses the regular send path so
                // it's encrypted + sequence-numbered like everything else.
                SendInternal(ReadOnlySpan<byte>.Empty);
            }
        }
        catch (OperationCanceledException) { /* lifecycle cancellation, expected */ }
        catch (Exception ex)
        {
            this.logger.Debug("UDP keepalive loop ended: {0}", ex.Message);
        }
    }

    public void Stop()
    {
        if (this.disposed) return;
        try { this.lifecycleCts.Cancel(); } catch { /* nothing to do */ }
        try { this.socket.Shutdown(SocketShutdown.Both); } catch { /* nothing to do */ }
        try { this.socket.Close(); } catch { /* nothing to do */ }
    }

    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;
        Stop();
        try { this.socket.Dispose(); } catch { /* nothing to do */ }
        try { this.lifecycleCts.Dispose(); } catch { /* nothing to do */ }
    }
}
