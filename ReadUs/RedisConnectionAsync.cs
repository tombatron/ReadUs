using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ReadUs.Parser;
using ReadUs.ResultModels;
using static ReadUs.Parser.Parser;

namespace ReadUs;

public partial class RedisConnection
{
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        using var cancellationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        cancellationTimeout.CancelAfter(_commandTimeout);

        try
        {
            await _socket.ConnectAsync(_endPoint, cancellationTimeout.Token).ConfigureAwait(false);

            Trace.WriteLine($"Connected {ConnectionName} to {_endPoint.Address}:{_endPoint.Port}.");

            _backgroundTask = Task.Run(() => ConnectionWorker(_channel, _socket, this, cancellationToken), _backgroundTaskCancellationTokenSource.Token);

            await SetConnectionClientNameAsync(cancellationToken);
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