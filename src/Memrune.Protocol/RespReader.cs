using System.Globalization;
using System.Text;

namespace Memrune.Protocol;

// Unconsumed bytes stay in this reader across commands, including pipelined input.
public sealed class RespReader(Stream stream)
{
    public const int MaxArguments = 1024;
    public const int MaxBulkLength = 1024 * 1024;
    public const int MaxFrameLength = 4 * 1024 * 1024;
    private const int MaxNumberLength = 20;

    private readonly byte[] _buffer = new byte[4096];
    private int _position;
    private int _length;

    public async Task<byte[][]?> ReadCommandAsync(CancellationToken cancellationToken = default)
    {
        int prefix = await ReadByteAsync(cancellationToken);
        if (prefix == -1) return null;
        if (prefix != '*') throw new RespProtocolException("expected an array of bulk strings");

        int count = await ReadNumberAsync(cancellationToken);
        if (count is < 1 or > MaxArguments)
            throw new RespProtocolException($"argument count must be between 1 and {MaxArguments}");

        var arguments = new byte[count][];
        int frameLength = 0;
        for (int i = 0; i < count; i++)
        {
            if (await ReadRequiredByteAsync(cancellationToken) != '$')
                throw new RespProtocolException("expected a bulk string argument");

            int size = await ReadNumberAsync(cancellationToken);
            if (size is < 0 or > MaxBulkLength)
                throw new RespProtocolException($"bulk string length must be between 0 and {MaxBulkLength}");
            frameLength += size;
            if (frameLength > MaxFrameLength)
                throw new RespProtocolException($"command payload exceeds {MaxFrameLength} bytes");

            var value = new byte[size];
            await ReadExactlyAsync(value, cancellationToken);
            if (await ReadRequiredByteAsync(cancellationToken) != '\r' ||
                await ReadRequiredByteAsync(cancellationToken) != '\n')
                throw new RespProtocolException("bulk string must end with CRLF");
            arguments[i] = value;
        }

        return arguments;
    }

    private async Task<int> ReadNumberAsync(CancellationToken cancellationToken)
    {
        var digits = new byte[MaxNumberLength];
        int length = 0;
        while (true)
        {
            int value = await ReadRequiredByteAsync(cancellationToken);
            if (value == '\r')
            {
                if (await ReadRequiredByteAsync(cancellationToken) != '\n')
                    throw new RespProtocolException("line must end with CRLF");
                break;
            }
            if (length == digits.Length || value is < '0' or > '9')
                throw new RespProtocolException("invalid RESP length");
            digits[length++] = (byte)value;
        }
        if (length == 0 || !int.TryParse(Encoding.ASCII.GetString(digits, 0, length),
                NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            throw new RespProtocolException("invalid RESP length");
        return number;
    }

    private async Task ReadExactlyAsync(byte[] destination, CancellationToken cancellationToken)
    {
        int copied = 0;
        while (copied < destination.Length)
        {
            if (_position == _length && await FillAsync(cancellationToken) == 0)
                throw new RespProtocolException("unexpected end of input");
            int take = Math.Min(_length - _position, destination.Length - copied);
            _buffer.AsSpan(_position, take).CopyTo(destination.AsSpan(copied));
            _position += take;
            copied += take;
        }
    }

    private async Task<int> ReadRequiredByteAsync(CancellationToken cancellationToken)
    {
        int value = await ReadByteAsync(cancellationToken);
        if (value == -1) throw new RespProtocolException("unexpected end of input");
        return value;
    }

    private async Task<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (_position == _length && await FillAsync(cancellationToken) == 0) return -1;
        return _buffer[_position++];
    }

    private async Task<int> FillAsync(CancellationToken cancellationToken)
    {
        _position = 0;
        _length = await stream.ReadAsync(_buffer, cancellationToken);
        return _length;
    }
}