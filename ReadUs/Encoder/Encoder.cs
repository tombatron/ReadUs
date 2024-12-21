using System.Text;

namespace ReadUs.Encoder;

public static class Encoder
{
    private const string EncoderCarriageReturnLineFeed = "\r\n";
    private const string NullBulkString = "$-1\r\n\r\n";
    private static readonly byte[] NullBulkStringBytes = Encoding.ASCII.GetBytes(NullBulkString);

    public static byte[] Encode(params object?[] items)
    {
        if (items == default)
        {
            return NullBulkStringBytes;
        }
        else
        {
            var result = new StringBuilder();

            if (items.Length > 1)
            {
                result.Append('*');
                result.Append(items.Length.ToString());
                result.Append(EncoderCarriageReturnLineFeed);
            }

            foreach (var item in items)
            {
                result.Append(CreateBulkString(item));
            }

            return Encoding.ASCII.GetBytes(result.ToString());
        }
    }

    private static string CreateBulkString(object? item)
    {
        string? bulkString = item switch
        {
            RedisKey key => key.Name,
            null => null,
            _ => item.ToString()
        };

        if (bulkString is null)
        {
            return NullBulkString;
        }

        var result = new StringBuilder();

        result.Append('$');
        result.Append(bulkString.Length.ToString());
        result.Append(EncoderCarriageReturnLineFeed);
        result.Append(bulkString);
        result.Append(EncoderCarriageReturnLineFeed);

        return result.ToString();
    }
}