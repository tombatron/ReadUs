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

        var result = Parse(rawResult) switch
        {
            Ok<ParseResult> ok => EvaluateResult(ok.Value),
            Error<ParseResult> err => Result.Error(err.Message),
            _ => Result.Error("An unexpected error occurred while attempting to parse the result of the SET command.")
        };

        return result;
    }

    public async Task<Result<int>> Publish(string channel, string message, CancellationToken cancellationToken = default)
    {
        var command = new RedisCommandEnvelope("PUBLISH", channel, null, null, false, message);

        var result = await connection.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);

        return Parse(result) switch
        {
            Ok<ParseResult> ok => EvaluateResult(ok.Value, ParseAndReturnInt),
            Error<ParseResult> err => Result<int>.Error(err.Message),
            _ => Result<int>.Error("An unexpected error occurred while attempting to parse the result of the PUBLISH command.")
        };
    }

    public virtual async Task<Result<string>> GetAsync(RedisKey key, CancellationToken cancellationToken = default)
    {
        if (IsDisposed(out var error))
        {
            return Result<string>.Error(error!);
        }

        var command = RedisCommandEnvelope.CreateGetCommand(key);

        var rawResult = await connection.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);

        var result = Parse(rawResult) switch
        {
            Ok<ParseResult> ok => EvaluateResult<string>(ok.Value, (pr) => Result<string>.Ok(pr.ToString())),
            Error<ParseResult> err => Result<string>.Error(err.Message),
            _ => Result<string>.Error("An unexpected error occurred while attempting to parse the result of the GET command.")
        };

        return result;
    }

    public virtual async Task<Result<int>> LeftPushAsync(RedisKey key, params string[] element)
    {
        if (IsDisposed(out var error))
        {
            return Result<int>.Error(error!);
        }

        var command = RedisCommandEnvelope.CreateLeftPushCommand(key, element);

        var rawResult = await connection.SendCommandAsync(command).ConfigureAwait(false);

        var result = Parse(rawResult) switch
        {
            Ok<ParseResult> ok => EvaluateResult<int>(ok.Value, ParseAndReturnInt),
            Error<ParseResult> err => Result<int>.Error(err.Message),
            _ => Result<int>.Error("An unexpected error occurred while attempting to parse the result of the LPUSH command.")
        };

        return result;
    }

    public virtual async Task<Result<int>> ListLengthAsync(RedisKey key)
    {
        if (IsDisposed(out var error))
        {
            return Result<int>.Error(error!);
        }

        var command = RedisCommandEnvelope.CreateListLengthCommand(key);

        var rawResult = await connection.SendCommandAsync(command).ConfigureAwait(false);

        var result = Parse(rawResult) switch
        {
            Ok<ParseResult> ok => EvaluateResult(ok.Value, ParseAndReturnInt),
            Error<ParseResult> err => Result<int>.Error(err.Message),
            _ => Result<int>.Error("An unexpected error occurred while attempting to parse the result of the LLEN command.")
        };

        return result;
    }

    public virtual async Task<Result<int>> RightPushAsync(RedisKey key, params string[] element)
    {
        if (IsDisposed(out var error))
        {
            return Result<int>.Error(error!);
        }

        var command = RedisCommandEnvelope.CreateRightPushCommand(key, element);

        var rawResult = await connection.SendCommandAsync(command).ConfigureAwait(false);

        var result = Parse(rawResult) switch
        {
            Ok<ParseResult> ok => EvaluateResult(ok.Value, ParseAndReturnInt),
            Error<ParseResult> err => Result<int>.Error(err.Message),
            _ => Result<int>.Error("An unexpected error occurred while attempting to parse the result of the RPUSH command.")
        };

        return result;
    }

    public virtual async Task<Result> SetAsync(RedisKey key, string value, CancellationToken cancellationToken = default)
    {
        if (IsDisposed(out var error))
        {
            return Result.Error(error!);
        }

        var command = RedisCommandEnvelope.CreateSetCommand(key, value);

        var rawResult = await connection.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);

        var result = Parse(rawResult) switch
        {
            Ok<ParseResult> => Result.Ok,
            Error<ParseResult> err => Result.Error(err.Message),
            _ => Result.Error("An unexpected error occurred while attempting to parse the result of the SET command.")
        };

        return result;
    }

    public virtual void Dispose()
    {
        pool.ReturnConnection(connection);

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
            var errorMessage = $"Redis returned an error while we were invoking `{callingMember}`. The error was: {result.ToString()}";

            // TODO: Probably need to address this one too...   
            RedisServerExceptionEvent?.Invoke(this, new RedisServerExceptionEventArgs(new RedisServerException(errorMessage, string.Empty)));

            return Result.Error(errorMessage);
        }

        return Result.Ok;
    }
    
    protected Result<T> EvaluateResult<T>(ParseResult result, Func<ParseResult, Result<T>> converter, [CallerMemberName] string callingMember = "") where T : notnull
    {
        var evalResult = EvaluateResult(result, callingMember);
        
        return evalResult switch
        {
            Ok => converter(result),
            Error err => Result<T>.Error(err.Message),
            _ => Result<T>.Error("Ran into an unexpected (and I'll be honest, I thought it was impossible) error while evaluating the result of a Redis command.")
        };
    }    

    protected static Result<int> ParseAndReturnInt(ParseResult result)
    {
        if (result.Type == ResultType.Integer)
        {
            return Result<int>.Ok(int.Parse(result.ToString()));
        }

        return Result<int>.Error($"We expected an integer type in the reply but got {result.Type.ToString()} instead.");
    }

    internal delegate void RedisServerExceptionEventHandler(object sender, RedisServerExceptionEventArgs args);
}