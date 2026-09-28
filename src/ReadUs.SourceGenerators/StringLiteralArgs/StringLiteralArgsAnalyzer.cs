using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace ReadUs.SourceGenerators.StringLiteralArgs;

/// <summary>
/// Flags a call to <c>ReadUs.StringCommandExtensions.ExecuteAsync</c>/<c>ExecuteBlockingAsync</c>
/// (the <see langword="string"/>-based overload of <c>IRedisClient</c>'s raw escape
/// hatch) whose arguments are all compile-time string literals — the exact case where
/// <see cref="StringLiteralArgsCodeFixProvider"/> can mechanically rewrite the call to
/// the byte-based overload with <c>u8.ToArray()</c> literals, with no runtime UTF-8
/// encode left on that call. Deliberately does not flag a call with any non-literal
/// string argument (a variable, interpolation, concatenation) — partial-literal
/// rewriting is a real but lower-value case this analyzer bails out of cleanly rather
/// than attempting a general solver, the same scope discipline
/// <c>HashModelParser</c>/<c>CommandTableGenerator</c> already apply elsewhere in this
/// project.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StringLiteralArgsAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "READUS001";

    public static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Command uses only compile-time string literals",
        "This call to '{0}' uses only compile-time string literal arguments — the byte-based overload with u8 literals avoids a runtime UTF-8 encode on every call",
        "ReadUs.Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    private const string ExtensionsTypeMetadataName = "ReadUs.StringCommandExtensions";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(compilationContext =>
        {
            var extensionsType = compilationContext.Compilation.GetTypeByMetadataName(ExtensionsTypeMetadataName);
            if (extensionsType is null)
            {
                // ReadUs.Core isn't referenced (or this overload doesn't exist in the
                // referenced version) — nothing for this analyzer to do.
                return;
            }

            compilationContext.RegisterOperationAction(
                operationContext => Analyze(operationContext, extensionsType),
                OperationKind.Invocation);
        });
    }

    private static void Analyze(OperationAnalysisContext context, INamedTypeSymbol extensionsType)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;

        if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, extensionsType))
        {
            return;
        }

        if (method.Name is not ("ExecuteAsync" or "ExecuteBlockingAsync"))
        {
            return;
        }

        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter?.Name == "commandName")
            {
                if (!IsStringLiteral(argument.Value.Syntax))
                {
                    return;
                }
            }
            else if (argument.Parameter?.Name == "args")
            {
                if (argument.ArgumentKind == ArgumentKind.Explicit && !IsAllLiteralArgsExpression(argument.Value.Syntax))
                {
                    return;
                }
            }
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.Syntax.GetLocation(), method.Name));
    }

    private static bool IsStringLiteral(SyntaxNode node) =>
        node is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression };

    /// <summary>True for an explicit <c>null</c> (nothing to convert), or a collection expression / array creation whose every element is a plain string literal.</summary>
    internal static bool IsAllLiteralArgsExpression(SyntaxNode node) => node switch
    {
        LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } => true,
        CollectionExpressionSyntax collection => collection.Elements.All(static element =>
            element is ExpressionElementSyntax { Expression: var expr } && IsStringLiteral(expr)),
        ArrayCreationExpressionSyntax { Initializer: { } initializer } => initializer.Expressions.All(IsStringLiteral),
        ImplicitArrayCreationExpressionSyntax { Initializer: var initializer } => initializer.Expressions.All(IsStringLiteral),
        _ => false,
    };
}
