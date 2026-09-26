namespace ReadUs.Protocol;

/// <summary>
/// RESP data types. RESP2 servers only ever produce SimpleString, Error, Integer,
/// BulkString, Array and (via a BulkString/Array of length -1) Null. RESP3 adds the
/// rest. ReadUs negotiates RESP3 by default (project spec §1) but the reader accepts
/// either wire format transparently since RESP3 is a superset on the byte level.
/// </summary>
public enum RespType : byte
{
    SimpleString,   // +
    Error,          // -
    Integer,        // :
    BulkString,     // $
    Array,          // *
    Null,           // _ (RESP3); RESP2 signals this as a BulkString/Array of length -1
    Boolean,        // # (RESP3)
    Double,         // , (RESP3)
    BigNumber,      // ( (RESP3)
    BulkError,      // ! (RESP3)
    VerbatimString, // = (RESP3)
    Map,            // % (RESP3) — materialized as an Items array of alternating key/value
    Set,            // ~ (RESP3) — materialized as an Items array
    Push,           // > (RESP3) — out-of-band message, not correlated to a request
}
