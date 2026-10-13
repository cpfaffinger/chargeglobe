using System.Data.Common;
using ChargeGlobe.Application.Health;
using Microsoft.Extensions.Logging;

namespace ChargeGlobe.Infrastructure.Health;

public sealed class DatabaseProbe(DbDataSource dataSource, ILogger<DatabaseProbe> logger) : IDatabaseProbe
{
    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await connection.DisposeAsync();
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Database is not reachable");
            return false;
        }
    }
}
