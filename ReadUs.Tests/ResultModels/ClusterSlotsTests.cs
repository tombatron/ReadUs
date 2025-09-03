using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using ReadUs.ResultModels;
using Xunit;
using static ReadUs.ResultModels.ClusterSlots;

namespace ReadUs.Tests.ResultModels;

[UsedImplicitly]
public class ClusterSlotsTests
{
    public class OwnedSlotsContains
    {
        [Fact]
        public void AllOwnedSlots()
        {
            var clusterSlots = new ClusterSlots(SlotRange.Create(100, 200), SlotRange.Create(500, 500),
                SlotRange.Create(1000, 2000));

            var ownedSlots = clusterSlots.OwnedSlots.ToList();

            Assert.True(Enumerable.Range(100, 101).All(x => ownedSlots.Contains(x)));
            Assert.Contains(500, ownedSlots);
            Assert.True(Enumerable.Range(1000, 1001).All(x => ownedSlots.Contains(x)));
        }
    }

    public class ContainsSlotWill
    {
        public static IEnumerable<object[]> PositiveCases =
        [
            [new ClusterSlots(SlotRange.Create(0, 1000)), 500],
            [new ClusterSlots(SlotRange.Create(1001, 1001)), 1001],
            [new ClusterSlots(SlotRange.Create(100, 200), SlotRange.Create(500, 500), SlotRange.Create(1000, 2000)), 500]
        ];

        public static IEnumerable<object[]> NegativeCases =
        [
            [new ClusterSlots(SlotRange.Create(0, 1000)), 1001],
            [new ClusterSlots(SlotRange.Create(0, 1000), SlotRange.Create(1001, 1001)), 1002],
            [new ClusterSlots(SlotRange.Create(0, 1000), SlotRange.Create(1002, 1010)), 1001]
        ];

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
    }

    public class ImplicitConversionFrom
    {
        public static IEnumerable<object[]> SlotTestData =
        [
            [
                "7002",

                new ClusterSlots(
                    SlotRange.Create(7002, 7002)
                )
            ],

            [
                "7002-7003",

                new ClusterSlots(
                    SlotRange.Create(7002, 7003)
                )
            ],

            [
                "7000 7001",

                new ClusterSlots(
                    SlotRange.Create(7000, 7000),
                    SlotRange.Create(7001, 7001)
                )
            ],

            [
                "7000 7001 7002-7003",

                new ClusterSlots(
                    SlotRange.Create(7000, 7000),
                    SlotRange.Create(7001, 7001),
                    SlotRange.Create(7002, 7003)
                )
            ],

            [
                "7000 7001 7002-7003 7004",

                new ClusterSlots(
                    SlotRange.Create(7000, 7000),
                    SlotRange.Create(7001, 7001),
                    SlotRange.Create(7002, 7003),
                    SlotRange.Create(7004, 7004)
                )
            ],

            [
                "7000 7001 7002-7003 7004 7005-10000",

                new ClusterSlots(
                    SlotRange.Create(7000, 7000),
                    SlotRange.Create(7001, 7001),
                    SlotRange.Create(7002, 7003),
                    SlotRange.Create(7004, 7004),
                    SlotRange.Create(7005, 10_000)
                )
            ]
        ];

        [Theory]
        [MemberData(nameof(SlotTestData))]
        public void CharArraySucceeds(string rawValue, ClusterSlots expectedValue)
        {
            ClusterSlots slots = rawValue.ToCharArray();

            Assert.Equal(expectedValue, slots);
        }
    }
}