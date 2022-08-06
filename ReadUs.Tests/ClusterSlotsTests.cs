using System.Collections.Generic;
using Xunit;

namespace ReadUs.Tests
{
    public class ClusterSlotsTests
    {
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
}