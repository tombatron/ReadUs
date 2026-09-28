using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ReadUs.Protocol;

namespace ReadUs.Scripting;

/// <summary>
/// The low-level shape <see cref="RedisScript.EvaluateAsync(RedisCommandExecutor, ReadOnlyMemory{byte}[], ReadOnlyMemory{byte}[], CancellationToken)"/>
/// actually needs — kept as a plain delegate rather than taking
/// <see cref="IRedisClient"/> directly, so <see cref="RedisScript"/> itself stays
/// decoupled from any particular client abstraction. <c>Scripting.RedisScriptClientExtensions.EvaluateAsync</c>
/// is the one adapter from <see cref="IRedisClient"/>'s <c>ExecuteAsync</c> onto this
/// shape that every caller actually uses; see its own remarks and
/// docs/design/state-machines.md, "Recorded during implementation of IRedisClient
/// unification," for why it used to be three separate adapters.
/// </summary>
public delegate ValueTask<RedisResult> RedisCommandExecutor(ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken);

/// <summary>
/// A Lua script (project spec §10: "EVAL/EVALSHA/FUNCTION support with automatic
/// script-cache-miss fallback"), with its SHA1 computed once at construction — no round
/// trip to the server (e.g. via <c>SCRIPT LOAD</c>) is needed to learn it, since Redis's
/// own hashing is just SHA1 over the script's exact source bytes.
///
/// <see cref="EvaluateAsync"/> always tries <c>EVALSHA</c> first (cheaper: no script
/// body on the wire) and falls back to <c>EVAL</c> exactly once, only on a <c>NOSCRIPT</c>
/// error reply — which also causes the server to cache the script under its SHA1 for
/// next time. Deliberately keeps no client-side "is it loaded" cache: the server's own
/// script cache can be evicted independently of this client (<c>SCRIPT FLUSH</c>, a
/// restart), so a client-side "definitely loaded" assumption could go stale in a way
/// that would silently break every call after that point. Optimistic-EVALSHA +
/// catch-NOSCRIPT is the only version of this that can't go stale, matching how
/// StackExchange.Redis's own <c>LuaScript</c> handles the identical problem.
///
/// <c>EVAL</c>/<c>EVALSHA</c> already have working generated typed methods on
/// <c>RedisClient</c> (<c>ReadUs.Generated.ScriptingCommands</c>) — this type's actual
/// value is purely the fallback automation and the self-computed SHA1, not new wire
/// plumbing (unlike <c>ReadUs.PubSub.RedisSubscriber</c>, which needed real new
/// connection-layer machinery). Not built: an equivalent wrapper for
/// <c>FUNCTION</c>/<c>FCALL</c> — unlike a script, a function is explicitly loaded once
/// via the already-generated <c>FunctionLoadAsync</c> and then called by name via
/// <c>FcallAsync</c>/<c>FcallRoAsync</c>, with no cache-miss/fallback wrinkle at all to
/// automate.
/// </summary>
public sealed class RedisScript
{
    private static readonly byte[] EvalShaCommand = "EVALSHA"u8.ToArray();
    private static readonly byte[] EvalCommand = "EVAL"u8.ToArray();

    public RedisScript(string body)
    {
        Body = body;
        Sha1 = ComputeSha1(body);
    }

    public string Body { get; }

    public string Sha1 { get; }

    /// <summary>
    /// Evaluates this script against <paramref name="executor"/>. <paramref name="keys"/>
    /// becomes Lua's <c>KEYS</c> table and <paramref name="args"/> becomes <c>ARGV</c>,
    /// matching <c>EVAL</c>'s own <c>numkeys key [key ...] arg [arg ...]</c> wire shape —
    /// the caller never passes <c>numkeys</c> directly, since it's just
    /// <c>keys.Length</c>.
    /// </summary>
    public async ValueTask<RedisResult> EvaluateAsync(RedisCommandExecutor executor, ReadOnlyMemory<byte>[] keys, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken = default)
    {
        var sha1Bytes = Encoding.ASCII.GetBytes(Sha1);
        var reply = await executor(EvalShaCommand, BuildArgs(sha1Bytes, keys, args), cancellationToken).ConfigureAwait(false);

        if (reply.IsError && reply.AsString().StartsWith("NOSCRIPT", StringComparison.Ordinal))
        {
            var bodyBytes = Encoding.UTF8.GetBytes(Body);
            return await executor(EvalCommand, BuildArgs(bodyBytes, keys, args), cancellationToken).ConfigureAwait(false);
        }

        return reply;
    }

    private static ReadOnlyMemory<byte>[] BuildArgs(byte[] scriptOrSha, ReadOnlyMemory<byte>[] keys, ReadOnlyMemory<byte>[] args)
    {
        var wireArgs = new ReadOnlyMemory<byte>[2 + keys.Length + args.Length];
        wireArgs[0] = scriptOrSha;
        wireArgs[1] = Encoding.ASCII.GetBytes(keys.Length.ToString(CultureInfo.InvariantCulture));
        keys.CopyTo(wireArgs, 2);
        args.CopyTo(wireArgs, 2 + keys.Length);
        return wireArgs;
    }

#pragma warning disable CA5350 // Not a security use: EVALSHA's script identifier is defined by the Redis protocol itself to be the SHA1 of the script body, not a choice this client makes.
    private static string ComputeSha1(string body)
    {
        Span<byte> hash = stackalloc byte[SHA1.HashSizeInBytes];
        SHA1.HashData(Encoding.UTF8.GetBytes(body), hash);
        return Convert.ToHexStringLower(hash);
    }
#pragma warning restore CA5350
}
