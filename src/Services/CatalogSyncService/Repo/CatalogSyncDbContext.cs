using Microsoft.EntityFrameworkCore;

namespace MsContractor.CatalogSyncService.Repo;

public sealed class CatalogSyncDbContext(DbContextOptions<CatalogSyncDbContext> options)
    : DbContext(options)
{
    private Guid? tenantAccountId;

    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<Counterparty> Counterparties => Set<Counterparty>();
    public DbSet<SyncOutboxMessage> OutboxMessages => Set<SyncOutboxMessage>();
    public DbSet<SyncWatermark> SyncWatermarks => Set<SyncWatermark>();
    public DbSet<MergeJob> MergeJobs => Set<MergeJob>();
    public DbSet<MergeOperation> MergeOperations => Set<MergeOperation>();
    public DbSet<CounterpartyDocument> CounterpartyDocuments => Set<CounterpartyDocument>();
    public DbSet<CounterpartyDocumentAdditionalData> CounterpartyDocumentAdditionalData => Set<CounterpartyDocumentAdditionalData>();
    internal Guid? TenantAccountId => tenantAccountId;

    public Task SetTenantAsync(Guid accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (accountId == Guid.Empty)
            throw new ArgumentException("Tenant account ID must be a non-empty UUID.", nameof(accountId));
        if (tenantAccountId is not null && tenantAccountId != accountId)
            throw new InvalidOperationException("A DbContext cannot be reused across tenant accounts.");
        if (tenantAccountId == accountId)
            return Task.CompletedTask;

        tenantAccountId = accountId;
        return Task.CompletedTask;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("catalog_sync");

        modelBuilder.Entity<SyncRun>(entity =>
        {
            entity.ToTable("sync_runs");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Status).HasMaxLength(32).IsRequired();
            entity.Property(item => item.RequestedMode).HasMaxLength(16).IsRequired();
            entity.Property(item => item.ExecutionMode).HasMaxLength(16).IsRequired();
            entity.Property(item => item.ErrorCode).HasMaxLength(64);
            entity.Property(item => item.ErrorMessage).HasMaxLength(512);
            entity.HasIndex(item => item.MessageId).IsUnique();
            entity.HasIndex(item => new { item.AccountId, item.Status });
            entity.HasIndex(item => new { item.AccountId, item.CreatedAt });
            entity.HasAlternateKey(item => new { item.Id, item.AccountId });
        });

        modelBuilder.Entity<SyncWatermark>(entity =>
        {
            entity.ToTable("sync_watermarks");
            entity.HasKey(item => item.AccountId);
        });

        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.ToTable("inbox_messages");
            entity.HasKey(item => new { item.MessageId, item.ConsumerName });
            entity.Property(item => item.ConsumerName).HasMaxLength(128);
        });

        modelBuilder.Entity<Counterparty>(entity =>
        {
            entity.ToTable("counterparties");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).HasMaxLength(1024);
            entity.Property(item => item.Phone).HasMaxLength(255);
            entity.Property(item => item.Email).HasMaxLength(320);
            entity.Property(item => item.Inn).HasMaxLength(32);
            entity.Property(item => item.Kpp).HasMaxLength(32);
            entity.Property(item => item.NormalizedName).HasMaxLength(1024);
            entity.Property(item => item.NormalizedPhone).HasMaxLength(64);
            entity.Property(item => item.NormalizedEmail).HasMaxLength(320);
            entity.Property(item => item.NormalizedInn).HasMaxLength(32);
            entity.Property(item => item.NormalizedKpp).HasMaxLength(32);
            entity.Property(item => item.RawJson).HasColumnType("jsonb");
            entity.HasIndex(item => new { item.AccountId, item.Id }).IsUnique();
            entity.HasIndex(item => new { item.AccountId, item.NormalizedName });
            entity.HasIndex(item => new { item.AccountId, item.NormalizedPhone });
            entity.HasIndex(item => new { item.AccountId, item.NormalizedEmail });
            entity.HasIndex(item => new { item.AccountId, item.NormalizedInn });
            entity.HasIndex(item => new { item.LastSyncRunId, item.AccountId });
            entity.HasOne(item => item.LastSyncRun)
                .WithMany(item => item.Counterparties)
                .HasForeignKey(item => new { item.LastSyncRunId, item.AccountId })
                .HasPrincipalKey(item => new { item.Id, item.AccountId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SyncOutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Topic).HasMaxLength(255);
            entity.Property(item => item.MessageKey).HasMaxLength(128);
            entity.Property(item => item.EventType).HasMaxLength(128);
            entity.Property(item => item.Payload).HasColumnType("jsonb");
            entity.Property(item => item.LastError).HasMaxLength(1024);
            entity.HasIndex(item => new { item.PublishedAt, item.CreatedAt });
        });

        modelBuilder.Entity<MergeJob>(entity =>
        {
            entity.ToTable("merge_jobs");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Status).HasMaxLength(32).IsRequired();
            entity.Property(item => item.Payload).HasColumnType("jsonb");
            entity.HasAlternateKey(item => new { item.Id, item.AccountId });
            entity.HasIndex(item => item.MessageId).IsUnique();
            entity.HasIndex(item => new { item.AccountId, item.Status });
            entity.HasIndex(item => new { item.AccountId, item.CreatedAt });
        });

        modelBuilder.Entity<MergeOperation>(entity =>
        {
            entity.ToTable("merge_operations");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.OperationType).HasMaxLength(64).IsRequired();
            entity.Property(item => item.Status).HasMaxLength(32).IsRequired();
            entity.Property(item => item.ErrorCode).HasMaxLength(64);
            entity.Property(item => item.ErrorMessage).HasMaxLength(512);
            entity.HasIndex(item => new { item.MergeJobId, item.Sequence }).IsUnique();
            entity.HasIndex(item => new { item.MergeJobId, item.OperationType, item.CounterpartyId }).IsUnique();
            entity.HasOne(item => item.MergeJob)
                .WithMany(item => item.Operations)
                .HasForeignKey(item => new { item.MergeJobId, item.AccountId })
                .HasPrincipalKey(item => new { item.Id, item.AccountId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CounterpartyDocument>(entity =>
        {
            entity.ToTable("counterparty_documents");
            entity.HasKey(item => new { item.AccountId, item.DocumentType, item.DocumentId });
            entity.HasAlternateKey(item => item.DocumentId);
            entity.Property(item => item.DocumentType).HasMaxLength(64).IsRequired();
            entity.HasIndex(item => new { item.AccountId, item.CounterpartyId });
            entity.HasOne(item => item.Counterparty)
                .WithMany(item => item.Documents)
                .HasForeignKey(item => new { item.CounterpartyId, item.AccountId })
                .HasPrincipalKey(item => new { item.Id, item.AccountId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CounterpartyDocumentAdditionalData>(entity =>
        {
            entity.ToTable("counterparty_document_additional_data");
            entity.HasKey(item => item.DocumentId);
            entity.Property(item => item.Contract).HasColumnType("uuid");
            entity.HasOne<CounterpartyDocument>()
                .WithOne()
                .HasForeignKey<CounterpartyDocumentAdditionalData>(item => item.DocumentId)
                .HasPrincipalKey<CounterpartyDocument>(item => item.DocumentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
