using System;
using System.Threading.Tasks;
using ReadUs.ResultModels;

namespace ReadUs
{
    public abstract class RedisConnectionPool : IRedisConnectionPool
    {
        public abstract Task<IRedisDatabase> GetAsync();

        public abstract void ReturnConnection(IRedisConnection connection);

        public abstract void Dispose();

        public static IRedisConnectionPool Create(Uri connectionString)
        {
            RedisConnectionConfiguration configuration = connectionString;

            if (TryGetClusterInformation(configuration, out var clusterNodesResult))
            {
                return new RedisClusterConnectionPool(clusterNodesResult, configuration.ConnectionsPerNode);
            }
            else
            {
                return new RedisSingleInstanceConnectionPool(configuration);
            }
        }

        internal static bool TryGetClusterInformation(RedisConnectionConfiguration configuration, out ClusterNodesResult? clusterNodesResult)
        {
            // First, let's create a connection to whatever server that was provided.
            using var probingConnection = new RedisConnection(configuration);

            // Now let's... connect.
            probingConnection.Connect();

            // Next, execute the `cluster nodes` command to get an inventory of the cluster.
            var rawResult = probingConnection.SendCommand(RedisCommandEnvelope.CreateClusterNodesCommand());

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