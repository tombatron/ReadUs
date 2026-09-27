namespace ReadUs.Connections;

/// <summary>
/// A rotating-credential value, returned by <see cref="IRedisCredentialsProvider"/>.
/// </summary>
public readonly struct RedisCredentials
{
    /// <summary>Null means the server's default user.</summary>
    public string? Username { get; init; }

    public required string Password { get; init; }

    /// <summary>
    /// When the provider knows the credential will stop being valid, used to schedule
    /// proactive re-authentication (project spec §7: "the client responsible for
    /// re-authenticating proactively before expiry rather than waiting for an auth
    /// failure"). Null means the provider can't say — <see cref="RedisConnection"/>
    /// won't schedule a refresh in that case, only re-authenticating reactively via
    /// <see cref="RedisConnection.ReAuthenticateAsync"/> if a caller invokes it.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>
/// Pluggable credential supply for auth flows ReadUs doesn't implement itself — AWS
/// ElastiCache/MemoryDB IAM tokens (SigV4-signed), Azure Cache Entra ID bearer tokens,
/// or any other rotating-token scheme (project spec §7). ReadUs deliberately does not
/// depend on the AWS or Azure SDKs to generate these; an application wires up its own
/// provider backed by whichever SDK it already uses, and <see cref="RedisConnection"/>
/// just calls it — at connect time, and again on a schedule if
/// <see cref="RedisCredentials.ExpiresAt"/> is supplied.
///
/// The provider owns its own caching/refresh-if-near-expiry policy — every call to
/// <see cref="GetCredentialsAsync"/> should return a currently-valid credential, doing
/// whatever work is needed internally, not force the caller to reason about staleness.
/// </summary>
public interface IRedisCredentialsProvider
{
    ValueTask<RedisCredentials> GetCredentialsAsync(CancellationToken cancellationToken);
}
