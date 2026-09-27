using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using ReadUs.Protocol;

namespace ReadUs.Connections;

/// <summary>
/// A single physical connection: one socket, one read loop, one write loop, running
/// the connection lifecycle FSM in docs/design/state-machines.md §1. This is a Tier 1
/// (multiplexed) connection in the project spec §4 sense — many concurrent callers may
/// call <see cref="SendAsync"/> on the same instance; replies are correlated to
/// requests purely by FIFO order (Redis guarantees in-order replies), via the
/// <see cref="_pending"/> queue.
///
/// Tier 2 (leased, dedicated) usage — blocking commands and transactions, per project
/// spec §4 — shares this same type: <see cref="SendBlockingAsync"/> reuses the identical
/// transport, read loop, and pending-queue machinery as <see cref="SendAsync"/>, per the
/// spec's explicit direction that tiering is a pooling/lifetime policy layered on common
/// connection machinery, not a second protocol stack. What differs is only how
/// cancellation is handled (see docs/design/state-machines.md §2).
/// </summary>
public sealed class RedisConnection : IAsyncDisposable
{
    /// <summary>
    /// Bound on how long a Tier 2 cancellation waits, after a successful <c>CLIENT
    /// UNBLOCK</c>, for the corresponding frame to arrive on the primary connection
    /// before giving up and discarding the connection (docs/design/state-machines.md
    /// §2.5, the <c>Discarded</c> transition). Deferred decision: fixed for v0; should
    /// become configurable once Tier 2 pool options solidify (see design doc §5).
    /// </summary>
    private static readonly TimeSpan UnblockReconciliationGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How much of a rotating credential's remaining lifetime to burn through before
    /// proactively refreshing it (project spec §7). Deferred decision: fixed for v0;
    /// see design doc §5.
    /// </summary>
    private const double CredentialRefreshLifetimeFraction = 0.8;

    private static readonly TimeSpan MinCredentialRefreshDelay = TimeSpan.FromSeconds(1);

    private readonly ConcurrentQueue<PendingRequest> _pending = new();
    private readonly ConcurrentQueue<PendingRequest> _requestPool = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private Socket _socket = null!;
    private Stream _stream = null!;
    private PipeReader _reader = null!;
    private PipeWriter _writer = null!;
    private Task _readLoopTask = Task.CompletedTask;
    private IRedisCredentialsProvider? _credentialsProvider;

    private int _state = (int)ConnectionState.Created;

    private RedisConnection()
    {
    }

    internal ConnectionState State => (ConnectionState)Volatile.Read(ref _state);

    /// <summary>
    /// Captured via <c>CLIENT ID</c> during the handshake (docs/design/state-machines.md
    /// §2.2) so a Tier 2 cancellation can issue <c>CLIENT UNBLOCK &lt;id&gt;</c> without
    /// an extra round trip that would itself race the thing it's trying to interrupt.
    /// </summary>
    public long ClientId { get; private set; }

