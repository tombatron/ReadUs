using ReadUs.ResultModels;
using Xunit;

namespace ReadUs.Tests.ResultModels;

public class ClusterNodesResultItemTests
{
    public class ItWillCorrectlyParse
    {
        private static char[] SamplePrimaryEntry = "65d8df12f4515df293cbdf8d5014dc6273621bfc 192.168.86.40:7001@17001 master - 0 1659439685901 19 connected 5461-10922".ToCharArray();
        private static char[] SampleSecondaryEntry = "64f88596fec6e244d4f87fa5b702654b36848c35 192.168.86.40:7005@17005 slave 361a0b693ee878c23d0a45c16f965c15ea1e37e6 0 1659439685000 17 connected".ToCharArray();
        private static char[] SamplePrimaryEntryWithMultipleFlags = "361a0b693ee878c23d0a45c16f965c15ea1e37e6 192.168.86.40:7000@17000 myself,master - 0 1659439684000 17 connected 5461-6999 7002 7004-10922".ToCharArray();
            
        [Fact]
        public void TheId()
        {
            var item = new ClusterNodesResultItem(SamplePrimaryEntry);

            Assert.Equal("65d8df12f4515df293cbdf8d5014dc6273621bfc", item.Id);
        }

        [Fact]
        public void TheAddress()
        {
            var item = new ClusterNodesResultItem(SamplePrimaryEntry);

            Assert.NotNull(item.Address);
        }

        [Fact]
        public void TheFlags()
        {
            var item = new ClusterNodesResultItem(SamplePrimaryEntryWithMultipleFlags);

            Assert.NotNull(item.Flags);
        }

        [Fact]
        public void ThePrimaryId()
        {
            var item = new ClusterNodesResultItem(SampleSecondaryEntry);

            Assert.Equal("361a0b693ee878c23d0a45c16f965c15ea1e37e6", item.PrimaryId);
        }

        [Fact]
        public void ThePingSent()
        {
            var item = new ClusterNodesResultItem(SamplePrimaryEntry);
                
            Assert.Equal(0, item.PingSent);
        }

        [Fact]
        public void ThePongReceived()
        {
            var item = new ClusterNodesResultItem(SamplePrimaryEntry);
                
            Assert.Equal(1659439685901, item.PongReceived);
        }

        [Fact]
        public void TheConfigEpoch()
        {
            var item = new ClusterNodesResultItem(SamplePrimaryEntry);

            Assert.Equal(19, item.ConfigEpoch);
        }

        [Fact]
        public void TheLinkState()
        {
            var item = new ClusterNodesResultItem(SamplePrimaryEntry);

            Assert.IsType<ClusterNodeLinkStateConnected>(item.LinkState);
        }

        [Fact]
        public void TheSlotRanges()
        {
            var item = new ClusterNodesResultItem(SamplePrimaryEntryWithMultipleFlags);

            Assert.NotNull(item.Slots);
        }
    }
}