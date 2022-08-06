using Xunit;

namespace ReadUs.Tests
{
    public class ClusterNodeFlagsTests
    {
        public class ImplicitConversionFrom
        {
            [Theory]
            [InlineData("myself,master", new[] {"myself", "master"})]
            [InlineData("master", new[] {"master"})]
            public void CharArraySucceeds(string rawValue, string[] expectation)
            {
                ClusterNodeFlags flags = rawValue.ToCharArray();

                Assert.Equal(expectation, flags);
            }
        }
    }
}