namespace Memrune.Protocol;

public enum RespKind { SimpleString, BulkString, NullBulkString, Integer, Error }

public sealed record RespValue(RespKind Kind, byte[]? Data = null, long Number = 0)
{
    public static RespValue Simple(string text) => new(RespKind.SimpleString, System.Text.Encoding.ASCII.GetBytes(text));
    public static RespValue Bulk(byte[] data) => new(RespKind.BulkString, data);
    public static RespValue NullBulk() => new(RespKind.NullBulkString);
    public static RespValue Integer(long number) => new(RespKind.Integer, Number: number);
    public static RespValue Error(string text) => new(RespKind.Error, System.Text.Encoding.ASCII.GetBytes(text));
}