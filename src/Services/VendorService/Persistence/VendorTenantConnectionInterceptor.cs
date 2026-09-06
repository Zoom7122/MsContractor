using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MsContractor.VendorService.Persistence;

public sealed class VendorTenantConnectionInterceptor : DbConnectionInterceptor
{
    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        if (eventData.Context is VendorDbContext { TenantAccountId: { } accountId })
        {
            command.CommandText = "SELECT set_config('app.account_id', @account_id, false)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "account_id";
            parameter.Value = accountId.ToString("D");
            command.Parameters.Add(parameter);
        }
        else
        {
            command.CommandText = "RESET app.account_id";
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
