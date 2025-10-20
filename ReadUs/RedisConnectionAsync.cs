using System.Diagnostics;
using System.Net.Sockets;
using ReadUs.Errors;

namespace ReadUs;

public partial class RedisConnection
{
    // TODO: Going to rename this at some point. 
    private Task _connectionWork = Task.CompletedTask;
    private readonly CancellationTokenSource _backgroundTaskCancellationTokenSource = new();

    public bool IsFaulted { get; private set; }

    public async Task<Result> ConnectAsync(CancellationToken cancellationToken = default)
    {
        using var cancellationWithTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        cancellationWithTimeout.CancelAfter(_commandTimeout);

        try
        {
            await _socket.ConnectAsync(_endPoint, cancellationWithTimeout.Token).ConfigureAwait(false);

            Trace.WriteLine($"Connected {ConnectionName} to {_endPoint.Address}:{_endPoint.Port}.");

            _connectionWork =
                Task.Run(() => ConnectionWorker(_channel, _socket, this, _backgroundTaskCancellationTokenSource.Token),
                    _backgroundTaskCancellationTokenSource.Token);

            return Result.Ok;
        }
        catch (SocketException sockEx)
        {
            IsFaulted = true;

            return Result.Error($"Could not connect to endpoint: {_endPoint.Address}:{_endPoint.Port}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            IsFaulted = true;
            // We're in here now assuming that the cancellation is because the timeout has lapsed.
            Trace.WriteLine("Connection attempt timed out.");

            return Result.Error("Connection attempt timed out.");
        }
    }

    public async Task<Result<byte[]>> SendCommandAsync(RedisCommandEnvelope command, CancellationToken cancellationToken = default)
    {
        using var timeoutCancellationSource = new CancellationTokenSource();
        timeoutCancellationSource.CancelAfter(_commandTimeout);

        using var combinedCancellationTokenSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellationSource.Token);

        try
        {
            // Write command.
            await _socket.SendAsync(command, combinedCancellationTokenSource.Token);

            // Wait for a response.
            await _channel.Reader.WaitToReadAsync(combinedCancellationTokenSource.Token);

            if (_channel.Reader.TryRead(out var response))
            {
                return Result<byte[]>.Ok(response);
            }
        }
        catch (OperationCanceledException ex) when (timeoutCancellationSource.IsCancellationRequested)
        {
            return CommandTimeout.Create<byte[]>($"Redis command took longer than: {_commandTimeout}");
        }
        catch (SocketException sockEx)
        {
            return Result<byte[]>.Error($"[SOCKET_EXCEPTION]: {sockEx.Message}");
        }

        return Result<byte[]>.Error("Failed to read response.");
    }

    // Initial stab at implementing pub/sub...
    public async Task SendCommandWithMultipleResponses(RedisCommandEnvelope command, Action<byte[]> onResponse,
        CancellationToken cancellationToken = default)
    {
        // Write command.
        await _socket.SendAsync(command, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            await _channel.Reader.WaitToReadAsync(cancellationToken);

            if (_channel.Reader.TryRead(out var response))
            {
                // I wonder if it would be helpful to pass the connection info as well?
                onResponse(response);
            }
        }
    }

    private async Task SetConnectionClientNameAsync(CancellationToken cancellationToken) =>
        await SendCommandAsync(RedisCommandEnvelope.CreateClientSetNameCommand(ConnectionName), cancellationToken);
}