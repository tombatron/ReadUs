namespace ReadUs
{
    internal static class StandardValues
    {
        internal static readonly byte[] CarriageReturnLineFeed = new byte[] { 13, 10 };

        internal const byte BulkStringHeader = 36;
        internal const byte ArrayHeader = 42;
        internal const byte SimpleStringHeader = 43;
        internal const byte ErrorHeader = 45;
        internal const byte IntegerHeader = 58;

        internal const int HeaderTokenLength = 1;
        internal const int CrlfLength = 2;
    }
}