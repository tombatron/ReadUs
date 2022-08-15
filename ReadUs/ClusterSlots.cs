using System;
using System.Collections.Generic;
using System.Linq;

namespace ReadUs
{
    public class ClusterSlots
    {
        public class SlotRange : IComparable<SlotRange>
        {
            public int Begin { get; }

            public int End { get; }

            internal SlotRange(char[] rawValue) =>
                (Begin, End) = Initialize(rawValue);

            private SlotRange(int begin, int end)
            {
                Begin = begin;
                End = end;
            }

            private (int start, int end) Initialize(char[] rawData)
            {
                var separatorPosition = Array.IndexOf(rawData, '-', 0);

                if (separatorPosition == -1)
                {
                    var slot = int.Parse(rawData);

                    return (slot, slot);
                }

                return (int.Parse(rawData[..separatorPosition]), int.Parse(rawData[(separatorPosition + 1)..]));
            }

            internal static SlotRange Create(int begin, int end) =>
                new SlotRange(begin, end);

            public override bool Equals(object obj)
            {
                if (obj is SlotRange range)
                {
                    return range.Begin == Begin && range.End == End;
                }

                return false;
            }

            public override int GetHashCode() =>
                HashCode.Combine(Begin, End);

            public int CompareTo(SlotRange other)
            {
                // If the other instance is null we'll put this instance before it.
                if (other is null)
                {
                    return -1;
                }

                // If the start range is less than the other's start range, then this one goes first.
                if (Begin < other.Begin)
                {
                    return -1;
                }

                // If the start range is greater than the other's start range, then this one will go after. 
                if (other.Begin < Begin)
                {
                    return 1;
                }

                // If the end range is less than the other's end range, this one will go before.
                if (End < other.End)
                {
                    return -1;
                }

                return other.End < End ? 1 : 0;
            }

            public static bool operator ==(SlotRange lhs, SlotRange rhs)
            {
                if (lhs is null && rhs is null)
                {
                    return true;
                }

                return lhs?.Equals(rhs) ?? false;
            }

            public static bool operator !=(SlotRange lhs, SlotRange rhs) =>
                !(lhs == rhs);

        }

        private SlotRange[] _slots;

        public ClusterSlots(params SlotRange[] slots) =>
            _slots = slots;

        public ClusterSlots(IEnumerable<SlotRange> slots) =>
            _slots = slots.ToArray();

        public bool ContainsSlot(int slotNumber)
        {
            foreach (var slot in _slots)
            {
                if (slot.Begin <= slotNumber && slot.End >= slotNumber)
                {
                    return true;
                }
            }
            return false;
        }

        public static implicit operator ClusterSlots(char[] rawValue)
        {
            int startPosition = 0;


            var slots = new List<SlotRange>();

            while (startPosition < rawValue.Length)
            {
                SlotRange range;

                int nextDelimiter = Array.IndexOf(rawValue, ' ', startPosition);

                if (nextDelimiter == -1)
                {
                    range = new SlotRange(rawValue[startPosition..]);

                    startPosition = rawValue.Length;
                }
                else
                {
                    range = new SlotRange(rawValue[startPosition..nextDelimiter]);

                    startPosition = nextDelimiter + 1;
                }

                slots.Add(range);
            }

            return new ClusterSlots(slots);
        }

        public override int GetHashCode()
        {
            if (_slots is null)
            {
                return 0;
            }

            int hashCode = 0;

            for (var i = 0; i < _slots.Length; i++)
            {
                if (i == 0)
                {
                    hashCode = _slots[i].GetHashCode();
                }
                else
                {
                    hashCode = HashCode.Combine(hashCode, _slots[i].GetHashCode());
                }
            }

            return hashCode;
        }

        public override bool Equals(object obj)
        {
            if (obj is ClusterSlots clusterSlots)
            {
                if (clusterSlots._slots.Length != _slots.Length)
                {
                    return false;
                }

                for (var i = 0; i < _slots.Length; i++)
                {
                    if (_slots[i] != clusterSlots._slots[i])
                    {
                        return false;
                    }
                }

                return true;
            }

            return false;
        }
        public static bool operator ==(ClusterSlots lhs, ClusterSlots rhs)
        {
            if (lhs is null && rhs is null)
            {
                return true;
            }

            return lhs?.Equals(rhs) ?? false;
        }

        public static bool operator !=(ClusterSlots lhs, ClusterSlots rhs) =>
            !(lhs == rhs);
    }
}