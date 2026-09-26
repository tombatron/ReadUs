using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipelines;
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
/// Tier 2 (leased, dedicated) connections — required for blocking commands,
/// transactions, and pub/sub sessions per project spec §4 — are not implemented yet
/// (§13 step 3); this type only needs to satisfy the non-blocking multiplexed path.
/// </summary>
public sealed class RedisConnection : IAsyncDisposable
{
    private readonly ConcurrentQueue<PendingRequest> _pending = new();
    private readonly ConcurrentQueue<PendingRequest> _requestPool = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private Socket _socket = null!;
    private Stream _stream = null!;
    private PipeReader _reader = null!;
    private PipeWriter _writer = null!;
    private Task _readLoopTask = Task.CompletedTask;

    private int _state = (int)ConnectionState.Created;

    private RedisConnection()
    {
    }

    internal ConnectionState State => (ConnectionState)Volatile.Read(ref _state);

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

    public async ValueTask DisposeAsync()
    {
        await FaultAsync(new ObjectDisposedException(nameof(RedisConnection))).ConfigureAwait(false);
        await _readLoopTask.ConfigureAwait(false);
    }

    private async Task OpenAsync(RedisConnectionOptions options, CancellationToken cancellationToken)
    {
        SetState(ConnectionState.Connecting);

        _socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

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

        // TLS (project spec §7) is not implemented yet — TlsHandshaking is skipped.
        SetState(ConnectionState.ProtocolHandshake);

        _stream = new NetworkStream(_socket, ownsSocket: true);
        _reader = PipeReader.Create(_stream);
        _writer = PipeWriter.Create(_stream);

        try
        {
            var helloArgs = BuildHelloArgs(options);
            RespCommandWriter.WriteCommand(_writer, "HELLO"u8, helloArgs);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);

            SetState(ConnectionState.Authenticating);
            var reply = await ReadOneFrameAsync(_reader, cancellationToken).ConfigureAwait(false);

            if (reply.IsError)
            {
                throw new RedisConnectionException($"HELLO failed: {reply.AsString()}");
            }
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
    }

    private static ReadOnlyMemory<byte>[] BuildHelloArgs(RedisConnectionOptions options)
    {
        if (options.Password is null)
        {
            return [Encoding.ASCII.GetBytes(options.RespVersion.ToString(CultureInfo.InvariantCulture))];
        }

        return
        [
            Encoding.ASCII.GetBytes(options.RespVersion.ToString(CultureInfo.InvariantCulture)),
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

        // Returns false if a caller's CancellationToken already claimed this request —
        // the real reply is simply discarded, per the Tier 1 cancellation policy
        // documented on PendingRequest.
        request.TryCompleteWithResult(result);
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
