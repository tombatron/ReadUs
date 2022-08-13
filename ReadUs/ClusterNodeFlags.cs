﻿using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ReadUs
{
    public class ClusterNodeFlags : ReadOnlyCollection<string>
    {
        private ClusterNodeFlags(IList<string> list) : base(list)
        {
        }

        private static ClusterNodeFlags FromCharArray(char[] rawValue)
        {
            var flagEntries = new string(rawValue).Split(",");

            return new ClusterNodeFlags(flagEntries);
        }

        public static implicit operator ClusterNodeFlags(char[] rawValue) =>
            FromCharArray(rawValue);

        private const string PrimaryRoleFlag = "master";
        private const string SecondaryRoleFlag = "slave";

        public static implicit operator ClusterNodeRole(ClusterNodeFlags flags)
        {
            if (flags.Contains(PrimaryRoleFlag))
            {
                return ClusterNodeRole.Primary;
            }

            if (flags.Contains(SecondaryRoleFlag))
            {
                return ClusterNodeRole.Secondary;
            }

            return ClusterNodeRole.Undefined;
        }
    }
}