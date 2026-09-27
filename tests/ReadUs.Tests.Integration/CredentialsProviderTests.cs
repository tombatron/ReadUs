using System.Net;
using ReadUs.Connections;

namespace ReadUs.Tests.Integration;

/// <summary>
/// Exercises the pluggable rotating-credential auth path (project spec §7) against a
/// real server — a stand-in for AWS IAM/Azure Entra token providers, which ReadUs
/// deliberately doesn't implement itself. Each test gets its own throwaway ACL user
/// (<see cref="InitializeAsync"/>/<see cref="DisposeAsync"/>) so tests never interfere
/// with each other's password state.
/// </summary>
public class CredentialsProviderTests : IAsyncLifetime
{
    private const string InitialPassword = "initial-pw-1";
    private readonly string _username = $"readus-rotate-{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        await using var admin = await RedisClient.ConnectAsync(new RedisConnectionOptions { EndPoint = new DnsEndPoint("localhost", 6379) }, connectionCount: 1);
        await admin.ExecuteAsync("ACL"u8.ToArray(), ["SETUSER"u8.ToArray(), Encode(_username), Encode($">{InitialPassword}"), "allkeys"u8.ToArray(), "allcommands"u8.ToArray(), "on"u8.ToArray()]);
    }

    public async Task DisposeAsync()
    {
        await using var admin = await RedisClient.ConnectAsync(new RedisConnectionOptions { EndPoint = new DnsEndPoint("localhost", 6379) }, connectionCount: 1);
        await admin.ExecuteAsync("ACL"u8.ToArray(), ["DELUSER"u8.ToArray(), Encode(_username)]);
    }

    [Fact]
    public async Task ConnectsUsingACredentialsProviderInsteadOfAStaticPassword()
    {
        var provider = new MutableCredentialsProvider(_username, InitialPassword);
        var options = new RedisConnectionOptions { EndPoint = new DnsEndPoint("localhost", 6379), CredentialsProvider = provider };

        await using var connection = await RedisConnection.ConnectAsync(options);
        var result = await connection.SendAsync("PING"u8.ToArray(), []);

        Assert.Equal("PONG", result.AsString());
    }

    [Fact]
    public async Task ConnectingWithAWrongPasswordFromTheProviderFails()
    {
        var provider = new MutableCredentialsProvider(_username, "definitely-the-wrong-password");
        var options = new RedisConnectionOptions { EndPoint = new DnsEndPoint("localhost", 6379), CredentialsProvider = provider };

        await Assert.ThrowsAsync<RedisConnectionException>(async () => await RedisConnection.ConnectAsync(options));
    }

    [Fact]
    public async Task ReAuthenticateAsyncPicksUpARotatedPasswordOnAnAlreadyOpenConnection()
    {
        const string rotatedPassword = "rotated-pw-2";

        var provider = new MutableCredentialsProvider(_username, InitialPassword);
        var options = new RedisConnectionOptions { EndPoint = new DnsEndPoint("localhost", 6379), CredentialsProvider = provider };

        await using var connection = await RedisConnection.ConnectAsync(options);
        var beforeRotation = await connection.SendAsync("PING"u8.ToArray(), []);
        Assert.Equal("PONG", beforeRotation.AsString());

        // Rotate the server-side password out from under the already-open connection —
        // exactly the scenario a real IAM/Entra token refresh looks like.
        await using (var admin = await RedisClient.ConnectAsync(new RedisConnectionOptions { EndPoint = new DnsEndPoint("localhost", 6379) }, connectionCount: 1))
        {
            await admin.ExecuteAsync("ACL"u8.ToArray(), ["SETUSER"u8.ToArray(), Encode(_username), Encode($">{rotatedPassword}"), Encode($"<{InitialPassword}")]);
        }

        provider.SetPassword(rotatedPassword);
        await connection.ReAuthenticateAsync();

        var afterRotation = await connection.SendAsync("PING"u8.ToArray(), []);
        Assert.Equal("PONG", afterRotation.AsString());
    }

    private static byte[] Encode(string value) => System.Text.Encoding.UTF8.GetBytes(value);

    private sealed class MutableCredentialsProvider(string username, string initialPassword) : IRedisCredentialsProvider
    {
        private string _password = initialPassword;

        public void SetPassword(string password) => Volatile.Write(ref _password, password);

        public ValueTask<RedisCredentials> GetCredentialsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RedisCredentials { Username = username, Password = Volatile.Read(ref _password) });
    }
}
