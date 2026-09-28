using ReadUs.Protocol;
using ReadUs.Scripting;

namespace ReadUs.Cluster;

/// <summary>
/// Thin adapter from <see cref="RedisScript"/>'s executor-delegate shape onto
/// <see cref="ClusterClient"/>. Routing note: <c>EVAL</c>/<c>EVALSHA</c>'s keys are
/// staticaly resolvable in principle (<c>numkeys</c> plus a fixed-position key list),
/// but the command-table generator currently marks them <c>HasUnknownKeys = true</c> —
/// their key-spec shape isn't the simple first/last/step form the generator's key
/// extractor supports yet — so a Cluster script call round-robins to an arbitrary
/// master on the first attempt rather than routing directly by key. Same already-
/// accepted, already-documented fallback as <c>SORT</c>'s identical `HasUnknownKeys`
/// case (docs/design/state-machines.md §5, "Recorded during §13 step 5 implementation
/// (Cluster support)"): a wrong first guess surfaces as an ordinary <c>MOVED</c> reply,
/// which <see cref="ClusterClient"/>'s existing redirect-following transparently
/// retries. Correct, just not optimally routed on the first try.
/// </summary>
public static class ClusterScriptingExtensions
{
    public static ValueTask<RedisResult> EvaluateAsync(this RedisScript script, ClusterClient client, ReadOnlyMemory<byte>[] keys, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken = default) =>
        script.EvaluateAsync(client.ExecuteAsync, keys, args, cancellationToken);
}
