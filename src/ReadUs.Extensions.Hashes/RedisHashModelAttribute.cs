namespace ReadUs.Extensions.Hashes;

/// <summary>
/// Marks a <c>partial</c> type for compile-time generated Redis hash mapping (project
/// spec §10 — proper property-per-field mapping, as opposed to
/// <c>ReadUs.Extensions.Json</c>'s whole-value JSON blob approach). The type must be
/// declared <c>partial</c> and have either a public parameterless constructor or a
/// public constructor whose parameters exactly match every mapped property by name
/// (covers a plain mutable POCO and a positional <c>record</c>, the two common
/// shapes) — see the generator's diagnostics for anything else.
///
/// Every public read-write instance property is mapped by its own name unless marked
/// <see cref="RedisHashIgnoreAttribute"/>. Supported property types: <see cref="string"/>,
/// <c>byte[]</c>, <see cref="int"/>, <see cref="long"/>, <see cref="double"/>,
/// <see cref="bool"/>, <see cref="System.Guid"/>, <see cref="System.DateTime"/>, any
/// <see langword="enum"/>, and a nullable of any of the value types. A non-nullable
/// property throws if its field is absent when reading; a nullable one (or a
/// null-valued one when writing) is simply skipped.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class RedisHashModelAttribute : Attribute
{
}
