using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Govor.Application.Tests.Support;

// Inject a provider-independent command failure after schema/seed initialization.
internal sealed class FailDatabaseCommand(Func<string, bool>? matches = null, Func<Task>? beforeFailure = null) : DbCommandInterceptor
{
    private readonly Func<string, bool> _matches = matches ?? (_ => true);
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (_matches(command.CommandText))
        {
            if (beforeFailure is not null) await beforeFailure();
            throw new InvalidOperationException("Simulated database failure.");
        }
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (_matches(command.CommandText))
        {
            if (beforeFailure is not null) await beforeFailure();
            throw new InvalidOperationException("Simulated database failure.");
        }
        return result;
    }
}

