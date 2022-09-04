using ReadUs.ResultModels;
using System;
using Xunit;

namespace ReadUs.Tests.ResultModels
{
    public class ClusterNodeLinkStateTests
    {
        public class ImplicitConversionFrom
        {
            [Theory]
            [InlineData("connected", typeof(ClusterNodeLinkStateConnected))]
            [InlineData("disconnected", typeof(ClusterNodeLinkStateDisconnected))]
            public void CharArraySucceeds(string rawValue, Type expectedType)
            {
                ClusterNodeLinkState state = rawValue.ToCharArray();

                Assert.IsType(expectedType, state);
            }

            [Fact]
            public void InvalidCharArrayReturnsNull()
            {
                ClusterNodeLinkState state = "blah".ToCharArray();

                Assert.Null(state);
            }
        }
    }
}