namespace ReadUs;

/// <summary>
/// Placeholder cached command-name byte arrays. This is exactly what the command-table
/// source generator (project spec §3, §13 step 4) will replace with a full,
/// codegen'd surface — kept tiny and hand-written for now purely so the Tier 1
/// pool has something real to exercise in tests and benchmarks before that generator
/// exists.
/// </summary>
internal static class CommandNames
{
    public static readonly byte[] Ping = "PING"u8.ToArray();
    public static readonly byte[] Set = "SET"u8.ToArray();
    public static readonly byte[] Get = "GET"u8.ToArray();
    public static readonly byte[] Echo = "ECHO"u8.ToArray();
}
