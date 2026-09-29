using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using ReadUs.Diagnostics;
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

    /// <summary>
    /// Work accepted for writing but not yet on the wire (design doc Invariant I8).
    /// <see cref="PendingRequest"/> is null for a subscription-state command (design
    /// doc §2/Pub/Sub §10) — its confirmation arrives via an out-of-band push, not a
    /// FIFO-correlated reply, so there is nothing to enqueue into <see cref="_pending"/>
    /// for it.
    /// </summary>
    private readonly ConcurrentQueue<(ReadOnlyMemory<byte> CommandName, ReadOnlyMemory<byte>[] Args, PendingRequest? Pending)> _writeQueue = new();

    /// <summary>Wakes <see cref="WriteLoopAsync"/>. Unbounded max count (the single-arg constructor) so concurrent <see cref="SemaphoreSlim.Release()"/> calls from many producers never throw; a few redundant wakeups this can cause are a harmless, bounded inefficiency, not a correctness concern.</summary>
    private readonly SemaphoreSlim _flushSignal = new(0);

    /// <summary>
    /// Guards the narrow "move one item from <see cref="_writeQueue"/> into
    /// <see cref="_pending"/>" step (design doc Invariant I8) — without this, an item
    /// dequeued from <see cref="_writeQueue"/> but not yet re-enqueued into
    /// <see cref="_pending"/> is observable in *neither* queue for a few CPU
    /// instructions, a real window a concurrently-running <see cref="FaultAsync"/>
    /// (triggered independently by, say, the read loop) can land in and miss it
    /// entirely — found via a genuine, reproducible hang under real concurrent fault
    /// injection, not by inspection. Held only for that one synchronous move, never
    /// across <c>WriteCommand</c>'s full batch or <c>FlushAsync</c>, so it costs
    /// nothing close to what the original single write-gate-across-the-flush design
    /// did.
    /// </summary>
    private readonly Lock _writeQueueMoveLock = new();

    private Socket _socket = null!;
    private Stream _stream = null!;
    private PipeReader _reader = null!;
    private PipeWriter _writer = null!;
    private Task _readLoopTask = Task.CompletedTask;
    private Task _writeLoopTask = Task.CompletedTask;
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

        if (options.PostConnectAsync is not null)
        {
            await options.PostConnectAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    public async ValueTask<RedisResult> SendAsync(ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken = default)
    {
        ThrowIfNotReady();

        var pending = RentPendingRequest();
        var startTimestamp = Stopwatch.GetTimestamp();
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
            ReadUsDiagnostics.CommandExecuted("multiplexed", Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
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
        var startTimestamp = Stopwatch.GetTimestamp();
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
            // Includes the actual server-side block time by design — tagged
            // separately from "multiplexed" so it doesn't skew that histogram; how
            // long blocking commands actually block is itself useful signal.
            ReadUsDiagnostics.CommandExecuted("leased", Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await FaultAsync(new ObjectDisposedException(nameof(RedisConnection))).ConfigureAwait(false);
        await _readLoopTask.ConfigureAwait(false);
        await _writeLoopTask.ConfigureAwait(false);
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

    /// <summary>
    /// Accepts a command for writing and returns immediately — the actual write and
    /// flush happen on <see cref="WriteLoopAsync"/> (design doc Invariant I7), not on
    /// this caller's own execution context. This is safe for every caller today: both
    /// <see cref="SendAsync"/> and <see cref="SendBlockingAsync"/> only actually need to
    /// wait for the eventual *reply* (via <paramref name="pending"/>'s own
    /// <see cref="ValueTask{TResult}"/>), never specifically for "the flush completed" —
    /// they were always really waiting on the reply, so returning here before the flush
    /// happens changes nothing observable to them.
    /// </summary>
    private ValueTask WriteAndEnqueueAsync(PendingRequest pending, ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfNotReady();
        _writeQueue.Enqueue((commandName, args, pending));
        _flushSignal.Release();
        CompleteIfStrandedByARaceWithFaultAsync(pending);
        return default;
    }

    /// <summary>
    /// Closes a narrow race (design doc Invariant I8) between this call's own
    /// <see cref="ThrowIfNotReady"/> check and its <see cref="_writeQueue"/> enqueue:
    /// <see cref="FaultAsync"/> only ever drains <see cref="_writeQueue"/> once (it's
    /// idempotent — a second call is a no-op), so an item enqueued strictly *after*
    /// that one-time drain already ran would otherwise sit there forever with nothing
    /// left to ever complete it. Re-checking <see cref="State"/> immediately after
    /// enqueueing and self-completing if it's no longer <see cref="ConnectionState.Ready"/>
    /// closes this for every interleaving: either this check observes the fault (and
    /// completes the request itself), or it doesn't — meaning the fault transition
    /// (which always happens strictly before <see cref="FaultAsync"/>'s own drain)
    /// hadn't happened yet at the time of this check, which happened strictly after
    /// the enqueue above, so <see cref="FaultAsync"/>'s drain is guaranteed to run
    /// strictly after the enqueue too and will catch it instead. Calling
    /// <see cref="PendingRequest.TryCompleteWithException"/> here even if
    /// <see cref="FaultAsync"/>'s drain also independently reaches the same item is
    /// safe and cannot double-complete anything (Invariant I3) — whichever call gets
    /// there first wins, and the other is a harmless no-op.
    /// </summary>
    private void CompleteIfStrandedByARaceWithFaultAsync(PendingRequest pending)
    {
        if (State != ConnectionState.Ready)
        {
            pending.TryCompleteWithException(new RedisConnectionException($"The connection is not ready (state: {State})."));
        }
    }

    /// <summary>
    /// Same acceptance path as <see cref="WriteAndEnqueueAsync"/>, but for the small set
    /// of subscription-state commands (<c>SUBSCRIBE</c>/<c>PSUBSCRIBE</c>/<c>SSUBSCRIBE</c>
    /// and their <c>UN</c>/<c>PUN</c>/<c>SUN</c> counterparts) whose confirmation arrives
    /// as an out-of-band RESP3 Push frame (project spec §1: RESP3 is always negotiated),
    /// not as the ordinary correlated reply every other command gets — so no
    /// <see cref="PendingRequest"/> is enqueued for one of these;
    /// <see cref="DispatchReply"/> hands a Push frame to <see cref="OnPush"/> directly,
    /// never touching <see cref="_pending"/>. Internal: a specialized primitive for
    /// <c>ReadUs.PubSub.RedisSubscriber</c>, not a general escape hatch — anything
    /// expecting a normal correlated reply must use <see cref="SendAsync"/>.
    /// </summary>
    internal ValueTask SendSubscriptionCommandAsync(ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfNotReady();
        _writeQueue.Enqueue((commandName, args, null));
        _flushSignal.Release();
        return default;
    }

    /// <summary>
    /// The sole writer for this connection's entire lifetime (design doc Invariant I7)
    /// — every other code path only ever enqueues into <see cref="_writeQueue"/> and
    /// signals <see cref="_flushSignal"/>; nothing else ever touches <see cref="_writer"/>.
    /// Draining as many currently-queued items as possible before a single
    /// <see cref="PipeWriter.FlushAsync"/> call (rather than one flush per item, the
    /// previous design) is what turns concurrent load into genuine pipelining instead of
    /// N serialized round trips to the socket — see docs/design/state-machines.md's
    /// "Recorded during implementation of batched connection writes" for the measurements
    /// that motivated this.
    /// </summary>
    private async Task WriteLoopAsync()
    {
        try
        {
            while (true)
            {
                await _flushSignal.WaitAsync().ConfigureAwait(false);

                if (State != ConnectionState.Ready)
                {
                    // Faulted while we were idle waiting — FaultAsync already drained
                    // both _pending and _writeQueue as part of that same transition
                    // (Invariant I4/I8); nothing left for us to do. A fault that
                    // happens *during* an active batch below is instead discovered the
                    // ordinary way, via WriteCommand/FlushAsync itself throwing.
                    return;
                }

                var batchSize = 0;
                while (TryClaimNextWriteQueueItem(out var item))
                {
                    RespCommandWriter.WriteCommand(_writer, item.CommandName.Span, item.Args);
                    batchSize++;
                }

                if (batchSize > 0)
                {
                    ReadUsDiagnostics.WriteBatchFlushed(batchSize);
                    await _writer.FlushAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            // Any failure here — including partway through a batch — faults the whole
            // connection, never just one item: RespCommandWriter may have already
            // written partial command bytes into the pipe for a *later* item in this
            // same batch, which desyncs RESP framing for every request sharing this
            // connection's FIFO reply order. There is no such thing as a safely-failed
            // write. Every item already moved into _pending above will be failed by
            // FaultAsync's existing drain (Invariant I4); anything still sitting in
            // _writeQueue (not yet reached by this batch, or enqueued by a racing
            // caller) is covered by I8's extension of that same drain.
            await FaultAsync(ex).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Dequeues one item from <see cref="_writeQueue"/> and, in the same lock-held
    /// step, claims it into <see cref="_pending"/> (or, for a subscription command
    /// with no <see cref="PendingRequest"/>, just removes it) — never leaving it
    /// observable in neither queue, which is exactly the window
    /// <see cref="_writeQueueMoveLock"/> exists to close (design doc Invariant I8; see
    /// that field's own remarks for the real, reproduced-under-load hang this fixes).
    /// <see cref="FaultAsync"/> holds the same lock across its own combined drain of
    /// both queues, so the two can never observe an item mid-move.
    /// </summary>
    private bool TryClaimNextWriteQueueItem(out (ReadOnlyMemory<byte> CommandName, ReadOnlyMemory<byte>[] Args, PendingRequest? Pending) item)
    {
        lock (_writeQueueMoveLock)
        {
            if (!_writeQueue.TryDequeue(out item))
            {
                return false;
            }

            if (item.Pending is not null)
            {
                _pending.Enqueue(item.Pending);
            }

            return true;
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
        ReadUsDiagnostics.ConnectionOpened();

        // Deliberately CancellationToken.None: the read loop's lifetime is the
        // connection's lifetime, not the caller's connect-time deadline — it must
        // keep running (and eventually fault itself via ReadLoopAsync's own catch)
        // long after ConnectAsync's token could legitimately fire or be disposed.
        _readLoopTask = Task.Run(ReadLoopAsync, CancellationToken.None);
        _writeLoopTask = Task.Run(WriteLoopAsync, CancellationToken.None);

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
        var versionArg = options.RespVersion.ToString(CultureInfo.InvariantCulture);

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

    /// <summary>
    /// Invoked from the read loop for every RESP3 out-of-band push frame (project spec
    /// §10) — pub/sub messages, <c>CLIENT TRACKING</c> invalidation notices, etc. Not
    /// correlated to any pending request, so there's nothing to complete; a caller that
    /// wants to observe these (e.g. <c>ReadUs.Caching.ClientSideCache</c>) sets this
    /// once, before the connection sees any traffic that could produce one. No general
    /// pub/sub subscriber layer exists yet — this is deliberately just a raw hook, not
    /// a queue or dispatcher, so it stays useful to whatever the next consumer turns
    /// out to need without guessing that shape now.
    /// </summary>
    public Action<RedisResult>? OnPush { get; set; }

    /// <summary>
    /// Invoked exactly once, from <see cref="FaultAsync"/>, the moment this connection
    /// transitions to <see cref="ConnectionState.Faulted"/> — a minimal seam in the same
    /// spirit as <see cref="OnPush"/>, for a consumer that has state of its own tied to
    /// this connection's lifetime and needs to know the instant it dies rather than
    /// discovering it lazily on the next call (e.g. <c>ReadUs.PubSub.RedisSubscriber</c>,
    /// whose open <c>await foreach</c> subscriptions would otherwise hang forever with no
    /// signal that nothing will ever arrive again).
    /// </summary>
    public Action<Exception>? OnFaulted { get; set; }

    private void DispatchReply(RedisResult result)
    {
        if (result.Type == RespType.Push)
        {
            OnPush?.Invoke(result);
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

        ReadUsDiagnostics.ConnectionFaulted(wasReady: previous == ConnectionState.Ready);

        // Both drains happen as one combined critical section, under the same lock
        // WriteLoopAsync's TryClaimNextWriteQueueItem briefly holds around its own
        // "dequeue from _writeQueue, claim into _pending" move (Invariant I8) — without
        // this, an item could be observed in *neither* queue (already dequeued from
        // _writeQueue, not yet re-enqueued into _pending) at the exact instant both
        // loops below run, and be missed by both. Found as a genuine, reproducible hang
        // under real concurrent fault injection, not by inspection — see
        // _writeQueueMoveLock's own remarks.
        lock (_writeQueueMoveLock)
        {
            while (_pending.TryDequeue(out var request))
            {
                request.TryCompleteWithException(new RedisConnectionException("The connection failed.", cause));
            }

            while (_writeQueue.TryDequeue(out var item))
            {
                item.Pending?.TryCompleteWithException(new RedisConnectionException("The connection failed.", cause));
            }
        }

        await CloseAsync().ConfigureAwait(false);

        // Wakes a write loop that's currently idle in _flushSignal.WaitAsync() with
        // nothing queued (e.g. DisposeAsync faulting a connection with no write in
        // flight) so it observes State != Ready and exits instead of leaking forever —
        // a loop that instead faults from its own write/flush exception already exits
        // via its own catch block regardless of this call.
        _flushSignal.Release();

        OnFaulted?.Invoke(cause);
    }

    private async ValueTask CloseAsync()
    {
        SetState(ConnectionState.Closed);

        // PipeWriter.CompleteAsync (StreamPipeWriter specifically) tries to flush any
        // still-buffered bytes before it finishes completing — which throws if the
        // transport is already broken (e.g. the peer reset the connection while a
        // write-loop batch still had unflushed bytes sitting in the writer). We're
        // already tearing down a connection already known to be dead; failing to
        // gracefully flush its last few bytes is expected and irrelevant here, and
        // must never prevent the real cleanup below (socket/stream disposal) from
        // running — an uncaught exception at this point would both crash whichever
        // caller is awaiting FaultAsync *and* leak the socket, since nothing after the
        // throwing line would ever execute.
        try
        {
            await _writer.CompleteAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            await _reader.CompleteAsync().ConfigureAwait(false);
        }
        catch
        {
        }

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
