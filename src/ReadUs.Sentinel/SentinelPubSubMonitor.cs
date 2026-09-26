using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using ReadUs.Protocol;

namespace ReadUs.Sentinel;

/// <summary>
/// A minimal, dedicated connection subscribed to one sentinel's <c>+switch-master</c>
/// channel — project spec §6's "dedicated connection to at least one... sentinel[s]...
/// for fast failover detection". Deliberately not built on <c>RedisConnection</c>:
/// that type's Tier 1/Tier 2 machinery exists to correlate many concurrent requests to
/// FIFO replies, which is irrelevant here — this connection sends exactly one command
/// ever (<c>SUBSCRIBE</c>) and then only ever receives unsolicited push messages for
/// the rest of its life. Building a general pub/sub layer into <c>RedisConnection</c>
/// is real future work (project spec §10) but isn't a prerequisite for Sentinel, which
/// only needs this one narrow channel. See <see cref="SentinelClient"/>'s pub/sub
/// supervisor loop for reconnection policy.
/// </summary>
internal sealed class SentinelPubSubMonitor : IAsyncDisposable
{
    private const string SwitchMasterChannel = "+switch-master";

    private readonly Socket _socket;
    private readonly Stream _stream;
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoopTask;

    private SentinelPubSubMonitor(Socket socket, Stream stream, PipeReader reader, PipeWriter writer, Action<string> onMessage)
    {
        _socket = socket;
        _stream = stream;
        _reader = reader;
        _writer = writer;
        _readLoopTask = Task.Run(() => ReadLoopAsync(onMessage));
    }

    /// <summary>Completes when the read loop exits for any reason (connection dropped, disposed) — the supervisor loop in <see cref="ReadUs.SentinelClient"/> awaits this to know when to reconnect.</summary>
    public Task Completion => _readLoopTask;

    public static async Task<SentinelPubSubMonitor> SubscribeAsync(EndPoint endpoint, Action<string> onMessage, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);

        var stream = new NetworkStream(socket, ownsSocket: true);
        var reader = PipeReader.Create(stream);
        var writer = PipeWriter.Create(stream);

        RespCommandWriter.WriteCommand(writer, "SUBSCRIBE"u8, [System.Text.Encoding.ASCII.GetBytes(SwitchMasterChannel)]);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        // The subscribe confirmation itself ("subscribe", channel, count) — consumed
        // and discarded so the read loop only ever sees real messages afterward.
        await ReadOneFrameAsync(reader, cancellationToken).ConfigureAwait(false);

        return new SentinelPubSubMonitor(socket, stream, reader, writer, onMessage);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        try
        {
            await _writer.CompleteAsync().ConfigureAwait(false);
            await _reader.CompleteAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort; the socket dispose below is what actually matters.
        }

        _stream.Dispose();
        _socket.Dispose();

        try
        {
            await _readLoopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }

    private async Task ReadLoopAsync(Action<string> onMessage)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await ReadOneFrameAsync(_reader, _cts.Token).ConfigureAwait(false);
                var items = frame.AsItems();

                // A pub/sub message arrives as ["message", channel, payload].
                if (items.Length >= 3 && items[0].AsString() == "message")
                {
                    onMessage(items[2].AsString());
                }
            }
        }
        catch
        {
            // The connection dropped or was disposed — the caller's supervisor loop
            // (polling is the documented safety net regardless) is responsible for
            // deciding whether/how to reconnect, not this type.
        }
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
}
