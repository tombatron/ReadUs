using System.Net;
using ReadUs.Parser;

namespace ReadUs.ResultModels;

public sealed class ReplicaDescription
{
    public ReplicaDescription(ParseResult address, ParseResult port, ParseResult currentReplicationOffset) :
        this(IPAddress.Parse(address.ToString()), int.Parse(port.ToString()),
            long.Parse(currentReplicationOffset.ToString()))
    {
    }

    public ReplicaDescription(IPAddress address, int port, long currentReplicationOffset)
    {
        Address = address;
        Port = port;
        CurrentReplicationOffset = currentReplicationOffset;
    }

    public IPAddress Address { get; }

    public int Port { get; }

    public long CurrentReplicationOffset { get; }
}