using ReadUs.ResultModels;

namespace ReadUs;

public partial class RedisConnection
{
    public void Connect() => ConnectAsync().GetAwaiter().GetResult();

    public Result<byte[]> SendCommand(RedisCommandEnvelope command) =>
        SendCommandAsync(command).GetAwaiter().GetResult();

    private Result<RoleResult>? _role;

    public Result<RoleResult> Role() => _role ?? RoleAsync().GetAwaiter().GetResult();

    private void SetConnectionClientName() =>
        SendCommand(RedisCommandEnvelope.CreateClientSetNameCommand(ConnectionName));

    internal virtual void SetRole(RoleResult role) =>
        _role = Result<RoleResult>.Ok(role);
}