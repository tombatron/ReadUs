using System.Collections.Generic;
using System.Linq;
using System.Text;
using ReadUs.SourceGenerators.Model;

namespace ReadUs.SourceGenerators;

internal static class NameHelpers
{
    // Contextual keywords (var, required, async, ...) are fine as identifiers and
    // don't need @-escaping; only these reserved words actually require it.
    private static readonly HashSet<string> ReservedKeywords = new()
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
        "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw",
        "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using",
        "virtual", "void", "volatile", "while",
    };

    /// <summary>
    /// Capitalizes a single word with no internal separators as one unit (e.g. "BLPOP"
    /// -> "Blpop", not "BlPop") since there's no reliable, general way to auto-split
    /// these into word boundaries without a hand-curated exception table for all ~460
    /// commands — a deliberate v0 looseness (see docs/design/state-machines.md §5),
    /// not an oversight. Call sites read `client.Blpop(...)` rather than
    /// `client.BlPop(...)`; still perfectly usable, just less pretty-cased. Only safe
    /// to call on a token with no '_'/'-' in it — see <see cref="PascalCaseWords"/>.
    /// </summary>
    private static string PascalCaseToken(string token) =>
        token.Length == 0 ? token : char.ToUpperInvariant(token[0]) + token.Substring(1).ToLowerInvariant();

    /// <summary>
    /// Splits on '_'/'-' first, unlike <see cref="PascalCaseToken"/> — needed for
    /// anything that isn't guaranteed to be one unbroken word: group names ("sorted_set"),
    /// and subcommand/token names that occasionally contain a literal hyphen (e.g.
    /// <c>CLIENT NO-EVICT</c>) that would otherwise end up embedded in a generated
    /// identifier and fail to compile.
    /// </summary>
    public static string PascalCaseWords(string value) =>
        string.Concat(value.Split('_', '-').Where(w => w.Length > 0).Select(PascalCaseToken));

    public static string MethodName(CommandDefinition command)
    {
        var prefix = command.Container is null ? string.Empty : PascalCaseWords(command.Container);
        return prefix + PascalCaseWords(command.Name) + "Async";
    }

    public static string GroupClassName(string group) => PascalCaseWords(group) + "Commands";

    /// <summary>A stable, readable identifier for a static byte-array field caching one command/subcommand/token's wire bytes.</summary>
    public static string WireNameFieldName(string token) => "Wire" + PascalCaseWords(SanitizeForCasing(token));

    public static string DotNetTypeName(ArgumentWireType wireType) => wireType switch
    {
        ArgumentWireType.Bytes => "ReadOnlyMemory<byte>",
        ArgumentWireType.Integer => "long",
        ArgumentWireType.Double => "double",
        ArgumentWireType.PureToken => "bool",
        _ => throw new System.ArgumentOutOfRangeException(nameof(wireType)),
    };

    /// <summary>A tuple element name (e.g. for a block group like SORT's LIMIT offset/count) — PascalCase, so reserved-keyword collisions can't occur (no keyword starts with an uppercase letter).</summary>
    public static string TupleElementName(string redisArgumentName) => PascalCaseWords(redisArgumentName);

    /// <summary>A camelCase method parameter identifier, escaped with '@' if it would otherwise collide with a reserved keyword (e.g. Redis's own "event"/"float" argument names).</summary>
    public static string ParameterIdentifier(string redisArgumentName)
    {
        var pascal = PascalCaseWords(redisArgumentName);
        var camel = pascal.Length == 0 ? "value" : char.ToLowerInvariant(pascal[0]) + pascal.Substring(1);
        return ReservedKeywords.Contains(camel) ? "@" + camel : camel;
    }

    public static string SanitizeIdentifier(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        var result = sb.ToString();
        return result.Length > 0 && char.IsDigit(result[0]) ? "_" + result : result;
    }

    /// <summary>Strips characters PascalCaseWords' '_'/'-' splitting doesn't already handle, before casing (e.g. a raw token containing other punctuation).</summary>
    private static string SanitizeForCasing(string value) =>
        new([.. value.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-')]);
}
