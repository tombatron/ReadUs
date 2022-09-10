using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using static ReadUs.Encoder.Encoder;

namespace ReadUs.Tests
{
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
    }
}