using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Models;
using MsContractor.CatalogSyncService.Persistence;

namespace MsContractor.DuplicatesMergeService.Repositories;

public interface IDocumentsInOperationRepository
{
    Task AppendEgressResponseAsync(
        Guid accountId,
        Guid mergeJobId,
        string operationType,
        int statusCode,
        string responseBody,
        CancellationToken cancellationToken);
}

public sealed class DocumentsInOperationRepository : IDocumentsInOperationRepository
{
    private readonly CatalogSyncDbContext _dbContext;

    public DocumentsInOperationRepository(CatalogSyncDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AppendEgressResponseAsync(
        Guid accountId,
        Guid mergeJobId,
        string operationType,
        int statusCode,
        string responseBody,
        CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        if (accountId == Guid.Empty || mergeJobId == Guid.Empty ||
            string.IsNullOrWhiteSpace(operationType) || operationType.Length > 64 ||
            statusCode is < 100 or > 599 || responseBody is null)
            throw new ArgumentException("A tenant, merge job, operation type and HTTP response are required.");

        var responseEntry = CreateResponseEntry(statusCode, responseBody);
        var row = await _dbContext.DocumentsInOperation.SingleOrDefaultAsync(
            item => item.AccountId == accountId &&
                    item.MergeJobId == mergeJobId &&
                    item.OperationType == operationType,
            cancellationToken);

        if (row is null)
        {
            row = new DocumentsInOperation
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                MergeJobId = mergeJobId,
                OperationType = operationType,
                RawJson = new JsonArray(responseEntry).ToJsonString(),
                CreatedAt = DateTimeOffset.UtcNow
            };
            _dbContext.DocumentsInOperation.Add(row);
        }
        else
        {
            var responses = JsonNode.Parse(row.RawJson) as JsonArray
                ?? throw new JsonException("Stored Egress response group is not a JSON array.");
            responses.Add(responseEntry);
            row.RawJson = responses.ToJsonString();
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static JsonObject CreateResponseEntry(int statusCode, string responseBody)
    {
        JsonNode? body;
        try
        {
            body = JsonNode.Parse(responseBody);
        }
        catch (JsonException)
        {
            body = JsonValue.Create(responseBody);
        }

        return new JsonObject
        {
            ["statusCode"] = statusCode,
            ["body"] = body
        };
    }
}
