using System;
using ReadUs.Parser;

namespace ReadUs
{
    public class BlockingPopResult
    {
        public string ListKey { get; }

        public string Value { get; }

        public BlockingPopResult(string listKey, string value)
        {
            ListKey = listKey;
            Value = value;
        }

        public static explicit operator BlockingPopResult(ParseResult result)
        {
            if (result.IsArray)
            {
                var resultArray = (ParseResult[])result;

                var listKey = resultArray[0];
                var value = resultArray[1];

                return new BlockingPopResult(listKey.ToString(), value.ToString());
            }
            else
            {
                throw new Exception("We expected a result that was a multi-bulk here.");
            }
        }
    }
}
