using System.Diagnostics;
using Memrune.Core;
using Memrune.Protocol;

int iterations = args.Length == 1 && int.TryParse(args[0], out int parsed) && parsed > 0
    ? parsed : 100_000;
byte[] pingFrame = "*1\r\n$4\r\nPING\r\n"u8.ToArray();
byte[] input = new byte[checked(iterations * pingFrame.Length)];
for (int i = 0; i < iterations; i++)
    pingFrame.CopyTo(input.AsSpan(i * pingFrame.Length));

// Warm JIT paths before each measurement. This probe isolates managed component
// allocations; it does not model TCP, scheduling, or whole-server performance.
for (int i = 0; i < 10_000; i++)
    await new RespReader(new MemoryStream(pingFrame)).ReadCommandAsync();
var store = new KeyValueStore();
var executor = new CommandExecutor(store);
byte[][] ping = ["PING"u8.ToArray()];
for (int i = 0; i < 10_000; i++) executor.Execute(ping);
var writer = new RespCoalescedWriter();
for (int i = 0; i < 10_000; i++)
    await writer.WriteAsync(Stream.Null, RespValue.Simple("PONG"));

Measure("resp_reader_ping", iterations, () =>
{
    var reader = new RespReader(new MemoryStream(input, writable: false));
    for (int i = 0; i < iterations; i++)
        if (reader.ReadCommandAsync().GetAwaiter().GetResult() is not { Length: 1 })
            throw new InvalidDataException("Reader lost a request.");
});
Measure("command_executor_ping", iterations, () =>
{
    for (int i = 0; i < iterations; i++)
        if (executor.Execute(ping).Response.Kind != RespKind.SimpleString)
            throw new InvalidDataException("Executor returned the wrong reply.");
});
RespValue response = RespValue.Simple("PONG");
Measure("coalesced_writer_ping", iterations, () =>
{
    for (int i = 0; i < iterations; i++)
        writer.WriteAsync(Stream.Null, response).GetAwaiter().GetResult();
});

static void Measure(string component, int count, Action action)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    long before = GC.GetAllocatedBytesForCurrentThread();
    long started = Stopwatch.GetTimestamp();
    action();
    double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Console.WriteLine(FormattableString.Invariant($"{component},{count},{allocated},{allocated / (double)count:F1},{seconds:F3}"));
}
