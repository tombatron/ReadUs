using System;
using System.Collections.Generic;
using System.Linq;
using ReadUs.Parser;
using static ReadUs.Parser.Parser;
using static ReadUs.Extras.HashTools;

namespace ReadUs.ResultModels;

public class ClusterNodesResult : List<ClusterNodesResultItem>
{
    public ClusterNodesResult(byte[] rawResult) : this(Parse(rawResult))
    {
    }

    public ClusterNodesResult(ParseResult parsedResult)
    {
        Initialize(parsedResult);
    }

    public bool HasError { get; private set; }

    private void Initialize(ParseResult parseResult)
    {
        HasError = parseResult.Type == ResultType.Error;

        var startIndex = 0;

        var parsedResultArray = parseResult.Value ?? [];

        while (true)
        {
            var nextLineBreak = Array.IndexOf(parsedResultArray, '\n', startIndex + 1);

            if (nextLineBreak < 0) return;

            var rawLine = parsedResultArray[startIndex..nextLineBreak];

            startIndex = nextLineBreak;

            Add(new ClusterNodesResultItem(rawLine));
        }
    }

    public override string ToString() => string.Join("|", this.Select(x => x.ToString()));
    
    public string GetNodesSignature() => CreateMd5Hash(ToString().Replace("myself|", string.Empty));
}

public class ClusterNodesResultItem
{
    public ClusterNodesResultItem(char[] rawLine)
    {
        if (rawLine.Length == 0)
        {
            return;
        }

        var startIndex = 0;

        var nextSpaceIndex = Array.IndexOf(rawLine, ' ', startIndex);

        Id = rawLine[startIndex..nextSpaceIndex];

        startIndex = nextSpaceIndex + 1;
        nextSpaceIndex = Array.IndexOf(rawLine, ' ', startIndex);

        Address = rawLine[startIndex..nextSpaceIndex];

        startIndex = nextSpaceIndex + 1;
        nextSpaceIndex = Array.IndexOf(rawLine, ' ', startIndex);

        Flags = rawLine[startIndex..nextSpaceIndex];

        startIndex = nextSpaceIndex + 1;
        nextSpaceIndex = Array.IndexOf(rawLine, ' ', startIndex);

        PrimaryId = rawLine[startIndex..nextSpaceIndex];

        startIndex = nextSpaceIndex + 1;
        nextSpaceIndex = Array.IndexOf(rawLine, ' ', startIndex);

        PingSent = long.Parse(rawLine[startIndex..nextSpaceIndex]);

        startIndex = nextSpaceIndex + 1;
        nextSpaceIndex = Array.IndexOf(rawLine, ' ', startIndex);

        PongReceived = long.Parse(rawLine[startIndex..nextSpaceIndex]);

        startIndex = nextSpaceIndex + 1;
        nextSpaceIndex = Array.IndexOf(rawLine, ' ', startIndex);

        ConfigEpoch = int.Parse(rawLine[startIndex..nextSpaceIndex]);

        startIndex = nextSpaceIndex + 1;
        nextSpaceIndex = Array.IndexOf(rawLine, ' ', startIndex);

        LinkState = rawLine[startIndex..(nextSpaceIndex == -1 ? rawLine.Length - 1 : nextSpaceIndex)];

        // If the `nextSpaceIndex` is -1 at this point this is a secondary node and won't have `slots` defined.
        if (nextSpaceIndex == -1) return;

        startIndex = nextSpaceIndex + 1;

        Slots = rawLine[startIndex..];
    }

    public ClusterNodeId? Id { get; }

    public ClusterNodeAddress? Address { get; internal set; }

    public ClusterNodeFlags? Flags { get; }

    /// <summary>
    /// This will be null if this node is a primary.
    /// </summary>
    public ClusterNodePrimaryId? PrimaryId { get; private set; }

    public long PingSent { get; private set; }

    public long PongReceived { get; private set; }

    public int ConfigEpoch { get; private set; }

    public ClusterNodeLinkState? LinkState { get; private set; }

    public ClusterSlots? Slots { get; }

    private const string NoId = "NOID";
    private const string NoAddress = "NOADDRESS";
    private const string NoFlags = "NOFLAGS";
    private const string NoSlots = "NOSLOTS";
    
    public override string ToString() =>
        $"{Id ?? NoId}:{Address ?? NoAddress}:{Flags ?? NoFlags}:{Slots ?? NoSlots}";
}