using System;

namespace ReadUs.Parser;

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

    public bool TryToArray(out ParseResult[] array)
    {
        if (_array is not null)
        {
            array = _array;
            return true;
        }

        array = Array.Empty<ParseResult>();
        return false;
    }

    public override string ToString()
    {
        return new string(Value);
    }
}