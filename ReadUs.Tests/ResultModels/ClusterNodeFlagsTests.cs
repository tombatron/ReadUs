﻿using ReadUs.ResultModels;
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
        [InlineData("myself,master", ClusterNodeRole.Primary)]
        [InlineData("master", ClusterNodeRole.Primary)]
        [InlineData("slave", ClusterNodeRole.Secondary)]
        [InlineData("myself,slave", ClusterNodeRole.Secondary)]
        [InlineData("whatever", ClusterNodeRole.Undefined)]
        public void ClusterNodeRoleSucceeds(string rawValue, ClusterNodeRole expectedNodeRole)
        {
            ClusterNodeFlags flags = rawValue.ToCharArray();

            ClusterNodeRole role = flags;

            Assert.Equal(expectedNodeRole, role);
        }
    }
}