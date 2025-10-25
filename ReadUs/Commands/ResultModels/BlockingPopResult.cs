using ReadUs.Exceptions;
using ReadUs.Parser;

namespace ReadUs.Commands.ResultModels;

public class BlockingPopResult(string listKey, string value)
{
    public string ListKey { get; } = listKey;

    public string Value { get; } = value;

    public static implicit operator BlockingPopResult(ParseResult result)
    {
        if (result.TryToArray(out var resultArray))
        {
            if (resultArray.Length < 2)
            {
                throw new RedisWrongTypeException("We expected a multi-bulk result with at least two items.");
            }
            
            var listKey = resultArray[0];
            var value = resultArray[1];

            return new BlockingPopResult(listKey.ToString(), value.ToString());
        }
        
        throw new RedisWrongTypeException("We expected a result that was a multi-bulk here.");
    }
}