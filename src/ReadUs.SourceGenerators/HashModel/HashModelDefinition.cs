using System.Collections.Generic;

namespace ReadUs.SourceGenerators.HashModel;

/// <summary>The scalar shapes a Redis hash field can round-trip a property through — see <see cref="HashModelParser"/> for exactly which CLR types classify as each.</summary>
internal enum HashFieldKind
{
    String,
    Bytes,
    Int32,
    Int64,
    Double,
    Boolean,
    Guid,
    DateTime,
    Enum,
}

/// <summary>One property this model maps to/from a hash field. The field name on the wire is always the property's own name.</summary>
internal sealed class HashModelProperty
{
    public required string Name { get; init; }

    public required HashFieldKind Kind { get; init; }

    /// <summary>True for a <c>Nullable&lt;T&gt;</c> value type or a nullable-annotated reference type — absence on read means null/default rather than a thrown exception, and a null value on write is skipped entirely rather than encoded.</summary>
    public required bool IsNullable { get; init; }

    /// <summary>The fully-qualified enum type name, only set when <see cref="Kind"/> is <see cref="HashFieldKind.Enum"/>.</summary>
    public string? EnumTypeName { get; init; }
}

/// <summary>
/// How <see cref="HashModelEmitter"/> constructs an instance from decoded field values —
/// see <see cref="HashModelParser"/>'s remarks for why only these two shapes are
/// supported.
/// </summary>
internal enum HashModelConstructionStrategy
{
    /// <summary>A public parameterless constructor exists; every mapped property is assigned via an object initializer.</summary>
    ParameterlessThenInitializer,

    /// <summary>A public constructor's parameters exactly match every mapped property by name; construction is purely positional.</summary>
    Positional,
}

/// <summary>One <c>[RedisHashModel]</c>-decorated type, ready to emit a companion partial declaration for (see <see cref="HashModelEmitter"/>).</summary>
internal sealed class HashModelDefinition
{
    /// <summary>Empty string for the global namespace.</summary>
    public required string Namespace { get; init; }

    /// <summary><c>class</c>, <c>struct</c>, <c>record</c>, or <c>record struct</c> — must match the user's own declaration exactly, since this is a second partial declaration of the same type.</summary>
    public required string TypeKeyword { get; init; }

    public required string TypeName { get; init; }

    public required HashModelConstructionStrategy ConstructionStrategy { get; init; }

    /// <summary>Only meaningful for <see cref="HashModelConstructionStrategy.Positional"/> — the constructor's parameter order, which is what a positional call must match (not necessarily <see cref="Properties"/>'s own order).</summary>
    public required IReadOnlyList<string> ConstructorParameterOrder { get; init; }

    public required IReadOnlyList<HashModelProperty> Properties { get; init; }
}
