using ReadUs.ResultModels;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using static ReadUs.ResultModels.ClusterSlots;

namespace ReadUs.Tests.ResultModels;

public class ClusterSlotsTests
{
    public class OwnedSlotsContains
    {
        [Fact]
        public void AllOwnedSlots()
        {
            var clusterSlots = new ClusterSlots(SlotRange.Create(100, 200), SlotRange.Create(500, 500), SlotRange.Create(1000, 2000));

            var ownedSlots = clusterSlots.OwnedSlots.ToList();

            Assert.True(Enumerable.Range(100, 101).All(x => ownedSlots.Contains(x)));
            Assert.Contains(500, ownedSlots);
            Assert.True(Enumerable.Range(1000, 1001).All(x => ownedSlots.Contains(x)));
        }
    }

    public class ConstainsSlotWill
    {
        [Theory]
        [MemberData(nameof(PositiveCases))]
        public void ReturnTrueIfSlotIsInRange(ClusterSlots testSlots, uint expectedSlot)
        {
            Assert.True(testSlots.ContainsSlot(expectedSlot));
        }

        [Theory]
        [MemberData(nameof(NegativeCases))]
        public void ReturnFalseIfSlotIsNotInRange(ClusterSlots testSlots, uint missingSlot)
        {
            Assert.False(testSlots.ContainsSlot(missingSlot));
        }

        public static IEnumerable<object[]> PositiveCases = new[]
        {
            new object[] { new ClusterSlots(SlotRange.Create(0, 1000)), 500 },
            new object[] { new ClusterSlots(SlotRange.Create(1001, 1001)), 1001 },
            new object[] { new ClusterSlots(SlotRange.Create(100, 200), SlotRange.Create(500, 500), SlotRange.Create(1000, 2000)), 500 }
        };

        public static IEnumerable<object[]> NegativeCases = new[]
        {
            new object[] { new ClusterSlots(SlotRange.Create(0, 1000)), 1001 },
            new object[] { new ClusterSlots(SlotRange.Create(0, 1000), SlotRange.Create(1001, 1001)), 1002 },
            new object[] { new ClusterSlots(SlotRange.Create(0, 1000), SlotRange.Create(1002, 1010)), 1001 }
        };
    }

    public class ImplicitConversionFrom
    {
        [Theory]
        [MemberData(nameof(SlotTestData))]
        public void CharArraySucceeds(string rawValue, ClusterSlots expectedValue)
        {
            ClusterSlots slots = rawValue.ToCharArray();

            Assert.Equal(expectedValue, slots);
        }

        public static IEnumerable<object[]> SlotTestData = new[]
        {
            new object[]
            {
                "7002",

                new ClusterSlots(
                    ClusterSlots.SlotRange.Create(7002, 7002)
                )
            },

            new object[]
            {
                "7002-7003",

                new ClusterSlots(
                    ClusterSlots.SlotRange.Create(7002, 7003)
                )
            },

            new object[]
            {
                "7000 7001",

                new ClusterSlots(
                    ClusterSlots.SlotRange.Create(7000, 7000),
                    ClusterSlots.SlotRange.Create(7001, 7001)
                )
            },

            new object[]
            {
                "7000 7001 7002-7003",

                new ClusterSlots(
                    ClusterSlots.SlotRange.Create(7000, 7000),
                    ClusterSlots.SlotRange.Create(7001, 7001),
                    ClusterSlots.SlotRange.Create(7002, 7003)
                )
            },

            new object[]
            {
                "7000 7001 7002-7003 7004",

                new ClusterSlots(
                    ClusterSlots.SlotRange.Create(7000, 7000),
                    ClusterSlots.SlotRange.Create(7001, 7001),
                    ClusterSlots.SlotRange.Create(7002, 7003),
                    ClusterSlots.SlotRange.Create(7004, 7004)
                )
            },

            new object[]
            {
                "7000 7001 7002-7003 7004 7005-10000",

                new ClusterSlots(
                    ClusterSlots.SlotRange.Create(7000, 7000),
                    ClusterSlots.SlotRange.Create(7001, 7001),
                    ClusterSlots.SlotRange.Create(7002, 7003),
                    ClusterSlots.SlotRange.Create(7004, 7004),
                    ClusterSlots.SlotRange.Create(7005, 10_000)
                )
            }
        };
    }
}