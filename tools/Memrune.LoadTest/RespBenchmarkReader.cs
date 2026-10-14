using System.Text;

// One instance per TCP connection. The receive and payload buffers are reused
// for every validated reply, including fragmented and pipelined responses.
public sealed class RespBenchmarkReader(Stream stream, int maxPayload)
{
    private readonly byte[] _receive = new byte[8192];
    private readonly byte[] _payload = new byte[Math.Max(maxPayload, 64)];
    private int _position;
    private int _length;

    public async Task ReadSimpleAsync(ReadOnlyMemory<byte> expected, CancellationToken cancellationToken)
    {
        int prefix = await ReadRequiredByteAsync(cancellationToken);
        if (prefix == '-') throw await ServerErrorAsync(cancellationToken);
        if (prefix != '+') throw new InvalidDataException($"Expected simple string, got RESP prefix {(char)prefix}.");
        for (int i = 0; i < expected.Length; i++)
        {
            byte item = expected.Span[i];
            if (await ReadRequiredByteAsync(cancellationToken) != item)
                throw new InvalidDataException("Incorrect simple-string response.");
        }
    }

    public async Task ReadBulkAsync(ReadOnlyMemory<byte> expected, CancellationToken cancellationToken)
    {
        int prefix = await ReadRequiredByteAsync(cancellationToken);
        if (prefix == '-') throw await ServerErrorAsync(cancellationToken);
        if (prefix != '$') throw new InvalidDataException($"Expected bulk string, got RESP prefix {(char)prefix}.");

        int length = await ReadLengthAsync(cancellationToken);
        if (length != expected.Length)
            throw new InvalidDataException($"Bulk length {length} differs from expected {expected.Length}.");
        await ReadExactlyAsync(_payload.AsMemory(0, length), cancellationToken);
        if (!_payload.AsSpan(0, length).SequenceEqual(expected.Span))
            throw new InvalidDataException("Incorrect bulk-string payload.");
        if (await ReadRequiredByteAsync(cancellationToken) != '\r' ||
            await ReadRequiredByteAsync(cancellationToken) != '\n')
            throw new InvalidDataException("Bulk response lacks CRLF.");
    }

    public async Task<long> ReadIntegerAsync(CancellationToken cancellationToken)
    {
        int prefix = await ReadRequiredByteAsync(cancellationToken);
        if (prefix == '-') throw await ServerErrorAsync(cancellationToken);
        if (prefix != ':') throw new InvalidDataException($"Expected integer, got RESP prefix {(char)prefix}.");
        long number = 0;
        bool hasDigit = false;
        while (true)
        {
            int item = await ReadRequiredByteAsync(cancellationToken);
            if (item == '\r')
            {
                if (await ReadRequiredByteAsync(cancellationToken) != '\n' || !hasDigit)
                    throw new InvalidDataException("Invalid integer response.");
                return number;
            }
            if (item is < '0' or > '9')
                throw new InvalidDataException("Invalid integer response.");
            hasDigit = true;
            number = checked(number * 10 + item - '0');
        }
    }

    private async Task<int> ReadLengthAsync(CancellationToken cancellationToken)
    {
        int number = 0;
        bool hasDigit = false;
        while (true)
        {
            int item = await ReadRequiredByteAsync(cancellationToken);
            if (item == '\r')
            {
                if (await ReadRequiredByteAsync(cancellationToken) != '\n' || !hasDigit)
                    throw new InvalidDataException("Invalid bulk length.");
                return number;
            }
            if (item is < '0' or > '9')
                throw new InvalidDataException("Invalid bulk length.");
            hasDigit = true;
            number = checked(number * 10 + item - '0');
            if (number > _payload.Length)
                throw new InvalidDataException("Bulk response exceeds benchmark limit.");
        }
    }

    private async Task<InvalidDataException> ServerErrorAsync(CancellationToken cancellationToken)
    {
        var message = new StringBuilder();
        while (message.Length < 256)
        {
            int item = await ReadRequiredByteAsync(cancellationToken);
            if (item == '\r')
            {
                if (await ReadRequiredByteAsync(cancellationToken) == '\n')
                    return new InvalidDataException($"Server error: {message}");
                throw new InvalidDataException("Invalid error response.");
            }
            message.Append((char)item);
        }
        throw new InvalidDataException("Server error response is too long.");
    }

    private async Task ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        int copied = 0;
        while (copied < destination.Length)
        {
            if (_position == _length && await FillAsync(cancellationToken) == 0)
                throw new EndOfStreamException("Incomplete RESP response.");
            int take = Math.Min(_length - _position, destination.Length - copied);
            _receive.AsMemory(_position, take).CopyTo(destination[copied..]);
            _position += take;
            copied += take;
        }
    }

    private async Task<int> ReadRequiredByteAsync(CancellationToken cancellationToken)
    {
        if (_position == _length && await FillAsync(cancellationToken) == 0)
            throw new EndOfStreamException("Incomplete RESP response.");
        return _receive[_position++];
    }

    private async Task<int> FillAsync(CancellationToken cancellationToken)
    {
        _position = 0;
        _length = await stream.ReadAsync(_receive, cancellationToken);
        return _length;
    }
}