namespace MsContractor.CatalogSyncService.Models.Exceptions;

public sealed class CatalogPersistenceException(Exception innerException)
    : Exception("Could not save catalog changes.", innerException);
