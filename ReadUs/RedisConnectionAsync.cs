using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ReadUs.Parser;
using ReadUs.ResultModels;
using SlotRange = ReadUs.ResultModels.ClusterSlots.SlotRange;

using static ReadUs.Parser.Parser;

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

    public async Task<Result<byte[]>> SendCommandAsync(RedisCommandEnvelope command,
        CancellationToken cancellationToken = default)
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
            // TODO: Assuming this works we'll make it a little more fancy. 
            return Result<byte[]>.Error($"[TIMEOUT]: Redis command took longer than: {_commandTimeout}");
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
                
                if (parseResult is Error<ParseResult> parseErr)
                {
                    return Result<RoleResult>.Error(parseErr.Message);
                }

                return Result<RoleResult>.Ok((RoleResult)parseResult.Unwrap());
            }

            if (result is Error<byte[]> err)
            {
                return Result<RoleResult>.Error(err.Message);
            }
        }

        return Result<RoleResult>.Error("Socket isn't ready, can't execute command.");
    }

    public async Task<Result<ClusterSlots>> SlotsAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            var result = await SendCommandAsync(RedisCommandEnvelope.CreateClusterShardsCommand(), cancellationToken);

            if (result is Error<byte[]> err)
            {
                return Result<ClusterSlots>.Error("Error getting slots for connection.", err);
            }
            
            var commandResult = result.Unwrap();
            var parsedResult = Parse(commandResult);

            if (parsedResult is Error<ParseResult> parseErr)
            {
                return Result<ClusterSlots>.Error("Error parsing the command result.", parseErr);
            }
            
            var okResult = parsedResult.Unwrap();
            
            var clusterShards = new ClusterShardsResult(okResult);
            var currentShard = clusterShards.First(x => x.Nodes!.Any(y => y.Port == EndPoint.Port));

            return Result<ClusterSlots>.Ok(new(currentShard!.Slots!));
        }

        return Result<ClusterSlots>.Error("Socket isn't ready, can't execute command.");
    }
}