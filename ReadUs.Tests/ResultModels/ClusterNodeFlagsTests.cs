using System;
using ReadUs.ResultModels;
using Xunit;

namespace ReadUs.Tests.ResultModels;

public class ClusterNodeFlagsTests
{
    public class ImplicitConversionFrom
    {
        [Theory]
        [InlineData("myself,master", new[] { "myself", "master" })]
        [InlineData("master", new[] { "master" })]
        public void CharArraySucceeds(string rawValue, string[] expectation)
        {
            ClusterNodeFlags flags = rawValue.ToCharArray();

            Assert.Equal(expectation, flags);
        }
    }

    public class ImplicitConversionTo
    {
        [Theory]
        [InlineData("myself,master", typeof(PrimaryRoleResult))]
        [InlineData("master", typeof(PrimaryRoleResult))]
        [InlineData("slave", typeof(ReplicaRoleResult))]
        [InlineData("myself,slave", typeof(ReplicaRoleResult))]
        //[InlineData("whatever", ClusterNodeRole.Undefined)]
        public void ClusterNodeRoleSucceeds(string rawValue, Type objectType)
        {
            ClusterNodeFlags flags = rawValue.ToCharArray();

            RoleResult role = flags;

            Assert.IsType(objectType, role);

            // Assert.Equal(expectedNodeRole, role);
        }
    }
}