using System;
using System.Net;

namespace ReadUs.ResultModels
{
    public class ClusterNodeAddress
    {
        private ClusterNodeAddress(char[] rawValue)
        {
            var startIndex = 0;
            var nextDelimiter = Array.IndexOf(rawValue, ':', startIndex);

            IpAddress = IPAddress.Parse(rawValue[startIndex..nextDelimiter]);

            startIndex = nextDelimiter + 1;
            nextDelimiter = Array.IndexOf(rawValue, '@', startIndex);

            RedisPort = int.Parse(rawValue[startIndex..nextDelimiter]);

            startIndex = nextDelimiter + 1;

            ClusterPort = int.Parse(rawValue[startIndex..]);
        }

        public IPAddress IpAddress { get; private set; }

        public int RedisPort { get; private set; }

        public int ClusterPort { get; private set; }

        public static implicit operator ClusterNodeAddress(char[] rawValue) =>
            new ClusterNodeAddress(rawValue);
    }
}