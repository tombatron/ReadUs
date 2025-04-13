using System;
using System.Text;
using Xunit;
using static ReadUs.Encoder.Encoder;

namespace ReadUs.Tests;

public class RedisCommandEnvelopeTests
{
    public class AllKeysInSingleSlotWillReturn
    {
        [Fact]
        public void FalseIfAllKeysArentInSameSlot()
        {
            var keys = new RedisKey[]
            {
                "82167180227c44189b70c16a866c51c7",
                "47cc48cbd0644331a30418334f1d2351",
                "27b9eae239ea40dba29ac3c6d5b863b7"
            };

            var command = new RedisCommandEnvelope(default, default, keys, default);

            Assert.False(command.AllKeysInSingleSlot);
        }

        [Fact]
        public void TrueIfAllKeysAreInSameSlot()
        {
            var keys = new RedisKey[]
            {
                "fc11bb8af5b440cfb13fd08a143a007a",
                "d32334dafd114fd08c55aed91c148d66",
                "1c9b65bb7f8c4a5080069be32023a800"
            };

            var command = new RedisCommandEnvelope(default, default, keys, default);

            Assert.True(command.AllKeysInSingleSlot);
        }
    }

    public class ToByteArrayWill
    {
        [Fact]
        public void WillAppendCommandNameIfPresent()
        {
            var command = new RedisCommandEnvelope("TestCommand", null, null, null);

            var expected = Encode("TestCommand");

            Assert.Equal(expected, command.ToByteArray());
        }

        [Fact]
        public void WillAppendSubcommandNameIfPresent()
        {
            var command = new RedisCommandEnvelope(null, "TestSubCommand", null, null);

            var expected = Encode("TestSubCommand");

            Assert.Equal(expected, command.ToByteArray());
        }

        [Fact]
        public void WillAppendCommandNameAndSubCommandNameIfPresent()
        {
            var command = new RedisCommandEnvelope("TestCommand", "TestSubCommand", null, null);

            var expected = Encode("TestCommand", "TestSubCommand");

            Assert.Equal(expected, command.ToByteArray());
        }

        [Fact]
        public void WillAppendItemsIfPresent()
        {
            var command = new RedisCommandEnvelope(null, null, null, null, "Hello", "World");

            var expected = Encode("Hello", "World");

            Assert.Equal(expected, command.ToByteArray());
        }

        [Fact]
        public void WillAppendCommandNameAndItemsIfPresent()
        {
            var command = new RedisCommandEnvelope("TestCommand", null, null, null, "Hello", "World");

            var expected = Encode("TestCommand", "Hello", "World");

            Assert.Equal(expected, command.ToByteArray());
        }

        [Fact]
        public void WillAppendCommandNameSubCommandNameAndItemsIfPresent()
        {
            var command = new RedisCommandEnvelope("TestCommand", "TestSubCommand", null, null, "Hello", "World");

            var expected = Encode("TestCommand", "TestSubCommand", "Hello", "World");

            Assert.Equal(expected, command.ToByteArray());
        }
    }

    public class ImplicitConversionToByteArray
    {
        [Fact]
        public void WillSucceed()
        {
            byte[] command = new RedisCommandEnvelope("TestCommand", "TestSubCommand", null, null, "Hello", "World");

            var expected = Encode("TestCommand", "TestSubCommand", "Hello", "World");

            Assert.Equal(expected, command);
        }
    }

    public class ImplicitConversionToMemorySpan
    {
        [Fact]
        public void WillSucceed()
        {
            ReadOnlyMemory<byte> command =
                new RedisCommandEnvelope("TestCommand", "TestSubCommand", null, null, "Hello", "World");

            ReadOnlyMemory<byte> expected = Encode("TestCommand", "TestSubCommand", "Hello", "World");

            Assert.Equal(expected.ToArray(), command.ToArray());
        }
    }

    public class MultipleSubCommands
    {
        [Fact]
        public void AreCorrectlyHandled()
        {
            ReadOnlyMemory<byte> command = new RedisCommandEnvelope("SUBSCRIBE", ["TEST_CHANNEL", "ANOTHER_CHANNEL"], null, null, false);

            ReadOnlyMemory<byte> expected = Encode("SUBSCRIBE", "TEST_CHANNEL", "ANOTHER_CHANNEL");

            Assert.Equal(expected.ToArray(), command.ToArray());
        }
    }
}