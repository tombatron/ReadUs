using System.Collections.Generic;

namespace ReadUs.SourceGenerators.Model;

internal enum ArgumentWireType
{
    /// <summary>Text-like: key, string, pattern, type name, glob pattern — carried as bytes.</summary>
    Bytes,
    Integer,
    Double,

    /// <summary>A literal keyword with no value (e.g. <c>ALPHA</c>, <c>NX</c>) — becomes a <c>bool</c> parameter.</summary>
    PureToken,
}

/// <summary>Common shape shared by a flattened scalar/pure-token slot and an unflattenable block group — see <see cref="CommandArgument"/> and <see cref="CommandArgumentGroup"/>.</summary>
internal interface ICommandParameterSlot
{
    string Name { get; }
}

/// <summary>
/// One flattened, generatable parameter: either a plain top-level scalar/pure-token
/// argument, or a member of a top-level <c>oneof</c> group (which CommandTableParser
/// flattens into independent optional parameters at this same level — see its remarks
/// for why).
/// </summary>
internal sealed class CommandArgument : ICommandParameterSlot
{
    public required string Name { get; init; }

    public required ArgumentWireType WireType { get; init; }

    /// <summary>The literal keyword written before the value(s), if any (e.g. <c>EX</c>, <c>NX</c>).</summary>
    public string? Token { get; init; }

    public bool Optional { get; init; }

    public bool Multiple { get; init; }

    /// <summary>Only meaningful when <see cref="Multiple"/> is true: repeat <see cref="Token"/> before every occurrence rather than once before the whole group.</summary>
    public bool MultipleToken { get; init; }
}

/// <summary>
/// A block of scalar arguments that has to travel together as one unit and so can't be
/// flattened into independent parameters like a <c>oneof</c> can — e.g. SORT's
/// <c>LIMIT offset count</c> (optional, non-repeating: becomes one nullable tuple
/// parameter) or HSET's repeating <c>field value</c> pairs (multiple: becomes one
/// list-of-tuples parameter). A block that is both required and non-repeating doesn't
/// need this wrapper at all — CommandTableParser inlines its members directly as
/// ordinary required <see cref="CommandArgument"/>s, since they're unconditionally
/// present in a fixed sequence anyway.
/// </summary>
internal sealed class CommandArgumentGroup : ICommandParameterSlot
{
    public required string Name { get; init; }

    public string? Token { get; init; }

    public bool Optional { get; init; }

    public bool Multiple { get; init; }

    public required IReadOnlyList<CommandArgument> Members { get; init; }
}
