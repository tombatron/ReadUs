using ReadUs.Exceptions;
using ReadUs.Parser;
using ReadUs.ResultModels;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using static ReadUs.Encoder.Encoder;
using static ReadUs.ParameterUtilities;
using static ReadUs.Parser.Parser;
using static ReadUs.RedisCommandNames;

namespace ReadUs
{
    public abstract class RedisDatabase : IRedisDatabase
    {
        protected readonly IRedisConnection _connection;
        protected readonly IRedisConnectionPool _pool;

        public RedisDatabase(IRedisConnection connection, IRedisConnectionPool pool)
        {
            _connection = connection;
            _pool = pool;
        }

        public abstract Task<BlockingPopResult> BlockingLeftPopAsync(params RedisKey[] keys);

        public abstract Task<BlockingPopResult> BlockingLeftPopAsync(TimeSpan timeout, params RedisKey[] keys);

        public abstract Task<BlockingPopResult> BlockingRightPopAsync(params RedisKey[] keys);

        public abstract Task<BlockingPopResult> BlockingRightPopAsync(TimeSpan timeout, params RedisKey[] keys);

        public abstract Task SelectAsync(int databaseId, CancellationToken cancellationToken = default);

        public virtual async Task SetMultipleAsync(KeyValuePair<RedisKey, string>[] keysAndValues, CancellationToken cacncellationToken = default)
        {
            CheckIfDisposed();

            var command = RedisCommandEnvelope.CreateSetMultipleCommand(keysAndValues);

            var rawResult = await _connection.SendCommandAsync(command).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);
        }

        public virtual async Task<string> GetAsync(RedisKey key, CancellationToken cancellationToken = default)
        {
            CheckIfDisposed();

            var command = RedisCommandEnvelope.CreateGetCommand(key);

            var rawResult = await _connection.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return result.ToString();
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
            
            var parameters = CombineParameters(RightPush, key, element);

            var rawCommand = Encode(parameters);

            var rawResult = await _connection.SendCommandAsync(key, rawCommand).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);

            return ParseAndReturnInt(result);
        }

        public virtual async Task SetAsync(RedisKey key, string value, CancellationToken cancellationToken = default)
        {
            CheckIfDisposed();
            
            var rawCommand = Encode(Set, key, value);

            var rawResult = await _connection.SendCommandAsync(key, rawCommand, cancellationToken).ConfigureAwait(false);

            var result = Parse(rawResult);

            EvaluateResultAndThrow(result);
        }

        private bool _isDisposed = false;

        public virtual void Dispose()
        {
            _pool.ReturnConnection(_connection);

            _isDisposed = true;
        }

        protected void CheckIfDisposed()
        {
            if (_isDisposed)
            {
                throw new RedisDatabaseDisposedException("This instance of `RedisDatabase` has already been disposed.");
            }
        }

        protected static void EvaluateResultAndThrow(ParseResult result, [CallerMemberName] string callingMember = "")
        {
            if (result.Type == ResultType.Error)
            {
                throw new RedisServerException($"Redis returned an error while we were invoking `{callingMember}`. The error was: {result.ToString()}");
            }
        }

        protected static int ParseAndReturnInt(ParseResult result, [CallerMemberName] string callingMember = "")
        {
            if (result.Type == ResultType.Integer)
            {
                return int.Parse(result.ToString());
            }
            else
            {
                // TODO: Need a real custom exception here. 
                throw new Exception($"We expected an integer type in the reply but got {result.Type.ToString()} instead.");
            }
        }
    }
}