using System;
using static ReadUs.Encoder.Encoder;
using static ReadUs.RedisCommandNames;

namespace ReadUs
{
    public class RedisClusterCommandsPool : RedisCommandsPool
    {
        private ClusterNodesResult _existingClusterNodes;

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
            var nodes = new ClusterNodesResult(rawResult);

            // TODO: Think about how to make this more robust. This won't survive any kind of change
            //       to the cluster. 
            _existingClusterNodes = nodes;
        }
    }
}