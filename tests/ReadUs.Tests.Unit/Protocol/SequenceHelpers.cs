using System.Buffers;

namespace ReadUs.Tests.Unit.Protocol;

/// <summary>
/// Builds single- and multi-segment <see cref="ReadOnlySequence{T}"/> instances so
/// protocol tests can exercise both the common (single pipe segment) path and the
/// path where a frame straddles a segment boundary, which is exactly the case a
/// live <see cref="System.IO.Pipelines.PipeReader"/> produces under partial reads.
/// </summary>
internal static class SequenceHelpers
{
    public static ReadOnlySequence<byte> Single(byte[] data) => new(data);

    public static ReadOnlySequence<byte> Segmented(params byte[][] chunks)
    {
        if (chunks.Length == 0)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        var first = new Segment(chunks[0], 0);
        var last = first;

        for (int i = 1; i < chunks.Length; i++)
        {
            last = last.Append(chunks[i]);
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(byte[] data, long runningIndex)
        {
            Memory = data;
            RunningIndex = runningIndex;
        }

        public Segment Append(byte[] data)
        {
            var segment = new Segment(data, RunningIndex + Memory.Length);
            Next = segment;
            return segment;
        }
    }
}
