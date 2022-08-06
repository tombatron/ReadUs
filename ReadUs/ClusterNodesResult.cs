using System;
using System.Collections;
using System.Collections.Generic;
using ReadUs.Parser;

namespace ReadUs
{
    public class ClusterNodesResult : IEnumerable<ClusterNodesResultItem>
    {
        private readonly ParseResult _parsedResult;

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

    public class SlotRange
    {
        public int BeginRange { get; }
        
        public int EndRange { get; }
    }

    public class ClusterNodesResultItem
    {
        public ClusterNodesResultItem(char[] rawLine) => 
            InitializeValues(rawLine);
        
        public ClusterNodeId Id { get; private set; }

        public ClusterNodeAddress Address { get; private set; }
        
        public string[] Flags { get; private set; }
        
        /// <summary>
        /// This will be null if this node is a primary.
        /// </summary>
        public string PrimaryId { get; private set; }
        
        public long PingSent { get; private set; }
        
        public long PongReceived { get; private set; }
        
        public int ConfigEpoch { get; private set; }
        
        public string LinkState { get; private set; }
        
        public SlotRange[] SlotRanges { get; private set; }

        private void InitializeValues(char[] rawLine)
        {
            var startIndex = 0;
            
            var nextSpaceIndex = Array.IndexOf(rawLine, ' ', startIndex);

            Id = rawLine[startIndex..nextSpaceIndex];

            startIndex = nextSpaceIndex + 1;
            nextSpaceIndex = Array.IndexOf(rawLine, ' ', startIndex);

            Address = rawLine[startIndex..nextSpaceIndex];
        }
    }
}