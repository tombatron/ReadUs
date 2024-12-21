namespace ReadUs.Parser;

internal static class ProtocolHeaders
{
    internal const char SimpleString = '+';
    internal const char Error = '-';
    internal const char Integer = ':';
    internal const char BulkString = '$';
    internal const char Array = '*';
}