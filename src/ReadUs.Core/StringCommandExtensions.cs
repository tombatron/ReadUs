using System.Text;
using ReadUs.Protocol;

namespace ReadUs;

/// <summary>
/// A <see langword="string"/>-based overload of <see cref="IRedisClient"/>'s raw escape
/// hatch (project spec §3), for the common case of a literal, known-text command —
/// ergonomic at the cost of a runtime UTF-8 encode per call, unlike the byte-based
/// overload every generated typed method and this one both ultimately call. Kept
/// deliberately off the generated typed surface (`SetAsync`/`GetAsync`/...) and off
/// <see cref="IRedisClient"/> itself — see docs/design/state-machines.md's "Recorded
/// during implementation of the string-argument analyzer/code fix" for the full scope
/// reasoning.
///
/// A companion Roslyn analyzer/code fix (<c>ReadUs.SourceGenerators</c>) flags a call to
/// either method here whose arguments are all compile-time string literals and offers to
/// rewrite it to the byte-based overload with <c>u8.ToArray()</c> literals — convenient
/// by default, one step from optimal wherever the compiler can actually do that rewrite.
/// </summary>
public static class StringCommandExtensions
{
    public static ValueTask<RedisResult> ExecuteAsync(this IRedisClient client, string commandName, string[]? args = null, CancellationToken cancellationToken = default) =>
        client.ExecuteAsync(Encoding.UTF8.GetBytes(commandName), ToByteArgs(args), cancellationToken);

    public static ValueTask<RedisResult> ExecuteBlockingAsync(this IRedisClient client, string commandName, string[]? args = null, CancellationToken cancellationToken = default) =>
        client.ExecuteBlockingAsync(Encoding.UTF8.GetBytes(commandName), ToByteArgs(args), cancellationToken);

    private static ReadOnlyMemory<byte>[] ToByteArgs(string[]? args)
    {
        if (args is null || args.Length == 0)
        {
            return [];
        }

        var result = new ReadOnlyMemory<byte>[args.Length];
        for (var i = 0; i < args.Length; i++)
        {
            result[i] = Encoding.UTF8.GetBytes(args[i]);
        }

        return result;
    }
}
