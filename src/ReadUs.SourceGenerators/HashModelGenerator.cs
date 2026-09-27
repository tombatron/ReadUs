using Microsoft.CodeAnalysis;
using ReadUs.SourceGenerators.HashModel;

namespace ReadUs.SourceGenerators;

/// <summary>
/// Generates zero-reflection <c>IRedisHashModel&lt;T&gt;</c> mapping code for every
/// type decorated with <c>[RedisHashModel]</c> (<c>ReadUs.Extensions.Hashes</c>) — see
/// <see cref="HashModelParser"/> for what's supported and <see cref="HashModelEmitter"/>
/// for what gets written. Unlike <see cref="CommandTableGenerator"/> (which reads a
/// vendored table of Redis's own commands), this one reads *user* code: it discovers
/// the attribute by its syntax location via <c>ForAttributeWithMetadataName</c>, the
/// standard efficient incremental-generator pattern for "find types with this
/// attribute" (only re-runs for files whose syntax actually changed, not the whole
/// compilation on every keystroke).
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class HashModelGenerator : IIncrementalGenerator
{
    private const string AttributeMetadataName = "ReadUs.Extensions.Hashes.RedisHashModelAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var candidates = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeMetadataName,
            static (node, _) => true,
            static (ctx, _) => (INamedTypeSymbol)ctx.TargetSymbol);

        context.RegisterSourceOutput(candidates, static (spc, typeSymbol) => Emit(spc, typeSymbol));
    }

    private static void Emit(SourceProductionContext context, INamedTypeSymbol typeSymbol)
    {
        var (definition, diagnostics) = HashModelParser.Parse(typeSymbol);

        foreach (var diagnostic in diagnostics)
        {
            context.ReportDiagnostic(diagnostic);
        }

        if (definition is null)
        {
            return;
        }

        var fileName = (definition.Namespace.Length > 0 ? definition.Namespace + "." : string.Empty) + definition.TypeName + ".RedisHashModel.g.cs";
        context.AddSource(fileName, HashModelEmitter.Emit(definition));
    }
}
