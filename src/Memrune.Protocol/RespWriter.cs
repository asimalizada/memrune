using System.Globalization;
using System.Text;

namespace Memrune.Protocol;

public static class RespWriter
{
    public static async Task WriteAsync(Stream stream, RespValue value, CancellationToken cancellationToken = default)
    {
        switch (value.Kind)
        {
            case RespKind.BulkString:
                byte[] data = value.Data ?? throw new ArgumentException("Bulk string data is required.");
                await WriteAsciiAsync(stream, $"${data.Length}\r\n", cancellationToken);
                await stream.WriteAsync(data, cancellationToken);
                await WriteAsciiAsync(stream, "\r\n", cancellationToken);
                break;
            case RespKind.NullBulkString:
                await WriteAsciiAsync(stream, "$-1\r\n", cancellationToken);
                break;
            case RespKind.SimpleString:
            case RespKind.Error:
                byte[] line = value.Data ?? throw new ArgumentException("Response text is required.");
                if (line.Contains((byte)'\r') || line.Contains((byte)'\n'))
                    throw new ArgumentException("RESP line cannot contain CR or LF.");
                await stream.WriteAsync(new[] { value.Kind == RespKind.Error ? (byte)'-' : (byte)'+' }, cancellationToken);
                await stream.WriteAsync(line, cancellationToken);
                await WriteAsciiAsync(stream, "\r\n", cancellationToken);
                break;
            case RespKind.Integer:
                await WriteAsciiAsync(stream, $":{value.Number.ToString(CultureInfo.InvariantCulture)}\r\n", cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private static Task WriteAsciiAsync(Stream stream, string text, CancellationToken cancellationToken) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(text), cancellationToken).AsTask();
}