using System.Text;
using Memrune.Protocol;

namespace Memrune.Core;

public static class CommandExecutor
{
    public static (RespValue Response, bool CloseConnection) Execute(byte[][] arguments)
    {
        string command = Encoding.ASCII.GetString(arguments[0]);
        if (command.Equals("PING", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.Length == 1) return (RespValue.Simple("PONG"), false);
            if (arguments.Length == 2) return (RespValue.Bulk(arguments[1]), false);
            return (WrongArguments("ping"), false);
        }
        if (command.Equals("ECHO", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.Length == 2) return (RespValue.Bulk(arguments[1]), false);
            return (WrongArguments("echo"), false);
        }
        if (command.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.Length == 1) return (RespValue.Simple("OK"), true);
            return (WrongArguments("quit"), false);
        }
        return (RespValue.Error("ERR unknown command"), false);
    }

    private static RespValue WrongArguments(string command) =>
        RespValue.Error($"ERR wrong number of arguments for '{command}' command");
}