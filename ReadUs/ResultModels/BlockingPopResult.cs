using System;
using ReadUs.Parser;

namespace ReadUs.ResultModels;

public class BlockingPopResult
{
    public BlockingPopResult(string listKey, string value)
    {
        ListKey = listKey;
        Value = value;
    }

    public string ListKey { get; }

    public string Value { get; }

    public static explicit operator BlockingPopResult(ParseResult result)
    {
        if (result.TryToArray(out var resultArray))
        {
            var listKey = resultArray[0];
            var value = resultArray[1];

            return new BlockingPopResult(listKey.ToString(), value.ToString());
        }

        // TODO: Throw custom exception here.
        throw new Exception("We expected a result that was a multi-bulk here.");
    }
}