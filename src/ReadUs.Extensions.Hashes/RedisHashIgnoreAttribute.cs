namespace ReadUs.Extensions.Hashes;

/// <summary>Excludes an otherwise-mappable public read-write property from a <see cref="RedisHashModelAttribute"/> type's generated hash mapping.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class RedisHashIgnoreAttribute : Attribute
{
}
