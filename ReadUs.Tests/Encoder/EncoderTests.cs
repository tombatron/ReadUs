﻿using System.Text;
using Xunit;

namespace ReadUs.Tests.Encoder
{
    using ReadUs.Encoder;

    public class EncoderTests
    {
        [Fact]
        public void CanCreateASingleBulkString()
        {
            var expectedByteArray = Encoding.ASCII.GetBytes("$6\r\nfoobar\r\n");

            var result = Encoder.Encode("foobar");

            var encodedResult = Encoding.ASCII.GetString(result);

            Assert.Equal(expectedByteArray, result);
        }

        [Fact]
        public void CanCreateANullBulkString()
        {
            var expectedByteArray = Encoding.ASCII.GetBytes("$-1\r\n\r\n");

            var result = Encoder.Encode(null);

            Assert.Equal(expectedByteArray, result);
        }

        [Fact]
        public void CanCreateArrayOfBulkStrings()
        {
            var expectedByteArray = Encoding.ASCII.GetBytes("*2\r\n$3\r\nfoo\r\n$3\r\nbar\r\n");

            var result = Encoder.Encode("foo", "bar");

            Assert.Equal(expectedByteArray, result);
        }
    }
}
