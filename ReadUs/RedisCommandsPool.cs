using System;
using System.Threading.Tasks;
using static ReadUs.Encoder.Encoder;
using static ReadUs.RedisCommandNames;

namespace ReadUs
{
    public abstract class RedisCommandsPool : IRedisCommandsPool
    {
        public abstract Task<IRedisDatabase> GetAsync();

        public abstract void ReturnConnection(IRedisConnection connection);

        public abstract void Dispose();

        public static IRedisCommandsPool Create(Uri connectionString)
        {
            RedisConnectionConfiguration configuration = connectionString;

            if (TryGetClusterInformation(configuration, out var clusterNodesResult))
            {
                return new RedisClusterCommandsPool(clusterNodesResult, configuration.ConnectionsPerNode);
            }
            else
            {
                return new RedisSingleInstanceCommandsPool(configuration);
            }
        }

        internal static bool TryGetClusterInformation(RedisConnectionConfiguration configuration, out ClusterNodesResult clusterNodesResult)
        {
            // First, let's create a connection to whatever server that was provided.
            using var probingConnection = new RedisConnection(configuration);
            probingConnection.Connect();

            // Next, execute the `cluster nodes` command to get an inventory of the cluster.
            var rawCommand = Encode(Cluster, ClusterSubcommands.Nodes);
            var rawResult = probingConnection.SendCommand(rawCommand, TimeSpan.FromMilliseconds(1));

            // Handle the result of the `cluster nodes` command by populating a data structure with the 
            // addresses, role, and slots assigned to each node. 
            var nodes = new ClusterNodesResult(rawResult);

            if (nodes.HasError)
            {
                clusterNodesResult = default(ClusterNodesResult);

                return false;
            }
            else
            {
                clusterNodesResult = nodes;

                return true;
            }
        }
    }
}