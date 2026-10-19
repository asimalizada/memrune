using System.Buffers.Text;
using System.Text;
using Memrune.Protocol;

namespace Memrune.Core;

public sealed class CommandExecutor(KeyValueStore store)
{
    private static readonly RespValue InvalidInteger = RespValue.Error("ERR value is not an integer or out of range");
    private static readonly RespValue SyntaxError = RespValue.Error("ERR syntax error");

    public (RespValue Response, bool CloseConnection) Execute(byte[][] arguments)
    {
        ReadOnlySpan<byte> command = arguments[0];
        if (IsCommand(command, "PING"u8))
        {
            if (arguments.Length == 1) return (RespValue.Simple("PONG"), false);
            if (arguments.Length == 2) return (RespValue.Bulk(arguments[1]), false);
            return (WrongArguments("ping"), false);
        }
        if (IsCommand(command, "ECHO"u8))
            return (arguments.Length == 2 ? RespValue.Bulk(arguments[1]) : WrongArguments("echo"), false);
        if (IsCommand(command, "QUIT"u8))
            return arguments.Length == 1
                ? (RespValue.Simple("OK"), true)
                : (WrongArguments("quit"), false);
        if (IsCommand(command, "SET"u8))
            return (Set(arguments), false);
        if (IsCommand(command, "GET"u8))
            return (arguments.Length == 2
                ? store.Get(arguments[1]) is byte[] value ? RespValue.Bulk(value) : RespValue.NullBulk()
                : WrongArguments("get"), false);
        if (IsCommand(command, "DEL"u8))
            return (arguments.Length >= 2
                ? RespValue.Integer(store.Delete(arguments.AsSpan(1)))
                : WrongArguments("del"), false);
        if (IsCommand(command, "EXISTS"u8))
            return (arguments.Length >= 2
                ? RespValue.Integer(store.Exists(arguments.AsSpan(1)))
                : WrongArguments("exists"), false);
        if (IsCommand(command, "EXPIRE"u8))
            return (Expire(arguments), false);
        if (IsCommand(command, "TTL"u8))
            return (arguments.Length == 2
                ? RespValue.Integer(store.Ttl(arguments[1]))
                : WrongArguments("ttl"), false);
        return (RespValue.Error("ERR unknown command"), false);
    }

    private RespValue Set(byte[][] arguments)
    {
        if (arguments.Length is not (3 or 5))
            return WrongArguments("set");
        long? milliseconds = null;
        if (arguments.Length == 5)
        {
            bool ex = EqualsAscii(arguments[3], "EX");
            bool px = EqualsAscii(arguments[3], "PX");
            if (!ex && !px) return SyntaxError;
            if (!TryParseLong(arguments[4], out long amount)) return InvalidInteger;
            if (amount <= 0) return RespValue.Error("ERR invalid expire time in 'set' command");
            if (ex)
            {
                if (amount > long.MaxValue / 1000) return InvalidInteger;
                milliseconds = amount * 1000;
            }
            else milliseconds = amount;
        }
        return store.Set(arguments[1], arguments[2], milliseconds)
            ? RespValue.Simple("OK") : InvalidInteger;
    }

    private RespValue Expire(byte[][] arguments)
    {
        if (arguments.Length != 3) return WrongArguments("expire");
        if (!TryParseLong(arguments[2], out long seconds)) return InvalidInteger;
        return store.Expire(arguments[1], seconds) switch
        {
            ExpireResult.Missing => RespValue.Integer(0),
            ExpireResult.Set => RespValue.Integer(1),
            _ => InvalidInteger
        };
    }

    private static bool IsCommand(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expectedUppercase)
    {
        if (actual.Length != expectedUppercase.Length) return false;
        for (int i = 0; i < actual.Length; i++)
        {
            byte letter = actual[i];
            if (letter is >= (byte)'a' and <= (byte)'z')
                letter -= (byte)('a' - 'A');
            if (letter != expectedUppercase[i]) return false;
        }
        return true;
    }
    private static bool EqualsAscii(byte[] value, string expected) =>
        Encoding.ASCII.GetString(value).Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseLong(byte[] value, out long number) =>
        Utf8Parser.TryParse(value, out number, out int consumed) && consumed == value.Length;

    private static RespValue WrongArguments(string command) =>
        RespValue.Error($"ERR wrong number of arguments for '{command}' command");
}