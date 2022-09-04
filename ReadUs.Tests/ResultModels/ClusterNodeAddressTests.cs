using ReadUs.ResultModels;
using System.Net;
using Xunit;

namespace ReadUs.Tests.ResultModels
{
    public class ClusterNodeAddressTests
    {
        public class ImplicitConversionFrom
        {
            [Fact]
            public void CharArraySucceeds()
            {
                ClusterNodeAddress address = "192.168.86.40:7001@17001".ToCharArray();
                
                Assert.Equal(IPAddress.Parse("192.168.86.40"), address.IpAddress);
                Assert.Equal(7001, address.RedisPort);
                Assert.Equal(17001, address.ClusterPort);
            }
        }
    }
}