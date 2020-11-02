using System.Text;
using Xunit;

namespace ReadUs.Encoder.Tests
{
    public class EncoderTests
    {
        [Fact]
        public void CanCreateASingleBulkString()
        {
            var expectedByteArray = Encoding.Unicode.GetBytes("$6\r\nfoobar\r\n");

            var result = Encoder.Encode("foobar");

            Assert.Equal(expectedByteArray, result);
        }

        [Fact]
        public void CanCreateANullBulkString()
        {
            var expectedByteArray = Encoding.Unicode.GetBytes("$-1\r\n\r\n");

            var result = Encoder.Encode(null);

            Assert.Equal(expectedByteArray, result);
        }

        [Fact]
        public void CanCreateArrayOfBulkStrings()
        {
            var expectedByteArray = Encoding.Unicode.GetBytes("*2\r\n$3\r\nfoo\r\n$3\r\nbar\r\n");

            var result = Encoder.Encode("foo", "bar");

            Assert.Equal(expectedByteArray, result);
        }
    }
}
