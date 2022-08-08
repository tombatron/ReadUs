using System;
using System.Collections;
using System.Collections.Generic;
using ReadUs.Parser;
using static ReadUs.Parser.Parser;

namespace ReadUs
{
    public class ClusterNodesResult : IEnumerable<ClusterNodesResultItem>
    {
        private readonly ParseResult _parsedResult;

        public ClusterNodesResult(byte[] rawResult) : this(Parse(rawResult))
        {
        }

        public ClusterNodesResult(ParseResult parsedResult) =>
            _parsedResult = parsedResult;

        public IEnumerator<ClusterNodesResultItem> GetEnumerator() => InternalIterator().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private IEnumerable<ClusterNodesResultItem> InternalIterator()
        {
            var startIndex = 0;

            while (true)
            {
                var nextLineBreak = Array.IndexOf(_parsedResult.Value, '\n', startIndex + 1);

                if (nextLineBreak < 0)
                {
                    yield break;
                }

                var rawLine = _parsedResult.Value[startIndex..nextLineBreak];
                
                startIndex = nextLineBreak;

                yield return new ClusterNodesResultItem(rawLine);
            }
        }
    }

    public class ClusterNodesResultItem
    {
        public ClusterNodesResultItem(char[] rawLine) => 
            InitializeValues(rawLine);
        
        public ClusterNodeId Id { get; private set; }

        public ClusterNodeAddress Address { get; private set; }
        
        public ClusterNodeFlags Flags { get; private set; }
        
        /// <summary>
        /// This will be null if this node is a primary.
        /// </summary>
        public ClusterNodePrimaryId PrimaryId { get; private set; }
        
        public long PingSent { get; private set; }
        
        public long PongReceived { get; private set; }
        
        public int ConfigEpoch { get; private set; }
        
        public ClusterNodeLinkState LinkState { get; private set; }
        
        public ClusterSlots Slots { get; private set; }

        private void InitializeValues(char[] rawLine)
        {
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
            if (nextSpaceIndex == -1)
            {
                return;
            }

            startIndex = nextSpaceIndex + 1;

            Slots = rawLine[startIndex..];
        }
    }
}