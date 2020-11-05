using static ReadUs.ParameterUtilities;
using Xunit;

namespace ReadUs.Tests
{
    public class ParameterUtilitiesTests
    {
        public class CombineParametersCan
        {
            [Fact]
            public void CombineSimpleItems()
            {
                var parameters = CombineParameters("johnny", 5, "is", "alive");

                Assert.Equal("johnny", parameters[0]);
                Assert.Equal(5, parameters[1]);
                Assert.Equal("is", parameters[2]);
                Assert.Equal("alive", parameters[3]);
            }

            [Fact]
            public void CombineItemsWithArray()
            {
                var simpleArray = new object[2];
                simpleArray[0] = 1;
                simpleArray[1] = 2;

                var parameters = CombineParameters("first", simpleArray, "second");

                Assert.Equal("first", parameters[0]);
                Assert.Equal(1, parameters[1]);
                Assert.Equal(2, parameters[2]);
                Assert.Equal("second", parameters[3]);
            }
        }
    }
}
