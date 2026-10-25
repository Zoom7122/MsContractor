namespace MsContractor.CatalogSyncService.Models;

public sealed record SyncCompletion(
    SyncRun Run,
    InboxMessage Inbox,
    SyncOutboxMessage Outbox,
    SyncWatermark Watermark,
    Func<int, SyncOutboxMessage>? CreateCompletedOutbox = null);
