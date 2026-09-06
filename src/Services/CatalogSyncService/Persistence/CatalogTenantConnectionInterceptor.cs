using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MsContractor.CatalogSyncService.Persistence;

public sealed class CatalogTenantConnectionInterceptor : DbConnectionInterceptor
{
    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        if (eventData.Context is CatalogSyncDbContext { TenantAccountId: { } accountId })
        {
            command.CommandText = "SELECT set_config('app.account_id', @account_id, false)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "account_id";
            parameter.Value = accountId.ToString("D");
            command.Parameters.Add(parameter);
        }
        else
        {
            // A context without a tenant must fail closed even if pooling configuration
            // changes and a physical connection contains stale session state.
            command.CommandText = "RESET app.account_id";
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
