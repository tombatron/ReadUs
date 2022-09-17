namespace ReadUs.Parser
{
    public readonly struct ParseResult
    {
        public ResultType Type { get; }

        public char[]? Value { get; }

        internal int TotalRawLength { get; }

        private readonly ParseResult[]? _array;

        public bool IsArray => _array != default;

        internal ParseResult(ResultType type, char[]? value, int totalRawLength, ParseResult[]? array = default)
        {
            Type = type;
            Value = value;
            TotalRawLength = totalRawLength;

            _array = array;
        }

        public static explicit operator ParseResult[]?(ParseResult result) => result._array;

        public override string ToString() => new string(Value);
    }
}