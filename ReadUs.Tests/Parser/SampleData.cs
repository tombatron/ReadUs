namespace ReadUs.Parser.Tests
{
    internal static class SampleData
    {
        internal static readonly char[] SimpleString = "+OK\r\n".ToCharArray();

        internal static readonly char[] Error = "-Error Message\r\n".ToCharArray();

        internal static readonly char[] Integer = ":1000\r\n".ToCharArray();

        internal static readonly char[] BulkString = "$6\r\nfoobar\r\n".ToCharArray();

        internal static readonly char[] EmptyBulkString = "$0\r\n\r\n".ToCharArray();

        internal static readonly char[] NullBulkString = "$-1\r\n\r\n".ToCharArray();

        internal static readonly char[] EmptyArray = "*0\r\n".ToCharArray();

        internal static readonly char[] Array = "*2\r\n$3\r\nfoo\r\n$3\r\nbar\r\n".ToCharArray();

        internal static readonly char[] MixedArray = "*6\r\n:1\r\n:2\r\n:3\r\n:4\r\n$6\r\nfoobar\r\n".ToCharArray();

        internal static readonly char[] ArrayOfArrays = "*2\r\n*3\r\n:1\r\n:2\r\n:3\r\n*2\r\n+Foo\r\n-Bar\r\n".ToCharArray();

        internal static readonly char[] ArrayWithNull = "*3\r\n$3\r\nfoo\r\n$-1\r\n$3\r\nbar\r\n".ToCharArray();

        internal static readonly char[] RoleResponseFromPrimary = "*5\r\n$5\r\nslave\r\n$13\r\n192.168.86.40\r\n:7005\r\n$9\r\nconnected\r\n:1291892\r\n".ToCharArray();
    }
}
