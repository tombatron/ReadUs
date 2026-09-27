using ReadUs.Protocol;

namespace ReadUs.Extensions.Hashes;

/// <summary>
/// Implemented by the compiler-generated companion partial of every
/// <see cref="RedisHashModelAttribute"/> type — never by hand. A static-abstract
/// member (C# 11+) rather than a constructor-taking factory delegate or reflection:
/// <see cref="HashRedisClientExtensions.GetHashAsync{T}"/> calls
/// <c>T.FromHash(reply)</c> directly, resolved entirely at compile time, no runtime
/// type inspection anywhere in the path.
/// </summary>
public interface IRedisHashModel<TSelf> where TSelf : IRedisHashModel<TSelf>
{
    IReadOnlyList<ReadOnlyMemory<byte>> ToHashFields();

    static abstract TSelf FromHash(RedisResult hashReply);
}
