﻿using System;
using System.Text;
using static ReadUs.Parser.ProtocolHeaders;

namespace ReadUs.Parser;

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

    public static bool TryParse(Span<char> rawResult, out ParseResult result)
    {
        try
        {
            result = Parse(rawResult);
            return true;
        }
        catch (Exception ex) // TODO: Catch whatever exception we decide to throw there in the default case of the parse method north of here.
        {
            result = default;
            return false;
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

        var bulkStringContent =
            rawBulkString.Slice(TokenLength + bulkStringLength.Length + CarriageReturnLineFeedLength,
                bulkStringLengthInt);

        var totalRawLength = TokenLength + bulkStringLength.Length + CarriageReturnLineFeedLength +
                             bulkStringLengthInt + CarriageReturnLineFeedLength;

        return new ParseResult(ResultType.BulkString, bulkStringContent.ToArray(), totalRawLength);
    }

    private static ParseResult HandleArray(Span<char> rawArray)
    {
        // We need to parse an array. Let's first find out where the first carriage return
        // is. Once we have that we can determine how many items are present in this array.
        var firstCarriageReturn = rawArray.IndexOf('\r') - 1;

        // We know how many characters make up the array token as well as how many characters
        // make up the first carriage return. We're going to take the slice in between to 
        // get the character value of the array length.
        var arrayLength = rawArray.Slice(1, firstCarriageReturn);

        // Now we're going to parse it...
        var arrayLengthInt = int.Parse(arrayLength);

        // We're initializing the `totalRawLength` variable such that when we start looking
        // at what has composed the array, we start AFTER the array header. 
        var totalRawLength = TokenLength + arrayLength.Length + CarriageReturnLineFeedLength;

        // Here we are initializing the array that will hold the result of the parsing of this
        // array...
        var parsedArray = new ParseResult[arrayLengthInt];
        var parsedArrayMembers = 0;

        for (var i = 0; i < arrayLengthInt; i++)
        {
            // Recursive call to the parse method to handle the each item within the array.
            var parsedResult = Parse(rawArray.Slice(totalRawLength));

            parsedArray[i] = parsedResult;

            totalRawLength += parsedResult.TotalRawLength;

            parsedArrayMembers++;

            if (totalRawLength == rawArray.Length)
            {
                // We've made it to the end of the character array, we've now got to check to see
                // if all we've got left is the final item in the array, if so, we'll go ahead and
                // increment the parsed array members counter because for now we're assuming that 
                // the final item in the array is nil.
                if (parsedArrayMembers == arrayLengthInt - 1) parsedArrayMembers++;

                break;
            }
        }

        if (parsedArrayMembers != arrayLengthInt)
            throw new Exception("You ain't got the whole array dawg."); // TODO: Custom exception.

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