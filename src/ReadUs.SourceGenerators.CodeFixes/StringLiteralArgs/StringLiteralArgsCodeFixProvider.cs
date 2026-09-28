using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ReadUs.SourceGenerators.StringLiteralArgs;

/// <summary>
/// Rewrites a call flagged by <see cref="StringLiteralArgsAnalyzer"/> from the
/// string-based <c>ExecuteAsync</c>/<c>ExecuteBlockingAsync</c> overload to the
/// byte-based one, by replacing each string-literal argument with the equivalent
/// <c>"text"u8.ToArray()</c> expression. Almost a pure per-literal rewrite — the one
/// structural adjustment is appending an explicit <c>[]</c> when the original call
/// omitted <c>args</c> entirely (relying on the string overload's <c>args = null</c>
/// default): the byte-based overload's <c>args</c> parameter has no default, so it must
/// always be supplied once the call resolves there. Overload resolution otherwise
/// re-picks the byte-based overload automatically once every argument's compile-time
/// type changes from <see langword="string"/>/<c>string[]</c> to
/// <c>byte[]</c>/<c>ReadOnlyMemory&lt;byte&gt;[]</c>. Safe only because the analyzer
/// already proved every string argument is a plain literal — this provider does not
/// attempt to handle anything it didn't verify.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp), Shared]
public sealed class StringLiteralArgsCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [StringLiteralArgsAnalyzer.DiagnosticId];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        var diagnostic = context.Diagnostics[0];
        var invocation = root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<InvocationExpressionSyntax>();
        if (invocation is null)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                "Use u8 literals (zero-allocation) instead of string arguments",
                cancellationToken => RewriteAsync(context.Document, root, invocation, cancellationToken),
                equivalenceKey: StringLiteralArgsAnalyzer.DiagnosticId),
            diagnostic);
    }

    private static Task<Document> RewriteAsync(Document document, SyntaxNode root, InvocationExpressionSyntax invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var newArgumentList = RewriteArgumentList(invocation.ArgumentList);
        var newInvocation = invocation.WithArgumentList(newArgumentList);
        var newRoot = root.ReplaceNode(invocation, newInvocation);
        return Task.FromResult(document.WithSyntaxRoot(newRoot));
    }

    private static ArgumentListSyntax RewriteArgumentList(ArgumentListSyntax argumentList)
    {
        var newArguments = argumentList.Arguments.Select(RewriteArgument).ToList();

        if (newArguments.Count == 1)
        {
            // The original call relied on the string overload's `args = null` default;
            // the byte-based overload has no such default, so it must be supplied
            // explicitly once the call resolves there.
            newArguments.Add(SyntaxFactory.Argument(SyntaxFactory.ParseExpression("[]")));
        }

        return argumentList.WithArguments(SyntaxFactory.SeparatedList(newArguments));
    }

    private static ArgumentSyntax RewriteArgument(ArgumentSyntax argument)
    {
        var expression = argument.Expression;

        if (IsStringLiteral(expression, out var literal))
        {
            return argument.WithExpression(ToUtf8Bytes(literal));
        }

        return expression switch
        {
            CollectionExpressionSyntax collection => argument.WithExpression(
                collection.WithElements(SyntaxFactory.SeparatedList(collection.Elements.Select(RewriteCollectionElement)))),
            ArrayCreationExpressionSyntax { Initializer: { } initializer } arrayCreation => argument.WithExpression(
                arrayCreation.WithInitializer(RewriteInitializer(initializer))),
            ImplicitArrayCreationExpressionSyntax implicitArray => argument.WithExpression(
                implicitArray.WithInitializer(RewriteInitializer(implicitArray.Initializer))),
            _ => argument,
        };
    }

    private static CollectionElementSyntax RewriteCollectionElement(CollectionElementSyntax element) =>
        element is ExpressionElementSyntax expressionElement && IsStringLiteral(expressionElement.Expression, out var literal)
            ? SyntaxFactory.ExpressionElement(ToUtf8Bytes(literal))
            : element;

    private static InitializerExpressionSyntax RewriteInitializer(InitializerExpressionSyntax initializer) =>
        initializer.WithExpressions(SyntaxFactory.SeparatedList(initializer.Expressions.Select(
            e => IsStringLiteral(e, out var literal) ? ToUtf8Bytes(literal) : e)));

    private static bool IsStringLiteral(ExpressionSyntax expression, out LiteralExpressionSyntax literal)
    {
        if (expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } match)
        {
            literal = match;
            return true;
        }

        literal = null!;
        return false;
    }

    private static ExpressionSyntax ToUtf8Bytes(LiteralExpressionSyntax literal) =>
        SyntaxFactory.ParseExpression(literal.Token.Text + "u8.ToArray()").WithTriviaFrom(literal);
}
