namespace ReadUs.SourceGenerators;

internal static class CodeGenText
{
    public static string Literal(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