    public static async Task<RedisConnection> ConnectAsync(RedisConnectionOptions options, CancellationToken cancellationToken = default)
    {
        var connection = new RedisConnection();
        await connection.OpenAsync(options, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    public async ValueTask<RedisResult> SendAsync(ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken = default)
    {
        ThrowIfNotReady();

        var pending = RentPendingRequest();
        await WriteAndEnqueueAsync(pending, commandName, args, cancellationToken).ConfigureAwait(false);

        using var registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(
                static state => ((PendingRequest)state!).TryCompleteWithException(new OperationCanceledException()),
                pending)
            : default;

        try
        {
            return await new ValueTask<RedisResult>(pending, pending.Version).ConfigureAwait(false);
        }
        finally
        {
            ReturnPendingRequest(pending);
        }
    }

    /// <summary>
    /// Tier 2 (project spec §4): sends a <c>BLOCKING</c>-flagged command on a leased
    /// connection. Unlike <see cref="SendAsync"/>, cancellation here does not simply
    /// discard the eventual reply — it drives the <c>CLIENT UNBLOCK</c> reconciliation
    /// protocol in docs/design/state-machines.md §2.3–§2.6, because a leased connection
    /// must become reusable again (or be safely discarded) once the caller stops
    /// waiting, not just abandoned mid-block forever.
    /// </summary>
    internal async ValueTask<RedisResult> SendBlockingAsync(
        ReadOnlyMemory<byte> commandName,
        ReadOnlyMemory<byte>[] args,
        IControlChannel controlChannel,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNotReady();

        var pending = RentPendingRequest();
        await WriteAndEnqueueAsync(pending, commandName, args, cancellationToken).ConfigureAwait(false);

        using var registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(
                static state =>
                {
                    var (self, request, channel) = ((RedisConnection, PendingRequest, IControlChannel))state!;
                    _ = self.ReconcileCancellationAsync(request, channel);
                },
                (this, pending, controlChannel))
            : default;

        try
        {
            return await new ValueTask<RedisResult>(pending, pending.Version).ConfigureAwait(false);
        }
        finally
        {
            ReturnPendingRequest(pending);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await FaultAsync(new ObjectDisposedException(nameof(RedisConnection))).ConfigureAwait(false);
        await _readLoopTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Re-authenticates an already-open, already-<c>Ready</c> connection using a fresh
    /// credential from <see cref="RedisConnectionOptions.CredentialsProvider"/> —
    /// project spec §7's "live re-AUTH on already-open connections where the server
    /// supports it" path. Sent as an ordinary pipelined <c>AUTH</c> command through
    /// <see cref="SendAsync"/> (docs/design/state-machines.md §1.3: re-authentication
    /// is not a connection-state-machine transition, just another request in FIFO
    /// order). Throws if no provider was configured, or if the server rejects the new
    /// credential.
    /// </summary>
    public async ValueTask ReAuthenticateAsync(CancellationToken cancellationToken = default)
    {
        if (_credentialsProvider is null)
        {
            throw new InvalidOperationException("No credentials provider was configured for this connection.");
        }

        var credentials = await _credentialsProvider.GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        await ReAuthenticateAsync(credentials, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ReAuthenticateAsync(RedisCredentials credentials, CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte>[] args =
        [
            Encoding.UTF8.GetBytes(credentials.Username ?? "default"),
            Encoding.UTF8.GetBytes(credentials.Password),
        ];

        var reply = await SendAsync("AUTH"u8.ToArray(), args, cancellationToken).ConfigureAwait(false);
        if (reply.IsError)
        {
            throw new RedisConnectionException($"Re-authentication failed: {reply.AsString()}");
        }
    }

    /// <summary>
    /// Self-rescheduling proactive refresh loop, started once after a successful
    /// handshake with a provider that reported <see cref="RedisCredentials.ExpiresAt"/>.
    /// If a refresh ever fails, faults the connection rather than leaving it running on
    /// a credential the provider considers stale — whichever pool owns this connection
    /// will replace it, and the replacement authenticates fresh via the provider
    /// (project spec §7's "reconnect with new credentials" fallback falls out of the
    /// existing fault-then-replace machinery for free, rather than needing its own
    /// separate implementation).
    /// </summary>
    private async Task CredentialRefreshLoopAsync(DateTimeOffset expiresAt)
    {
        while (true)
        {
            var delay = ComputeCredentialRefreshDelay(expiresAt);
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            if (State != ConnectionState.Ready)
            {
                return;
            }

            RedisCredentials credentials;
            try
            {
                credentials = await _credentialsProvider!.GetCredentialsAsync(CancellationToken.None).ConfigureAwait(false);
                await ReAuthenticateAsync(credentials, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await FaultAsync(new RedisConnectionException("Failed to refresh rotating credentials.", ex)).ConfigureAwait(false);
                return;
            }

            if (credentials.ExpiresAt is not DateTimeOffset next)
            {
                // No further expiry info -- nothing left to schedule. The connection
                // stays on this credential until a caller explicitly re-authenticates.
                return;
            }

            expiresAt = next;
        }
    }

    private static TimeSpan ComputeCredentialRefreshDelay(DateTimeOffset expiresAt)
    {
        var remaining = expiresAt - DateTimeOffset.UtcNow;
        var refreshAfter = remaining * CredentialRefreshLifetimeFraction;
        return refreshAfter < MinCredentialRefreshDelay ? MinCredentialRefreshDelay : refreshAfter;
    }

    /// <summary>
    /// The Tier 2 cancellation path (docs/design/state-machines.md §2.4–§2.6). Claims
    /// the request for reconciliation (a no-op if the real reply already won via the
    /// read loop's normal path), sends <c>CLIENT UNBLOCK &lt;id&gt; TIMEOUT</c> on the
    /// control channel, then waits — bounded by <see cref="UnblockReconciliationGrace"/>
    /// — for the corresponding frame on the primary connection before deciding the
    /// caller's outcome. Every exit path completes `pending` exactly once (H1/H2) and
    /// only ever leaves the connection in the pool-eligible <c>Ready</c> state when the
    /// reconciliation could be proven clean (H3) — any ambiguity faults (and thereby
    /// closes) the connection instead.
    /// </summary>
    private async Task ReconcileCancellationAsync(PendingRequest pending, IControlChannel controlChannel)
    {
        if (!pending.TryClaimForReconciliation(out var frameDelivered))
        {
            // The real reply already completed this request via the read loop's normal
            // TryCompleteWithResult path. Nothing to reconcile.
            return;
        }

        try
        {
            RedisResult unblockReply;
            try
            {
                unblockReply = await controlChannel
                    .ExecuteAsync(CommandNames.Client, [CommandNames.ClientUnblockSubcommand, EncodeClientId(ClientId), CommandNames.UnblockTimeoutMode])
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Control channel itself failed — no proof the server-side block was
                // ever interrupted. Discard rather than risk handing back a connection
                // that's still blocked server-side (H3).
                pending.CompleteClaimedWithException(new OperationCanceledException("The blocking command was cancelled, but CLIENT UNBLOCK could not be delivered.", ex));
                await FaultAsync(new RedisConnectionException("Failed to reconcile a cancelled blocking command.", ex)).ConfigureAwait(false);
                return;
            }

            using var graceCts = new CancellationTokenSource(UnblockReconciliationGrace);
            RedisResult frame;
            try
            {
                frame = await frameDelivered.WaitAsync(graceCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                pending.CompleteClaimedWithException(new OperationCanceledException("Timed out reconciling a cancelled blocking command; the connection cannot be safely reused."));
                await FaultAsync(new RedisConnectionException("Grace deadline elapsed reconciling a cancelled blocking command.")).ConfigureAwait(false);
                return;
            }

            if (unblockReply.AsInt64() == 1)
            {
                // The server confirms it actually unblocked our client; the frame that
                // just arrived is the resulting timeout/error artifact, not real data.
                pending.CompleteClaimedWithException(new OperationCanceledException("The blocking command was cancelled."));
            }
            else
            {
                // UNBLOCK returned 0: a genuine reply had already been produced before
                // the server could act on it. Honor it — see design doc §2.4's policy —
                // rather than throw away a valid result the caller already paid for.
                pending.CompleteClaimed(frame);
            }
        }
        catch (Exception ex)
        {
            // Defensive: never leave `pending` uncompleted (H1) regardless of what else
            // goes wrong above.
            pending.CompleteClaimedWithException(ex);
            await FaultAsync(ex).ConfigureAwait(false);
        }
    }

    private static byte[] EncodeClientId(long clientId) => Encoding.ASCII.GetBytes(clientId.ToString(CultureInfo.InvariantCulture));

    private async ValueTask WriteAndEnqueueAsync(PendingRequest pending, ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfNotReady();
            RespCommandWriter.WriteCommand(_writer, commandName.Span, args);
            _pending.Enqueue(pending);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Any failure here — including a cancelled write, not just an I/O error —
            // faults the whole connection, never just this request: RespCommandWriter
            // may have already written partial command bytes into the pipe, which
            // desyncs RESP framing for every other request sharing this connection's
            // FIFO reply order. There is no such thing as a safely-cancelled write.
            //
            // `pending` may already be queued above and will be failed by the fault
            // drain (Invariant I4). It is deliberately NOT returned to the free-list
            // here: nobody will call GetResult() on it, so its ManualResetValueTaskSourceCore
            // is never reset, and pooling it unreset would hand the next renter a
            // source that's already completed. Let it be collected instead.
            await FaultAsync(ex).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task OpenAsync(RedisConnectionOptions options, CancellationToken cancellationToken)
    {
        SetState(ConnectionState.Connecting);

        _socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        ApplyKeepAlive(_socket, options);

        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(options.ConnectTimeout);
            await _socket.ConnectAsync(options.EndPoint, connectCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetState(ConnectionState.Faulted);
            _socket.Dispose();
            SetState(ConnectionState.Closed);
            throw new RedisConnectionException("Failed to connect.", ex);
        }

        var networkStream = new NetworkStream(_socket, ownsSocket: true);

        if (options.UseTls)
        {
            SetState(ConnectionState.TlsHandshaking);

            var sslStream = new SslStream(networkStream, leaveInnerStreamOpen: false, options.CertificateValidationCallback);
            try
            {
                var sslOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = options.TlsTargetHost ?? InferTargetHost(options.EndPoint),
                    ClientCertificates = options.ClientCertificates,
                };
                await sslStream.AuthenticateAsClientAsync(sslOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SetState(ConnectionState.Faulted);
                sslStream.Dispose();
                SetState(ConnectionState.Closed);
                throw new RedisConnectionException("TLS handshake failed.", ex);
            }

            _stream = sslStream;
        }
        else
        {
            _stream = networkStream;
        }

        SetState(ConnectionState.ProtocolHandshake);

        _reader = PipeReader.Create(_stream);
        _writer = PipeWriter.Create(_stream);

        RedisCredentials? credentials = null;

        try
        {
            credentials = options.CredentialsProvider is not null
                ? await options.CredentialsProvider.GetCredentialsAsync(cancellationToken).ConfigureAwait(false)
                : null;

            var helloArgs = BuildHelloArgs(options, credentials);
            RespCommandWriter.WriteCommand(_writer, "HELLO"u8, helloArgs);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);

            SetState(ConnectionState.Authenticating);
            var reply = await ReadOneFrameAsync(_reader, cancellationToken).ConfigureAwait(false);

            if (reply.IsError)
            {
                throw new RedisConnectionException($"HELLO failed: {reply.AsString()}");
            }

            // Captured once, up front, per docs/design/state-machines.md §2.2 — a Tier 2
            // cancellation needs this to issue CLIENT UNBLOCK without an extra round
            // trip at cancel time.
            RespCommandWriter.WriteCommand(_writer, CommandNames.Client, [CommandNames.ClientIdSubcommand]);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            var clientIdReply = await ReadOneFrameAsync(_reader, cancellationToken).ConfigureAwait(false);
            ClientId = clientIdReply.AsInt64();
        }
        catch (RedisConnectionException)
        {
            SetState(ConnectionState.Faulted);
            await CloseAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            SetState(ConnectionState.Faulted);
            await CloseAsync().ConfigureAwait(false);
            throw new RedisConnectionException("Protocol handshake failed.", ex);
        }

        SetState(ConnectionState.Ready);

        // Deliberately CancellationToken.None: the read loop's lifetime is the
        // connection's lifetime, not the caller's connect-time deadline — it must
        // keep running (and eventually fault itself via ReadLoopAsync's own catch)
        // long after ConnectAsync's token could legitimately fire or be disposed.
        _readLoopTask = Task.Run(ReadLoopAsync, CancellationToken.None);

        if (options.CredentialsProvider is not null)
        {
            _credentialsProvider = options.CredentialsProvider;
            if (credentials?.ExpiresAt is DateTimeOffset expiresAt)
            {
                _ = Task.Run(() => CredentialRefreshLoopAsync(expiresAt), CancellationToken.None);
            }
        }
    }

    private static void ApplyKeepAlive(Socket socket, RedisConnectionOptions options)
    {
        if (!options.EnableTcpKeepAlive)
        {
            return;
        }

        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

        try
        {
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, (int)options.TcpKeepAliveTime.TotalSeconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, (int)options.TcpKeepAliveInterval.TotalSeconds);
        }
        catch (PlatformNotSupportedException)
        {
            // Best-effort: some platforms only expose the plain on/off KeepAlive option.
        }
    }

    private static string InferTargetHost(EndPoint endPoint) => endPoint switch
    {
        DnsEndPoint dns => dns.Host,
        IPEndPoint ip => ip.Address.ToString(),
        _ => throw new RedisConnectionException($"Cannot infer a TLS target host from endpoint type '{endPoint.GetType()}'; set RedisConnectionOptions.TlsTargetHost explicitly."),
    };

    private static ReadOnlyMemory<byte>[] BuildHelloArgs(RedisConnectionOptions options, RedisCredentials? providerCredentials)
    {
        string versionArg = options.RespVersion.ToString(CultureInfo.InvariantCulture);

        if (providerCredentials is RedisCredentials credentials)
        {
            return
            [
                Encoding.ASCII.GetBytes(versionArg),
                Encoding.UTF8.GetBytes("AUTH"),
                Encoding.UTF8.GetBytes(credentials.Username ?? "default"),
                Encoding.UTF8.GetBytes(credentials.Password),
            ];
        }

        if (options.Password is null)
        {
            return [Encoding.ASCII.GetBytes(versionArg)];
        }

        return
        [
            Encoding.ASCII.GetBytes(versionArg),
            Encoding.UTF8.GetBytes("AUTH"),
            Encoding.UTF8.GetBytes(options.Username ?? "default"),
            Encoding.UTF8.GetBytes(options.Password),
        ];
    }

    private static async ValueTask<RedisResult> ReadOneFrameAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            var readResult = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = readResult.Buffer;

            if (RespFrameReader.TryParse(ref buffer, out var result))
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
                return result;
            }

            reader.AdvanceTo(buffer.Start, buffer.End);

            if (readResult.IsCompleted)
            {
                throw new RespProtocolException("Connection closed before a complete frame was received.");
            }
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var readResult = await _reader.ReadAsync().ConfigureAwait(false);
                var buffer = readResult.Buffer;

                while (RespFrameReader.TryParse(ref buffer, out var result))
                {
                    DispatchReply(result);
                }

                _reader.AdvanceTo(buffer.Start, buffer.End);

                if (readResult.IsCompleted)
                {
                    throw new RedisConnectionException("The peer closed the connection.");
                }
            }
        }
        catch (Exception ex)
        {
            await FaultAsync(ex).ConfigureAwait(false);
        }
    }

    private void DispatchReply(RedisResult result)
    {
        if (result.Type == RespType.Push)
        {
            // Out-of-band RESP3 push (pub/sub, client-side-cache invalidation, etc.) —
            // not correlated to a pending request. No subscriber layer exists yet
            // (project spec §10); dropped here until it does.
            return;
        }

        if (!_pending.TryDequeue(out var request))
        {
            throw new RespProtocolException("Received a reply with no matching pending request.");
        }

        if (request.TryCompleteWithResult(result))
        {
            return;
        }

        // Already claimed. If a Tier 2 cancellation is reconciling this request, it's
        // waiting to inspect this exact frame (docs/design/state-machines.md §2.4-2.6) —
        // hand it over instead of dropping it. If no reconciliation is in progress
        // (the simpler Tier 1 cancellation case), this is a no-op and the frame is
        // discarded, matching Tier 1's documented policy.
        request.TryDeliverReconciliationFrame(result);
    }

    /// <summary>
    /// Transitions to Faulted and drains every outstanding request exactly once
    /// (Invariant I4), idempotently — concurrent callers (the read loop, a failed
    /// write) may all observe a fault at once, but only the first one actually
    /// performs the drain and close.
    /// </summary>
    private async Task FaultAsync(Exception cause)
    {
        var previous = (ConnectionState)Interlocked.Exchange(ref _state, (int)ConnectionState.Faulted);
        if (previous is ConnectionState.Faulted or ConnectionState.Closed)
        {
            return;
        }

        while (_pending.TryDequeue(out var request))
        {
            request.TryCompleteWithException(new RedisConnectionException("The connection failed.", cause));
        }

        await CloseAsync().ConfigureAwait(false);
    }

    private async ValueTask CloseAsync()
    {
        SetState(ConnectionState.Closed);
        await _writer.CompleteAsync().ConfigureAwait(false);
        await _reader.CompleteAsync().ConfigureAwait(false);
        _stream.Dispose();
        _socket.Dispose();
    }

    private void ThrowIfNotReady()
    {
        if (State != ConnectionState.Ready)
        {
            throw new RedisConnectionException($"The connection is not ready (state: {State}).");
        }
    }

    private void SetState(ConnectionState state) => Volatile.Write(ref _state, (int)state);

    private PendingRequest RentPendingRequest() =>
        _requestPool.TryDequeue(out var pending) ? pending : new PendingRequest();

    private void ReturnPendingRequest(PendingRequest pending)
    {
        pending.ResetForPooling();
        _requestPool.Enqueue(pending);
    }
}
