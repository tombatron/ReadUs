using ReadUs.Parser;
using ReadUs.ResultModels;
using Xunit;

namespace ReadUs.Tests.ResultModels;

public class BlockingPopResultTests
{
    [Fact]
    public void CanCastParseResultAsBlockingPopResult()
    {
        var key = new ParseResult(ResultType.SimpleString, "key".ToCharArray(), 3);
        var value = new ParseResult(ResultType.SimpleString, "value".ToCharArray(), 5);

        var parseResult = new ParseResult(ResultType.Array, null, 0, new[] { key, value });

        var castResult = (BlockingPopResult)parseResult;

        Assert.Equal("key", castResult.ListKey);
        Assert.Equal("value", castResult.Value);
    }
}