using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ReadUs.Parser;
using ReadUs.ResultModels;
using static ReadUs.Parser.Parser;

namespace ReadUs;

public partial class RedisConnection
{
    // TODO: Going to rename this at some point. 
    private Task _connectionWork = Task.CompletedTask;
    private readonly CancellationTokenSource _backgroundTaskCancellationTokenSource = new();

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        using var cancellationWithTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        cancellationWithTimeout.CancelAfter(_commandTimeout);

        try
        {
            await _socket.ConnectAsync(_endPoint, cancellationWithTimeout.Token).ConfigureAwait(false);

            Trace.WriteLine($"Connected {ConnectionName} to {_endPoint.Address}:{_endPoint.Port}.");

            _connectionWork = Task.Run(() => ConnectionWorker(_channel, _socket, this, _backgroundTaskCancellationTokenSource.Token), _backgroundTaskCancellationTokenSource.Token);
            
        }
        catch (SocketException sockEx)
        {
            throw new Exception($"Could not connect to endpoint: {_endPoint.Address}:{_endPoint.Port}", sockEx);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // We're in here now assuming that the cancellation is because the timeout has lapsed.
            Trace.WriteLine("Connection attempt timed out.");

            throw;
        }
    }
    
    public async Task<Result<byte[]>> SendCommandAsync(RedisCommandEnvelope command, CancellationToken cancellationToken = default)
    {
        // Write command.
        await _socket.SendAsync(command, cancellationToken);

        // Wait for a response.
        await _channel.Reader.WaitToReadAsync(cancellationToken);

        if (_channel.Reader.TryRead(out var response))
        {
            return Result<byte[]>.Ok(response);
        }

        return Result<byte[]>.Error("Failed to read response.");
    }
    
    // Initial stab at implementing pub/sub...
    public async Task SendCommandWithMultipleResponses(RedisCommandEnvelope command, Action<byte[]> onResponse, CancellationToken cancellationToken = default)
    {
        // Write command.
        await _socket.SendAsync(command, cancellationToken);
        
        while (!cancellationToken.IsCancellationRequested)
        {
            await _channel.Reader.WaitToReadAsync(cancellationToken);
            
            if (_channel.Reader.TryRead(out var response))
            {
                onResponse(response);
            }
        }
    }
    
    private async Task SetConnectionClientNameAsync(CancellationToken cancellationToken) =>
        await SendCommandAsync(RedisCommandEnvelope.CreateClientSetNameCommand(ConnectionName), cancellationToken);
    
    public async Task<Result<RoleResult>> RoleAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            var result = await SendCommandAsync(RedisCommandEnvelope.CreateRoleCommand(), cancellationToken);

            if (result is Ok<byte[]> ok)
            {
                var parseResult = Parse(ok.Value);

                if (parseResult is Ok<ParseResult> parseOk)
                {
                    return Result<RoleResult>.Ok((RoleResult)parseOk.Value);
                }

                if (parseResult is Error<ParseResult> parseErr)
                {
                    return Result<RoleResult>.Error(parseErr.Message);
                }
            }

            if (result is Error<byte[]> err)
            {
                return Result<RoleResult>.Error(err.Message);
            }
        }

        return Result<RoleResult>.Error("Socket isn't ready, can't execute command.");
    }    
}