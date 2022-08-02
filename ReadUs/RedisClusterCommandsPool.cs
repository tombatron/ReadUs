using System;
using System.Text;
using static ReadUs.Encoder.Encoder;
using static ReadUs.Parser.Parser;
using static ReadUs.RedisCommandNames;

namespace ReadUs
{
    public class RedisClusterCommandsPool : RedisCommandsPool
    {
        public RedisClusterCommandsPool(string serverAddress, int serverPort) : 
            base(serverAddress, serverPort) => ResolveClusterNodes();

        private void ResolveClusterNodes()
        {
            // First, let's create a connection to whatever server that was provided.
            var initialConnection = new RedisConnection(_serverAddress, _serverPort);
            initialConnection.Connect();

            // Next, execute the `cluster nodes` command to get an inventory of the cluster.
            var rawCommand = Encode(Cluster, ClusterSubcommands.Nodes);
            var rawResult = initialConnection.SendCommand(rawCommand, TimeSpan.FromMilliseconds(1));


            // Handle the result of the `cluster nodes` command by populating a data structure with the 
            // addresses, role, and slots assigned to each node. 
            var result = Parse(rawResult);
            var stringResult = new string(result.Value);
            
            /*
Sample Result:
65d8df12f4515df293cbdf8d5014dc6273621bfc 192.168.86.40:7001@17001 master - 0 1659439685901 19 connected 5461-10922
5953f8065390a9c6bb194762c21920e486f88252 192.168.86.40:7002@17002 master - 0 1659439684594 20 connected 10923-16383
64f88596fec6e244d4f87fa5b702654b36848c35 192.168.86.40:7005@17005 slave 361a0b693ee878c23d0a45c16f965c15ea1e37e6 0 1659439685000 17 connected
e7ed94e397b04f681b8993bb501867a511dbdaf4 192.168.86.40:7004@17004 slave 5953f8065390a9c6bb194762c21920e486f88252 0 1659439684091 20 connected
89c1e6c03dc9fe0a2227f55ff4dcb383350196d2 192.168.86.40:7003@17003 slave 65d8df12f4515df293cbdf8d5014dc6273621bfc 0 1659439684896 19 connected
361a0b693ee878c23d0a45c16f965c15ea1e37e6 192.168.86.40:7000@17000 myself,master - 0 1659439684000 17 connected 0-5460

Result Schema:

1st- id
2nd- ip address : port @ cluster port
3rd- flags
4th - primary id
5th - ping sent
6th - pong received
7th - config epoch
8th - link state
9th - slot range `-` delimited
            */
            
            // TODO: Write some code to parse the cluster nodes result!!!
            
            
            // Start a subprocess to monitor all of the connections to each node. 
        }
    }
}