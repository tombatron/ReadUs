namespace ReadUs
{
    public readonly struct RedisConnectionConfiguration
    {
        public string ServerAddress { get; }

        public int ServerPort { get; }

        public int ConnectionsPerNode { get; }

        public RedisConnectionConfiguration(string serverAddress, int serverPort, int connectionsPerNode = 1)
        {
            ServerAddress = serverAddress;
            ServerPort = serverPort;
            ConnectionsPerNode = connectionsPerNode;
        }
    }
}