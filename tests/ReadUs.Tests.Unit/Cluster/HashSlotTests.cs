using System.Text;
using ReadUs.Cluster.Routing;

namespace ReadUs.Tests.Unit.Cluster;

/// <summary>
/// Live-server cross-checks against real <c>CLUSTER KEYSLOT</c> replies live in
/// ReadUs.Tests.Integration.ClusterClientTests; these are the fast, deterministic
/// checks that don't need one.
/// </summary>
public class HashSlotTests
{
    [Fact]
    public void ProductionSlotComputationMatchesAnIndependentReferenceCrc16Implementation()
    {
        // Cross-checks the production HashSlot.Compute (backed by the internal Crc16
        // class) against a second, independently-written CRC-16/XMODEM implementation
        // in this test — not the same code, so this can't pass merely because both
        // sides share the same bug. Also anchors that reference implementation itself
        // to the standard XMODEM check value (0x31C3 for "123456789"), confirming
        // ReadUs uses the exact variant Redis's own src/crc16.c does (project spec §5).
        var referenceCrc = ComputeCrc16Xmodem("123456789"u8);
        Assert.Equal(0x31C3, referenceCrc);

        var expectedSlot = referenceCrc % HashSlot.Count;
        Assert.Equal(expectedSlot, HashSlot.Compute("123456789"u8));

        foreach (var key in new[] { "foo", "user1000", "readus", "{tag}key" })
        {
            var bytes = Encoding.UTF8.GetBytes(key);
            var expected = ComputeCrc16Xmodem(HashSlot.ExtractHashTag(bytes)) % HashSlot.Count;
            Assert.Equal(expected, HashSlot.Compute(bytes));
        }
    }

    [Theory]
    [InlineData("{user1000}.following")]
    [InlineData("{user1000}.followers")]
    public void KeysSharingAHashTagMapToTheSameSlotAsTheBareTag(string taggedKey)
    {
        var taggedSlot = HashSlot.Compute(Encoding.UTF8.GetBytes(taggedKey));
        var bareSlot = HashSlot.Compute("user1000"u8);

        Assert.Equal(bareSlot, taggedSlot);
    }

    [Fact]
    public void AnEmptyHashTagFallsBackToHashingTheWholeKey()
    {
        // "foo{}{bar}": the first '{' is immediately followed by '}' — an empty tag —
        // so per the spec this must fall back to the whole key, not "" or "{bar}".
        var key = "foo{}{bar}"u8;
        var slot = HashSlot.Compute(key);

        Assert.Equal(HashSlot.Compute(key), slot); // self-consistent
        Assert.NotEqual(HashSlot.Compute("bar"u8), slot);
        Assert.True(HashSlot.ExtractHashTag(key).SequenceEqual(key));
    }

    [Fact]
    public void AMissingClosingBraceFallsBackToHashingTheWholeKey()
    {
        var key = "foo{bar"u8;
        Assert.True(HashSlot.ExtractHashTag(key).SequenceEqual(key));
    }

    [Fact]
    public void NestedBracesUseOnlyUpToTheFirstClosingBrace()
    {
        // The documented edge case: "foo{{bar}}zap" hash-tags on "{bar" (the first '{'
        // through the first '}' after it), not "bar" and not "{bar}".
        var key = "foo{{bar}}zap"u8;
        var tag = HashSlot.ExtractHashTag(key);

        Assert.True(tag.SequenceEqual("{bar"u8));
        Assert.Equal(HashSlot.Compute("{bar"u8), HashSlot.Compute(key));
    }

    [Fact]
    public void AKeyWithNoBracesHashesOnTheWholeKey()
    {
        var key = "plainkey"u8;
        Assert.True(HashSlot.ExtractHashTag(key).SequenceEqual(key));
    }

    [Fact]
    public void SlotIsAlwaysWithinRange()
    {
        for (var i = 0; i < 1000; i++)
        {
            var slot = HashSlot.Compute(Encoding.UTF8.GetBytes($"key-{i}"));
            Assert.InRange(slot, 0, HashSlot.Count - 1);
        }
    }

    private static ushort ComputeCrc16Xmodem(ReadOnlySpan<byte> data)
    {
        // A second, independent implementation (not calling into Crc16 itself) so this
        // test can't pass merely because both sides share the same bug.
        ushort crc = 0;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
        }

        return crc;
    }
}
