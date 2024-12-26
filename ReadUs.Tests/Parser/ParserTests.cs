using ReadUs.Parser;
using Xunit;

namespace ReadUs.Tests.Parser;

public class ParserTests
{
    public class SimpleStrings
    {
        [Fact]
        public void Parse()
        {
            var result = ReadUs.Parser.Parser.Parse(SampleData.SimpleString);

            Assert.Equal(ResultType.SimpleString, result.Type);
            Assert.Equal("OK".ToCharArray(), result.Value);
        }
    }

    public class Errors
    {
        [Fact]
        public void Parse()
        {
            var result = ReadUs.Parser.Parser.Parse(SampleData.Error);

            Assert.Equal(ResultType.Error, result.Type);
            Assert.Equal("Error Message".ToCharArray(), result.Value);
        }
    }

    public class Integers
    {
        [Fact]
        public void Parse()
        {
            var result = ReadUs.Parser.Parser.Parse(SampleData.Integer);

            Assert.Equal(ResultType.Integer, result.Type);
            Assert.Equal("1000".ToCharArray(), result.Value);
        }
    }

    public class BulkStrings
    {
        [Fact]
        public void Parse()
        {
            var result = ReadUs.Parser.Parser.Parse(SampleData.BulkString);

            Assert.Equal(ResultType.BulkString, result.Type);
            Assert.Equal("foobar".ToCharArray(), result.Value);
        }

        [Fact]
        public void ParseEmpty()
        {
            var result = ReadUs.Parser.Parser.Parse(SampleData.EmptyBulkString);

            Assert.Equal(ResultType.BulkString, result.Type);
            Assert.Equal("".ToCharArray(), result.Value);
        }

        [Fact]
        public void ParseNull()
        {
            var result = ReadUs.Parser.Parser.Parse(SampleData.NullBulkString);

            Assert.Equal(ResultType.BulkString, result.Type);
            Assert.Null(result.Value);
        }
    }

    public class Arrays
    {
        [Fact]
        public void Parse()
        {
            var result = ReadUs.Parser.Parser.Parse(SampleData.Array);

            result.TryToArray(out var arrayValue);

            Assert.Equal(ResultType.Array, result.Type);

            Assert.Equal(2, arrayValue.Length);
            Assert.Equal("foo".ToCharArray(), arrayValue[0].Value);
            Assert.Equal("bar".ToCharArray(), arrayValue[1].Value);
        }

        [Fact]
        public void ParseMixed()
        {
            var result = ReadUs.Parser.Parser.Parse(SampleData.MixedArray);

            result.TryToArray(out var arrayValue);

            Assert.Equal(ResultType.Array, result.Type);

            Assert.Equal(5, arrayValue.Length);

            Assert.Equal("1".ToCharArray(), arrayValue[0].Value);
            Assert.Equal("2".ToCharArray(), arrayValue[1].Value);
            Assert.Equal("3".ToCharArray(), arrayValue[2].Value);
            Assert.Equal("4".ToCharArray(), arrayValue[3].Value);
            Assert.Equal("foobar".ToCharArray(), arrayValue[4].Value);
        }

        [Fact]
        public void ParseArrayOfArrays()
        {
            var result = ReadUs.Parser.Parser.Parse(SampleData.ArrayOfArrays);

            result.TryToArray(out var arrayValue);

            Assert.Equal(ResultType.Array, result.Type);

            Assert.Equal(2, arrayValue.Length);
            Assert.Equal(ResultType.Array, arrayValue[0].Type);

            arrayValue[0].TryToArray(out var subArrayValue);

            Assert.Equal(3, subArrayValue.Length);
            Assert.Equal("1".ToCharArray(), subArrayValue[0].Value);
            Assert.Equal("2".ToCharArray(), subArrayValue[1].Value);
            Assert.Equal("3".ToCharArray(), subArrayValue[2].Value);
        }

        [Fact]
        public void ArrayWithNull()
        {
            var result = ReadUs.Parser.Parser.Parse(SampleData.ArrayWithNull);

            result.TryToArray(out var arrayValue);

            Assert.Equal(ResultType.Array, result.Type);
            Assert.Equal(3, arrayValue.Length);

            Assert.Null(arrayValue[1].Value);
        }

        [Fact]
        public void RoleResponseFromPrimary()
        {
            var result = ReadUs.Parser.Parser.Parse(SampleData.RoleResponseFromPrimary);

            result.TryToArray(out var arrayValue);

            Assert.Equal(ResultType.Array, result.Type);
            Assert.Equal(5, arrayValue.Length);

            Assert.Equal("slave", arrayValue[0].ToString());
            Assert.Equal("192.168.86.40", arrayValue[1].ToString());
            Assert.Equal("7005", arrayValue[2].ToString());
            Assert.Equal("connected", arrayValue[3].ToString());
            Assert.Equal("1291892", arrayValue[4].ToString());
        }
    }
}