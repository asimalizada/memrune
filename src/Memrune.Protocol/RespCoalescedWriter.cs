using System.Buffers.Text;

namespace Memrune.Protocol;

// Owned by one sequential client session. Small responses reuse this buffer;
// large bulk responses use a temporary array so idle clients do not retain 1 MiB each.
public sealed class RespCoalescedWriter
{
    private const int MaxRetainedBytes = 64 * 1024;
    private static readonly byte[] NullBulk = "$-1\r\n"u8.ToArray();
    private byte[] _buffer = new byte[256];

    public Task WriteAsync(Stream stream, RespValue value, CancellationToken cancellationToken = default)
    {
        byte[] buffer;
        int length;
        switch (value.Kind)
        {
            case RespKind.BulkString:
            {
                byte[] data = value.Data ?? throw new ArgumentException("Bulk string data is required.");
                buffer = BufferFor(data.Length + 32);
                int offset = 0;
                buffer[offset++] = (byte)'$';
                if (!Utf8Formatter.TryFormat(data.Length, buffer.AsSpan(offset), out int digits))
                    throw new InvalidOperationException("Bulk length formatting failed.");
                offset += digits;
                WriteCrLf(buffer, ref offset);
                data.CopyTo(buffer.AsSpan(offset));
                offset += data.Length;
                WriteCrLf(buffer, ref offset);
                length = offset;
                break;
            }
            case RespKind.NullBulkString:
                buffer = NullBulk;
                length = buffer.Length;
                break;
            case RespKind.SimpleString:
            case RespKind.Error:
            {
                byte[] line = value.Data ?? throw new ArgumentException("Response text is required.");
                if (line.Contains((byte)'\r') || line.Contains((byte)'\n'))
                    throw new ArgumentException("RESP line cannot contain CR or LF.");
                buffer = BufferFor(line.Length + 3);
                buffer[0] = value.Kind == RespKind.Error ? (byte)'-' : (byte)'+';
                line.CopyTo(buffer.AsSpan(1));
                int offset = line.Length + 1;
                WriteCrLf(buffer, ref offset);
                length = offset;
                break;
            }
            case RespKind.Integer:
            {
                buffer = BufferFor(32);
                buffer[0] = (byte)':';
                if (!Utf8Formatter.TryFormat(value.Number, buffer.AsSpan(1), out int digits))
                    throw new InvalidOperationException("Integer formatting failed.");
                int offset = digits + 1;
                WriteCrLf(buffer, ref offset);
                length = offset;
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(value));
        }

        return stream.WriteAsync(buffer.AsMemory(0, length), cancellationToken).AsTask();
    }

    private byte[] BufferFor(int required)
    {
        if (required > MaxRetainedBytes) return new byte[required];
        if (_buffer.Length < required)
            _buffer = new byte[Math.Max(required, _buffer.Length * 2)];
        return _buffer;
    }

    private static void WriteCrLf(byte[] buffer, ref int offset)
    {
        buffer[offset++] = (byte)'\r';
        buffer[offset++] = (byte)'\n';
    }
}
