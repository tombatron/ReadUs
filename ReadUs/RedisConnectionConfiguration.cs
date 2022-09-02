namespace ReadUs
{
    public readonly struct RedisConnectionConfiguration
    {
        public string ServerAddress { get; }

        public int ServerPort { get; }

        public int ConnectionsPerNode { get; }
    }
}