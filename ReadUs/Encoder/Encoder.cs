using System.Text;

namespace ReadUs.Encoder
{
    public static class Encoder
    {
        internal const string CarriageReturnLineFeed = "\r\n";
        internal const string NullBulkString = "$-1\r\n\r\n";

        public static byte[] Encode(params object[] items)
        {
            var result = new StringBuilder();

            if (items == default)
            {
                result.Append(NullBulkString);
            }
            else
            {
                if (items?.Length > 1)
                {
                    result.Append('*');
                    result.Append(items.Length.ToString());
                    result.Append(CarriageReturnLineFeed);
                }

                foreach (var item in items)
                {
                    result.Append(CreateBulkString(item));
                }
            }

            return Encoding.ASCII.GetBytes(result.ToString());
        }

        private static string CreateBulkString(object item)
        {
            if (item == default)
            {
                return NullBulkString;
            }

            var result = new StringBuilder();

            string s;

            if (item is RedisKey key)
            {
                s = key.Name;
            }
            else
            {
                s = item.ToString();
            }

            result.Append('$');
            result.Append(s.Length.ToString());
            result.Append(CarriageReturnLineFeed);
            result.Append(s);
            result.Append(CarriageReturnLineFeed);

            return result.ToString();
        }
    }
}