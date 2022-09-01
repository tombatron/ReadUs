using System;
using System.Text;
using static ReadUs.Parser.ProtocolHeaders;

namespace ReadUs.Parser
{
    public static class Parser
    {
        private const int TokenLength = 1;
        private const int CarriageReturnLineFeedLength = 2;

        public static ParseResult Parse(Span<byte> rawResult)
        {
            var chars = Encoding.ASCII.GetString(rawResult).ToCharArray();

            return Parse(chars);
        }

        public static ParseResult Parse(Span<char> rawResult)
        {
            switch (rawResult[0])
            {
                case SimpleString:
                    return HandleSimpleString(rawResult);
                case Error:
                    return HandleError(rawResult);
                case Integer:
                    return HandleInteger(rawResult);
                case BulkString:
                    return HandleBulkString(rawResult);
                case ProtocolHeaders.Array:
                    return HandleArray(rawResult);
                default:
                    // TODO: Create and throw an appropriate exception here. 
                    throw new Exception("(╯°□°）╯︵ ┻━┻");
            }
        }

        private static ParseResult HandleSimpleString(Span<char> rawSimpleString) =>
            SimpleValueParse(ResultType.SimpleString, rawSimpleString);

        private static ParseResult HandleError(Span<char> rawError) =>
            SimpleValueParse(ResultType.Error, rawError);

        private static ParseResult HandleInteger(Span<char> rawInteger) =>
            SimpleValueParse(ResultType.Integer, rawInteger);

        private static ParseResult HandleBulkString(Span<char> rawBulkString)
        {
            var firstCarriageReturn = rawBulkString.IndexOf('\r') - 1;
            var bulkStringLength = rawBulkString.Slice(1, firstCarriageReturn);
            var bulkStringLengthInt = int.Parse(bulkStringLength);

            if (bulkStringLengthInt == -1)
            {
                return new ParseResult(ResultType.BulkString, null, 5);
            }
            else
            {
                var bulkStringContent = rawBulkString.Slice(TokenLength + bulkStringLength.Length + CarriageReturnLineFeedLength, bulkStringLengthInt);

                var totalRawLength = TokenLength + bulkStringLength.Length + CarriageReturnLineFeedLength + bulkStringLengthInt + CarriageReturnLineFeedLength;

                return new ParseResult(ResultType.BulkString, bulkStringContent.ToArray(), totalRawLength);
            }
        }

        private static ParseResult HandleArray(Span<char> rawArray)
        {
            var firstCarriageReturn = rawArray.IndexOf('\r') - 1;
            var arrayLength = rawArray.Slice(1, firstCarriageReturn);
            var arrayLengthInt = int.Parse(arrayLength);

            var totalRawLength = TokenLength + arrayLength.Length + CarriageReturnLineFeedLength;

            var parsedArray = new ParseResult[arrayLengthInt];

            for (var i = 0; i < arrayLengthInt; i++)
            {
                var parsedResult = Parse(rawArray.Slice(totalRawLength));

                parsedArray[i] = parsedResult;

                totalRawLength += parsedResult.TotalRawLength;

                if (totalRawLength == rawArray.Length)
                {
                    break;
                }
            }

            return new ParseResult(ResultType.Array, rawArray.ToArray(), totalRawLength, parsedArray);
        }

        private static ParseResult SimpleValueParse(ResultType type, Span<char> rawValue)
        {
            var firstCarriageReturn = rawValue.IndexOf('\r') - 1;
            var simpleValueContent = rawValue.Slice(1, firstCarriageReturn);

            var totalRawLength = TokenLength + simpleValueContent.Length + CarriageReturnLineFeedLength;

            return new ParseResult(type, simpleValueContent.ToArray(), totalRawLength);
        }
    }
}
