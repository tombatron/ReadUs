using Xunit;

namespace ReadUs.Tests
{
    public class ClusterNodePrimaryIdTests
    {
        public class ImplicitConversionFrom
        {
            [Theory]
            [InlineData("361a0b693ee878c23d0a45c16f965c15ea1e37e6", "361a0b693ee878c23d0a45c16f965c15ea1e37e6")]
            [InlineData("-", default(ClusterNodePrimaryId))]
            public void CharArraySucceeds(string rawValue, string expectedResult)
            {
                ClusterNodePrimaryId primaryId = rawValue.ToCharArray();

                Assert.Equal(expectedResult, primaryId);
            }
        }
    }
}