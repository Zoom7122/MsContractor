namespace MsContractor.CatalogSyncService.Models.Exceptions;

public sealed class SyncRunAlreadyOwnedException(string message) : Exception(message);
