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

public abstract class RedisDatabase(RedisConnectionPool pool) : IRedisDatabase
{
    public virtual async Task<Result<byte[]>> Execute(RedisCommandEnvelope command, CancellationToken cancellationToken = default)
    {
        var connection = await pool.GetConnection();

        if (!connection.IsConnected)
        {
            await connection.ConnectAsync(cancellationToken);
        }

        try
        {
            return await connection.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            pool.ReturnConnection(connection);
        }
    }
    
    public abstract Task<Result<BlockingPopResult>> BlockingLeftPopAsync(params RedisKey[] keys);

    public abstract Task<Result<BlockingPopResult>> BlockingLeftPopAsync(TimeSpan timeout, params RedisKey[] keys);

    public abstract Task<Result<BlockingPopResult>> BlockingRightPopAsync(params RedisKey[] keys);

    public abstract Task<Result<BlockingPopResult>> BlockingRightPopAsync(TimeSpan timeout, params RedisKey[] keys);
    
    public abstract Task<Result> SelectAsync(int databaseId, CancellationToken cancellationToken = default);

    public virtual async Task<Result> SetMultipleAsync(KeyValuePair<RedisKey, string>[] keysAndValues, CancellationToken cancellationToken = default)
    {
        var command = RedisCommandEnvelope.CreateSetMultipleCommand(keysAndValues);

        var rawResult = await Execute(command, cancellationToken).ConfigureAwait(false);

        var result = Parse(rawResult) switch
        {
            Ok<ParseResult> ok => EvaluateResult(ok.Value),
            Error<ParseResult> err => Result.Error(err.Message),
            _ => Result.Error("An unexpected error occurred while attempting to parse the result of the SET command.")
        };

        return result;
    }

    public virtual async Task<Result<int>> Publish(string channel, string message, CancellationToken cancellationToken = default)
    {
        var command = RedisCommandEnvelope.CreatePublishCommand(channel, message);

        var result = await Execute(command, cancellationToken).ConfigureAwait(false);

        return Parse(result) switch
        {
            Ok<ParseResult> ok => EvaluateResult(ok.Value, ParseAndReturnInt),
            Error<ParseResult> err => Result<int>.Error(err.Message),
            _ => Result<int>.Error("An unexpected error occurred while attempting to parse the result of the PUBLISH command.")
        };
    }

    /// <summary>
    /// Subscribe to a Redis Pub/Sub channel.
    /// </summary>
    /// <param name="channel"></param>
    /// <param name="messageHandler">`T` is the message.</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<RedisSubscription> Subscribe(string channel, Action<string> messageHandler, CancellationToken cancellationToken = default) =>
        await Subscribe([channel], (c,m) => messageHandler(m), cancellationToken);
    
    /// <summary>
    /// Subscribe to one or more Redis Pub/Sub channels.
    /// </summary>
    /// <param name="channels"></param>
    /// <param name="messageHandler">`T1` will be the channel the message came in from, `T2` will be the content of the message.</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<RedisSubscription> Subscribe(string[] channels, Action<string, string> messageHandler, CancellationToken cancellationToken = default)
    {
        var command = RedisCommandEnvelope.CreateSubscribeCommand(channels);

        var subscription = new RedisSubscription(pool, messageHandler);

        await subscription.Initialize(command, cancellationToken);

        return subscription;
    }

    /// <summary>
    /// Subscribe to a series of Redis Pub/Sub channels using a pattern instead of a specific channel name.
    /// </summary>
    /// <param name="channelPattern"></param>
    /// <param name="messageHandler">`T1` will be the channel pattern the message came in from, `T2` will be the specific channel, and `T3` will be the message.</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="NotImplementedException"></exception>
    public async Task<RedisSubscription> SubscribeWithPattern(string channelPattern, Action<string, string, string> messageHandler, CancellationToken cancellationToken = default) =>
        await SubscribeWithPattern([channelPattern], messageHandler, cancellationToken);
    
    /// <summary>
    /// Subscribe to a series of patterns of Redis Pub/Sub channels using multiple patterns instead of specific channel names.
    /// </summary>
    /// <param name="channelPatterns"></param>
    /// <param name="messageHandler">`T1` will be the channel pattern the message came in from, `T2` will be the specific channel, and `T3` will be the message.</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<RedisSubscription> SubscribeWithPattern(string[] channelPatterns, Action<string, string, string> messageHandler, CancellationToken cancellationToken = default)
    {
        var command = RedisCommandEnvelope.CreatePatternSubscribeCommand(channelPatterns);
        
        var subscription = new RedisSubscription(pool, messageHandler);
        
        await subscription.Initialize(command, cancellationToken);

        return subscription;
    }

    public virtual async Task<Result<string>> GetAsync(RedisKey key, CancellationToken cancellationToken = default)
    {
        var command = RedisCommandEnvelope.CreateGetCommand(key);

        var rawResult = await Execute(command, cancellationToken).ConfigureAwait(false);

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
        var command = RedisCommandEnvelope.CreateLeftPushCommand(key, element);

        var rawResult = await Execute(command).ConfigureAwait(false);

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
        var command = RedisCommandEnvelope.CreateListLengthCommand(key);

        var rawResult = await Execute(command).ConfigureAwait(false);

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
        var command = RedisCommandEnvelope.CreateRightPushCommand(key, element);

        var rawResult = await Execute(command).ConfigureAwait(false);

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
        var command = RedisCommandEnvelope.CreateSetCommand(key, value);

        var rawResult = await Execute(command, cancellationToken).ConfigureAwait(false);

        var result = Parse(rawResult) switch
        {
            Ok<ParseResult> => Result.Ok,
            Error<ParseResult> err => Result.Error(err.Message),
            _ => Result.Error("An unexpected error occurred while attempting to parse the result of the SET command.")
        };

        return result;
    }

    internal event RedisServerExceptionEventHandler? RedisServerExceptionEvent;

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