namespace ReadUs;

internal static class StandardValues
{
    internal const byte BulkStringHeader = 36;
    internal const byte ArrayHeader = 42;
    internal const byte SimpleStringHeader = 43;
    internal const byte ErrorHeader = 45;
    internal const byte IntegerHeader = 58;

    internal const int HeaderTokenLength = 1;
    internal const int CrlfLength = 2;
    internal static readonly byte[] CarriageReturnLineFeed = "\r\n"u8.ToArray();

    internal const int MaxClusterSlots = 16_384;
}