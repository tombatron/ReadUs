using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ReadUs.Exceptions;
using ReadUs.Parser;
using ReadUs.ResultModels;
using static ReadUs.Parser.Parser;

namespace ReadUs;

public abstract class RedisDatabase(IRedisConnection connection, IRedisConnectionPool pool) : IRedisDatabase
{
    private bool _isDisposed;

    public IRedisConnection Connection => connection;

    public abstract Task<Result<BlockingPopResult>> BlockingLeftPopAsync(params RedisKey[] keys);

    public abstract Task<Result<BlockingPopResult>> BlockingLeftPopAsync(TimeSpan timeout, params RedisKey[] keys);

    public abstract Task<Result<BlockingPopResult>> BlockingRightPopAsync(params RedisKey[] keys);

    public abstract Task<Result<BlockingPopResult>> BlockingRightPopAsync(TimeSpan timeout, params RedisKey[] keys);

    public abstract Task<Result> SelectAsync(int databaseId, CancellationToken cancellationToken = default);

    public virtual async Task<Result> SetMultipleAsync(KeyValuePair<RedisKey, string>[] keysAndValues, CancellationToken cancellationToken = default)
    {
        if (IsDisposed(out var error))
        {
            return Result.Error(error!);
        }
        
        var command = RedisCommandEnvelope.CreateSetMultipleCommand(keysAndValues);

        // TODO: Handle this using the result type instead of by allowing this to throw an exception. But we need to test several scenarios
        //       so that we make sure that we're handling only the exceptions we expect to handle.
        var rawResult = await connection.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);

        // var result = Parse(rawResult);

        var result = Parse(rawResult) switch
        {
            Ok<ParseResult> okResult => EvaluateResult(okResult.Value),
            Error<ParseResult> errorResult => Result.Error(errorResult.Message);
            _ => Result.Error("An unexpected error occurred while attempting to parse the result of the SET command.")
        };

        return result;
    }

    public virtual async Task<Result<string>> GetAsync(RedisKey key, CancellationToken cancellationToken = default)
    {
        IsDisposed();

        var command = RedisCommandEnvelope.CreateGetCommand(key);

        var rawResult = await connection.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);

        var result = Parse(rawResult);

        EvaluateResult(result);

        return Result<string>.Ok(result.ToString());
    }

    public virtual async Task<int> LeftPushAsync(RedisKey key, params string[] element)
    {
        IsDisposed();

        var command = RedisCommandEnvelope.CreateLeftPushCommand(key, element);

        var rawResult = await _connection.SendCommandAsync(command).ConfigureAwait(false);

        var result = Parse(rawResult);

        EvaluateResult(result);

        return ParseAndReturnInt(result);
    }

    public virtual async Task<int> ListLengthAsync(RedisKey key)
    {
        IsDisposed();

        var command = RedisCommandEnvelope.CreateListLengthCommand(key);

        var rawResult = await _connection.SendCommandAsync(command).ConfigureAwait(false);

        var result = Parse(rawResult);

        EvaluateResult(result);

        return ParseAndReturnInt(result);
    }

    public virtual async Task<int> RightPushAsync(RedisKey key, params string[] element)
    {
        IsDisposed();

        var command = RedisCommandEnvelope.CreateRightPushCommand(key, element);

        var rawResult = await _connection.SendCommandAsync(command).ConfigureAwait(false);

        var result = Parse(rawResult);

        EvaluateResult(result);

        return ParseAndReturnInt(result);
    }

    public virtual async Task SetAsync(RedisKey key, string value, CancellationToken cancellationToken = default)
    {
        IsDisposed();

        var command = RedisCommandEnvelope.CreateSetCommand(key, value);

        var rawResult = await _connection.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);

        var result = Parse(rawResult);

        EvaluateResult(result);
    }

    public virtual void Dispose()
    {
        _pool.ReturnConnection(_connection);

        _isDisposed = true;
    }

    internal event RedisServerExceptionEventHandler? RedisServerExceptionEvent;

    protected bool IsDisposed(out string? errorMessage)
    {
        errorMessage = null;
        
        if (_isDisposed)
        {
            errorMessage = "This instance of `RedisDatabase` has already been disposed.";
        }

        return _isDisposed;
    }

    protected Result EvaluateResult(ParseResult result, [CallerMemberName] string callingMember = "")
    {
        if (result.Type == ResultType.Error)
        {
            // This is kind of junky... we'll deal with it later. 
            var exceptionToThrow = new RedisServerException(
                $"Redis returned an error while we were invoking `{callingMember}`. The error was: {result.ToString()}",
                result.ToString());

            RedisServerExceptionEvent?.Invoke(this, new RedisServerExceptionEventArgs(exceptionToThrow));

            throw exceptionToThrow;
        }
    }

    protected static int ParseAndReturnInt(ParseResult result, [CallerMemberName] string callingMember = "")
    {
        if (result.Type == ResultType.Integer) return int.Parse(result.ToString());

        // TODO: Need a real custom exception here. 
        throw new Exception($"We expected an integer type in the reply but got {result.Type.ToString()} instead.");
    }

    internal delegate void RedisServerExceptionEventHandler(object sender, RedisServerExceptionEventArgs args);
}