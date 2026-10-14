namespace Memrune.Core;

public enum ExpireResult { Missing, Set, OutOfRange }

// All dictionary and expiration operations are synchronous and protected by one lock.
// This keeps multi-key commands and same-key mutations atomic at the current scale.
public sealed class KeyValueStore(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<byte[], Entry> _entries = new(new ByteArrayComparer());
    private readonly object _gate = new();

    public bool Set(byte[] key, byte[] value, long? expirationMilliseconds = null)
    {
        lock (_gate)
        {
            long? deadline = null;
            long now = _time.GetUtcNow().UtcTicks;
            if (expirationMilliseconds is long milliseconds)
            {
                if (milliseconds <= 0 || !TryDeadline(milliseconds, now, out long calculated))
                    return false;
                deadline = calculated;
            }

            // Dictionary keys and values are owned by the store. Replacing an
            // existing value retains its already-owned key and clears old TTL.
            if (_entries.TryGetValue(key, out Entry? existing))
            {
                existing.Value = value.ToArray();
                existing.ExpiresAtUtcTicks = deadline;
            }
            else
            {
                _entries.Add(key.ToArray(), new Entry(value.ToArray()) { ExpiresAtUtcTicks = deadline });
            }
            return true;
        }
    }

    public byte[]? Get(byte[] key)
    {
        lock (_gate)
        {
            return TryGetLive(key, _time.GetUtcNow().UtcTicks, out Entry? entry)
                ? entry!.Value.ToArray() : null;
        }
    }

    public long Delete(ReadOnlySpan<byte[]> keys)
    {
        lock (_gate)
        {
            long removed = 0;
            long now = _time.GetUtcNow().UtcTicks;
            foreach (byte[] key in keys)
                if (TryGetLive(key, now, out _) && _entries.Remove(key))
                    removed++;
            return removed;
        }
    }

    public long Exists(ReadOnlySpan<byte[]> keys)
    {
        lock (_gate)
        {
            long found = 0;
            long now = _time.GetUtcNow().UtcTicks;
            foreach (byte[] key in keys)
                if (TryGetLive(key, now, out _))
                    found++; // Redis counts repeated existing keys repeatedly.
            return found;
        }
    }

    public ExpireResult Expire(byte[] key, long seconds)
    {
        lock (_gate)
        {
            long now = _time.GetUtcNow().UtcTicks;
            if (!TryGetLive(key, now, out Entry? entry))
                return ExpireResult.Missing;
            if (seconds <= 0)
            {
                _entries.Remove(key);
                return ExpireResult.Set;
            }
            if (seconds > long.MaxValue / TimeSpan.TicksPerSecond ||
                !TryDeadline(seconds * 1000, now, out long deadline))
                return ExpireResult.OutOfRange;
            entry!.ExpiresAtUtcTicks = deadline;
            return ExpireResult.Set;
        }
    }

    public long Ttl(byte[] key)
    {
        lock (_gate)
        {
            long now = _time.GetUtcNow().UtcTicks;
            if (!TryGetLive(key, now, out Entry? entry))
                return -2;
            return entry!.ExpiresAtUtcTicks is long deadline
                ? (deadline - now) / TimeSpan.TicksPerSecond
                : -1;
        }
    }

    private bool TryGetLive(byte[] key, long now, out Entry? entry)
    {
        if (!_entries.TryGetValue(key, out entry))
            return false;
        if (entry.ExpiresAtUtcTicks is long deadline && deadline <= now)
        {
            _entries.Remove(key);
            entry = null;
            return false;
        }
        return true;
    }

    private static bool TryDeadline(long milliseconds, long now, out long deadline)
    {
        if (milliseconds > (DateTimeOffset.MaxValue.Ticks - now) / TimeSpan.TicksPerMillisecond)
        {
            deadline = 0;
            return false;
        }
        deadline = now + milliseconds * TimeSpan.TicksPerMillisecond;
        return true;
    }

    private sealed class Entry(byte[] value)
    {
        public byte[] Value { get; set; } = value;
        public long? ExpiresAtUtcTicks { get; set; }
    }
}