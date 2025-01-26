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

    public virtual async Task<Result> SetMultipleAsync(KeyValuePair<RedisKey, string>[] keysAndValues,
        CancellationToken cacncellationToken = default)
    {
        CheckIfDisposed();

        var command = RedisCommandEnvelope.CreateSetMultipleCommand(keysAndValues);

        var rawResult = await connection.SendCommandAsync(command).ConfigureAwait(false);

        var result = Parse(rawResult);

        EvaluateResultAndThrow(result);

        return Result.Ok;
    }

    public virtual async Task<Result<string>> GetAsync(RedisKey key, CancellationToken cancellationToken = default)
    {
        CheckIfDisposed();

        var command = RedisCommandEnvelope.CreateGetCommand(key);

        var rawResult = await connection.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);

        var result = Parse(rawResult);

        EvaluateResultAndThrow(result);

        return Result<string>.Ok(result.ToString());
    }

    public virtual async Task<int> LeftPushAsync(RedisKey key, params string[] element)
    {
        CheckIfDisposed();

        var command = RedisCommandEnvelope.CreateLeftPushCommand(key, element);

        var rawResult = await _connection.SendCommandAsync(command).ConfigureAwait(false);

        var result = Parse(rawResult);

        EvaluateResultAndThrow(result);

        return ParseAndReturnInt(result);
    }

    public virtual async Task<int> ListLengthAsync(RedisKey key)
    {
        CheckIfDisposed();

        var command = RedisCommandEnvelope.CreateListLengthCommand(key);

        var rawResult = await _connection.SendCommandAsync(command).ConfigureAwait(false);

        var result = Parse(rawResult);

        EvaluateResultAndThrow(result);

        return ParseAndReturnInt(result);
    }

    public virtual async Task<int> RightPushAsync(RedisKey key, params string[] element)
    {
        CheckIfDisposed();

        var command = RedisCommandEnvelope.CreateRightPushCommand(key, element);

        var rawResult = await _connection.SendCommandAsync(command).ConfigureAwait(false);

        var result = Parse(rawResult);

        EvaluateResultAndThrow(result);

        return ParseAndReturnInt(result);
    }

    public virtual async Task SetAsync(RedisKey key, string value, CancellationToken cancellationToken = default)
    {
        CheckIfDisposed();

        var command = RedisCommandEnvelope.CreateSetCommand(key, value);

        var rawResult = await _connection.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);

        var result = Parse(rawResult);

        EvaluateResultAndThrow(result);
    }

    public virtual void Dispose()
    {
        _pool.ReturnConnection(_connection);

        _isDisposed = true;
    }

    internal event RedisServerExceptionEventHandler? RedisServerExceptionEvent;

    protected void CheckIfDisposed()
    {
        if (_isDisposed)
        {
            throw new RedisDatabaseDisposedException("This instance of `RedisDatabase` has already been disposed.");
        }
    }

    protected void EvaluateResultAndThrow(ParseResult result, [CallerMemberName] string callingMember = "")
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