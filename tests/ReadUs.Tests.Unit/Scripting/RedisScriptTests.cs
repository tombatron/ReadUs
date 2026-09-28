using ReadUs.Scripting;

namespace ReadUs.Tests.Unit.Scripting;

/// <summary>
/// <see cref="RedisScript.Sha1"/> is a pure function — no server needed. Checked
/// against the SHA1 of "abc" as independently computed by both <c>sha1sum</c> and
/// Python's <c>hashlib</c> (not a value re-derived from this same implementation, and
/// not trusted from memory — an initial hand-recalled value for this exact test vector
/// turned out to be one character short when actually checked), so a fundamental
/// algorithm bug (wrong hash, wrong encoding, byte-order mistake) would actually be
/// caught rather than silently agreeing with itself.
/// </summary>
public class RedisScriptTests
{
    [Fact]
    public void Sha1MatchesTheStandardTestVector()
    {
        var script = new RedisScript("abc");

        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", script.Sha1);
    }

    [Fact]
    public void Sha1IsLowercaseHex()
    {
        var script = new RedisScript("return 1");

        Assert.Equal(40, script.Sha1.Length);
        Assert.Equal(script.Sha1, script.Sha1.ToLowerInvariant());
    }
}
