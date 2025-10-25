namespace ReadUs;

public partial class RedisConnection
{
    public Result Connect() => ConnectAsync().GetAwaiter().GetResult();
    
    public Result<byte[]> SendCommand(RedisCommandEnvelope command) => 
        SendCommandAsync(command).GetAwaiter().GetResult();
}