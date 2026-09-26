using System.Collections.Generic;

namespace ReadUs.SourceGenerators.Model;

/// <summary>Simplified key-position metadata — only the staticly-determinable range shape (project spec §3, §5). See <see cref="HasUnknownKeys"/> for the rest.</summary>
internal sealed class KeySpec
{
    public required int FirstKeyPosition { get; init; }

    /// <summary>Negative means "relative to the end of the argument list", per Redis's own convention (0 = last argument).</summary>
    public required int LastKeyPosition { get; init; }

    public required int KeyStep { get; init; }
}

/// <summary>
/// One parsed command or subcommand (the file may declare a subcommand, in which case
/// <see cref="Container"/> is set — e.g. <c>client-id.json</c> declares <c>ID</c> with
/// container <c>CLIENT</c>, meaning the wire command is <c>CLIENT ID</c>).
/// </summary>
internal sealed class CommandDefinition
{
    public required string Name { get; init; }

    public string? Container { get; init; }

    public required string Group { get; init; }

    public required int Arity { get; init; }

    public bool IsWrite { get; init; }

    public bool IsReadOnly { get; init; }

    public bool IsBlocking { get; init; }

    public required IReadOnlyList<KeySpec> KeySpecs { get; init; }

    /// <summary>True if any key_spec's position can't be statically determined (e.g. SORT's BY/GET/STORE) — the codegen'd metadata flags this rather than guessing (project spec §5's CROSSSLOT pre-flight validation needs to know the difference).</summary>
    public bool HasUnknownKeys { get; init; }

    /// <summary>Null if this command's argument shape couldn't be flattened into typed parameters (see CommandTableParser) — it still gets a metadata entry, just no generated typed method.</summary>
    public IReadOnlyList<ICommandParameterSlot>? Parameters { get; init; }

    public string WireCommandName => Container ?? Name;
}
