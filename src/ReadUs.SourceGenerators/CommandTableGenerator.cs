using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using ReadUs.SourceGenerators.Model;

namespace ReadUs.SourceGenerators;

/// <summary>
/// Reads the vendored Redis command table (codegen/redis-commands, project spec §3)
/// as additional files and emits the metadata table plus typed command methods. See
/// <see cref="CommandTableParser"/> for the JSON→model mapping and
/// <see cref="TypedMethodEmitter"/>/<see cref="CommandMetadataEmitter"/> for what gets
/// written out.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class CommandTableGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var parsedCommands = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(static (file, cancellationToken) => TryParse(file, cancellationToken))
            .Where(static definition => definition is not null)
            .Select(static (definition, _) => definition!)
            .Collect();

        context.RegisterSourceOutput(parsedCommands, static (spc, commands) => Emit(spc, commands));
    }

    private static CommandDefinition? TryParse(AdditionalText file, System.Threading.CancellationToken cancellationToken)
    {
        var text = file.GetText(cancellationToken);
        if (text is null)
        {
            return null;
        }

        try
        {
            return CommandTableParser.Parse(text.ToString());
        }
        catch
        {
            // A malformed or unexpectedly-shaped command file degrades to "no metadata
            // for this one command" rather than failing the whole build — the vendored
            // table is large (~460 files) and not every corner needs to block everyone
            // else's compile while the generator's coverage grows.
            return null;
        }
    }

    private static void Emit(SourceProductionContext context, System.Collections.Immutable.ImmutableArray<CommandDefinition> commands)
    {
        if (commands.IsDefaultOrEmpty)
        {
            return;
        }

        var ordered = commands.OrderBy(c => c.Container ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        context.AddSource("CommandMetadata.g.cs", CommandMetadataEmitter.Emit(ordered));

        foreach (var group in ordered.Where(c => c.Parameters is not null).GroupBy(c => c.Group))
        {
            var list = group.ToList();
            if (list.Count == 0)
            {
                continue;
            }

            string fileName = "Generated." + NameHelpers.PascalCaseWords(group.Key) + ".g.cs";
            context.AddSource(fileName, TypedMethodEmitter.EmitGroup(group.Key, list));
        }
    }
}
