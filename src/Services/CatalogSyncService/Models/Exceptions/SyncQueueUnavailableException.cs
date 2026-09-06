namespace MsContractor.CatalogSyncService.Models.Exceptions;

public sealed class SyncQueueUnavailableException(Exception innerException)
    : Exception("Synchronization queue is unavailable.", innerException);
