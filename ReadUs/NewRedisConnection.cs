using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ReadUs.ResultModels;

namespace ReadUs;

public class NewRedisConnection(IPEndPoint endPoint, TimeSpan commandTimeout) : IRedisConnection
{
    private static int _connectionCount = 0;

    private readonly IPEndPoint _endPoint = endPoint;
    private readonly Socket _socket = new(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
    private readonly TimeSpan _commandTimeout = commandTimeout;

    public string ConnectionName { get; } = $"ReadUs_Connection_{++_connectionCount}";

    public bool IsConnected => _socket.Connected;

    public void Connect()
    {
        Trace.WriteLine($"Connected {ConnectionName} to {_endPoint.Address}:{_endPoint.Port}.");

        _socket.Connect(_endPoint);

        SetConnectionClientName();
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        using var cancellationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        cancellationTimeout.CancelAfter(_commandTimeout);

        try
        {
            await _socket.ConnectAsync(_endPoint, cancellationTimeout.Token).ConfigureAwait(false);
            
            Trace.WriteLine($"Connected {ConnectionName} to {_endPoint.Address}:{_endPoint.Port}.");

            await SetConnectionClientNameAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // We're in here now assuming that the cancellation is because the timeout has lapsed.
            Trace.WriteLine("Connection attempt timed out.");

            throw;
        }
    }

    public Result<byte[]> SendCommand(RedisCommandEnvelope command)
    {
        throw new System.NotImplementedException();
    }

    public Task<Result<byte[]>> SendCommandAsync(RedisCommandEnvelope command, CancellationToken cancellationToken = default)
    {
        throw new System.NotImplementedException();
    }

    // Putting the `Role` and `RoleAsync` methods here to show that they are really only relevent
    // to a specific connection.    
    public Result<RoleResult> Role()
    {
        throw new System.NotImplementedException();
    }

    public Task<Result<RoleResult>> RoleAsync(CancellationToken cancellationToken = default)
    {
        throw new System.NotImplementedException();
    }

    // Putting the `SetConnectionClientName` and `SetConnectionClientNameAsync` methods here to show that they are really only relevent
    // to a specific connection.    
    private void SetConnectionClientName()
    {
        SendCommand(RedisCommandEnvelope.CreateClientSetNameCommand(ConnectionName));
    }

    private Task SetConnectionClientNameAsync(CancellationToken cancellationToken)
    {
        return SendCommandAsync(RedisCommandEnvelope.CreateClientSetNameCommand(ConnectionName), cancellationToken);
    }

    public void Dispose()
    {
        throw new System.NotImplementedException();
    }
}