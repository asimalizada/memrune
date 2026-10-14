namespace Memrune.Core;

internal sealed class ByteArrayComparer : IEqualityComparer<byte[]>
{
    public bool Equals(byte[]? x, byte[]? y) =>
        ReferenceEquals(x, y) || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));

    public int GetHashCode(byte[] value)
    {
        // A stable content hash; collisions are resolved by Equals.
        unchecked
        {
            uint hash = 2166136261;
            foreach (byte item in value)
                hash = (hash ^ item) * 16777619;
            return (int)hash;
        }
    }
}