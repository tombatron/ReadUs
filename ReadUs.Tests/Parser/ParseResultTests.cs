using ReadUs.Parser;
using Xunit;

namespace ReadUs.Tests.Parser;

public class ParseResultTests
{
    public class TryToArrayWill
    {
        [Fact]
        public void ReturnFalseIfArrayIsNull()
        {
            var parseResult = new ParseResult(ResultType.SimpleString, "Hello".ToCharArray(), 4);

            Assert.False(parseResult.TryToArray(out _));
        }

        [Fact]
        public void ReturnEmptyArrayIfNotArray()
        {
            var parseResult = new ParseResult(ResultType.SimpleString, "Hello".ToCharArray(), 4);

            var result = parseResult.TryToArray(out var arrayResult);

            Assert.False(result);
            Assert.Empty(arrayResult);
        }

        [Fact]
        public void ReturnTrueIfArrayIsNotNull()
        {
            var firstItem = new ParseResult(ResultType.Integer, "1".ToCharArray(), 1);
            var secondItem = new ParseResult(ResultType.SimpleString, "Hello".ToCharArray(), 4);

            var parseResult = new ParseResult(ResultType.Array, null, 0, new[] { firstItem, secondItem });

            Assert.True(parseResult.TryToArray(out _));
        }

        [Fact]
        public void ReturnArrayIfItsAnArray()
        {
            var firstItem = new ParseResult(ResultType.Integer, "1".ToCharArray(), 1);
            var secondItem = new ParseResult(ResultType.SimpleString, "Hello".ToCharArray(), 4);

            var parseResult = new ParseResult(ResultType.Array, null, 0, new[] { firstItem, secondItem });

            var result = parseResult.TryToArray(out var arrayResult);

            Assert.True(result);
            Assert.NotEmpty(arrayResult);
        }
    }

    public class ToStringWill
    {
        [Fact]
        public void ProjectValueToString()
        {
            var firstItem = new ParseResult(ResultType.Integer, "1".ToCharArray(), 1);

            Assert.Equal("1", firstItem.ToString());
        }

        [Fact]
        public void ProjectEmptyStringIfValueIsNull()
        {
            var firstItem = new ParseResult(ResultType.Integer, "1".ToCharArray(), 1);
            var secondItem = new ParseResult(ResultType.SimpleString, "Hello".ToCharArray(), 4);

            var parseResult = new ParseResult(ResultType.Array, null, 0, new[] { firstItem, secondItem });

            Assert.Empty(parseResult.ToString());
        }
    }
}