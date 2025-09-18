using ReadUs.ResultModels;

namespace ReadUs;

public partial class RedisConnection
{
    public Result Connect() => ConnectAsync().GetAwaiter().GetResult();
    
    public Result<byte[]> SendCommand(RedisCommandEnvelope command) => 
        SendCommandAsync(command).GetAwaiter().GetResult();
    
    public Result<RoleResult> Role() => RoleAsync().GetAwaiter().GetResult();

    public Result<ClusterSlots> Slots() => SlotsAsync().GetAwaiter().GetResult();
    
    private void SetConnectionClientName() => 
        SendCommand(RedisCommandEnvelope.CreateClientSetNameCommand(ConnectionName));
}