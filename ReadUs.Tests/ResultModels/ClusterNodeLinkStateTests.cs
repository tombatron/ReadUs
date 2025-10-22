using System;
using JetBrains.Annotations;
using ReadUs.Commands.ResultModels;
using Xunit;

namespace ReadUs.Tests.ResultModels;

[UsedImplicitly]
public class ClusterNodeLinkStateTests
{
    public class ImplicitConversionFrom
    {
        [Theory]
        [InlineData("connected", typeof(ClusterNodeLinkStateConnected))]
        [InlineData("disconnected", typeof(ClusterNodeLinkStateDisconnected))]
        [InlineData("this is a bogus value", typeof(ClusterNodeLinkStateDisconnected))]
        public void CharArraySucceeds(string rawValue, Type expectedType)
        {
            ClusterNodeLinkState state = rawValue.ToCharArray();

            Assert.IsType(expectedType, state);
        }
    }
}