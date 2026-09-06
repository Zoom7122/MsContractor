namespace MsContractor.CatalogSyncService.Models.Exceptions;

public sealed class CounterpartySnapshotChangedException(string message) : Exception(message);
