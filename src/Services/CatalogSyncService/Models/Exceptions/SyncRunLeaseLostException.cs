namespace MsContractor.CatalogSyncService.Models.Exceptions;

public sealed class SyncRunLeaseLostException(string message) : Exception(message);
