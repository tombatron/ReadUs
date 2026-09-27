using System.Text;
using ReadUs.Connections;
using ReadUs.Extensions.Hashes;
using ReadUs.Tests.Integration.Fixtures;

namespace ReadUs.Tests.Integration;

public enum AccountStatus
{
    Active,
    Suspended,
}

[RedisHashModel]
public sealed partial class MutableAccount
{
    public string Name { get; set; } = "";

    public int Age { get; set; }

    public bool IsActive { get; set; }

    public double Balance { get; set; }

    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public AccountStatus Status { get; set; }

    public string? Nickname { get; set; }

    public int? LoyaltyPoints { get; set; }

    [RedisHashIgnore]
    public string Ignored { get; set; } = "should never round-trip";
}

[RedisHashModel]
public sealed partial record PositionalAccount(string Name, int Age);

/// <summary>Exercises the compile-time generated <c>IRedisHashModel&lt;T&gt;</c> mapping (project spec §10) against a real, disposable Testcontainers-managed server (project spec §9.2).</summary>
[Collection(StandaloneRedisCollection.Name)]
public class HashRedisClientExtensionsTests(StandaloneRedisFixture fixture)
{
    private RedisConnectionOptions Options => new()
    {
        EndPoint = fixture.EndPoint,
    };

    [Fact]
    public async Task RoundTripsAMutablePocoIncludingNullableAndIgnoredFields()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1);
        var key = Encoding.UTF8.GetBytes($"readus:test:hash:{Guid.NewGuid():N}");
        var account = new MutableAccount
        {
            Name = "Ada Lovelace",
            Age = 36,
            IsActive = true,
            Balance = 1234.5,
            Id = Guid.NewGuid(),
            CreatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Status = AccountStatus.Suspended,
            Nickname = "Ada",
            LoyaltyPoints = 42,
        };

        await client.SetHashAsync(key, account);
        var result = await client.GetHashAsync<MutableAccount>(key);

        Assert.NotNull(result);
        Assert.Equal(account.Name, result!.Name);
        Assert.Equal(account.Age, result.Age);
        Assert.Equal(account.IsActive, result.IsActive);
        Assert.Equal(account.Balance, result.Balance);
        Assert.Equal(account.Id, result.Id);
        Assert.Equal(account.CreatedAt, result.CreatedAt);
        Assert.Equal(account.Status, result.Status);
        Assert.Equal(account.Nickname, result.Nickname);
        Assert.Equal(account.LoyaltyPoints, result.LoyaltyPoints);

        // [RedisHashIgnore] never round-trips — it comes back as this type's own
        // default, not whatever was in the original instance.
        Assert.Equal("should never round-trip", result.Ignored);

        // Prove it's a *real* hash, not a JSON blob — plain HGET on one field works.
        var nameOnly = await client.ExecuteAsync("HGET"u8.ToArray(), [key, "Name"u8.ToArray()]);
        Assert.Equal("Ada Lovelace", nameOnly.AsString());
    }

    [Fact]
    public async Task NullNullablePropertiesAreOmittedAndComeBackNull()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1);
        var key = Encoding.UTF8.GetBytes($"readus:test:hash:{Guid.NewGuid():N}");
        var account = new MutableAccount { Name = "Grace Hopper", Age = 85, Nickname = null, LoyaltyPoints = null };

        await client.SetHashAsync(key, account);

        var fieldCount = await client.ExecuteAsync("HLEN"u8.ToArray(), [key]);
        Assert.Equal(7, fieldCount.AsInt64()); // 9 mapped properties minus the 2 null nullable ones

        var result = await client.GetHashAsync<MutableAccount>(key);
        Assert.NotNull(result);
        Assert.Null(result!.Nickname);
        Assert.Null(result.LoyaltyPoints);
    }

    [Fact]
    public async Task GetHashAsyncReturnsNullForAMissingKey()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1);
        var key = Encoding.UTF8.GetBytes($"readus:test:hash:missing:{Guid.NewGuid():N}");

        var result = await client.GetHashAsync<MutableAccount>(key);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetHashAsyncThrowsWhenARequiredFieldIsMissing()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1);
        var key = Encoding.UTF8.GetBytes($"readus:test:hash:partial:{Guid.NewGuid():N}");

        // Written directly, bypassing SetHashAsync, so the required "Age" field is
        // simply never present — proves the generated FromHash actually enforces it
        // rather than silently defaulting.
        await client.ExecuteAsync("HSET"u8.ToArray(), [key, "Name"u8.ToArray(), "Incomplete"u8.ToArray()]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await client.GetHashAsync<MutableAccount>(key));
        Assert.Contains("Age", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RoundTripsAPositionalRecord()
    {
        await using var client = await RedisClient.ConnectAsync(Options, connectionCount: 1);
        var key = Encoding.UTF8.GetBytes($"readus:test:hash:positional:{Guid.NewGuid():N}");
        var account = new PositionalAccount("Katherine Johnson", 101);

        await client.SetHashAsync(key, account);
        var result = await client.GetHashAsync<PositionalAccount>(key);

        Assert.Equal(account, result);
    }
}
