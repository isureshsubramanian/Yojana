using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Yojana.Serialisation.DatabaseTests;

public sealed class PolicyCommandCounter : DbCommandInterceptor
{
    public static readonly PolicyCommandCounter Instance = new();
    private static readonly AsyncLocal<List<string>?> Commands = new();

    public static IDisposable Capture(out List<string> commands)
    {
        commands = [];
        Commands.Value = commands;
        return new CaptureScope();
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Commands.Value?.Add(command.CommandText);
        return ValueTask.FromResult(result);
    }

    private sealed class CaptureScope : IDisposable
    {
        public void Dispose() => Commands.Value = null;
    }
}
