using MsContractor.VendorService.Models;
using Microsoft.EntityFrameworkCore;

namespace MsContractor.VendorService.Persistence;

public sealed class VendorDbContext(DbContextOptions<VendorDbContext> options)
    : DbContext(options)
{
    private Guid? _tenantAccountId;

    public DbSet<Installation> Installations =>
        Set<Installation>();

    public DbSet<OutboxMessage> OutboxMessages =>
        Set<OutboxMessage>();
    internal Guid? TenantAccountId => _tenantAccountId;

    public Task SetTenantAsync(Guid accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (accountId == Guid.Empty)
            throw new ArgumentException("Tenant account ID must be a non-empty UUID.", nameof(accountId));
        if (_tenantAccountId is not null && _tenantAccountId != accountId)
            throw new InvalidOperationException("A DbContext cannot be reused across tenant accounts.");
        if (_tenantAccountId == accountId)
            return Task.CompletedTask;

        _tenantAccountId = accountId;
        return Task.CompletedTask;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("vendor");

        modelBuilder.Entity<Installation>(entity =>
        {
            entity.ToTable("installations");

            entity.HasKey(installation => installation.AccountId)
                .HasName("pk_installations");

            entity.Property(installation => installation.AccountId)
                .HasColumnName("account_id")
                .HasColumnType("uuid")
                .ValueGeneratedNever();

            entity.Property(installation => installation.AppId)
                .HasColumnName("app_id")
                .HasColumnType("uuid")
                .IsRequired();

            entity.Property(installation => installation.AppUid)
                .HasColumnName("app_uid")
                .HasColumnType("varchar(255)")
                .HasMaxLength(255)
                .IsRequired();

            entity.Property(installation => installation.AccountName)
                .HasColumnName("account_name")
                .HasColumnType("varchar(255)")
                .HasMaxLength(255);

            entity.Property(installation => installation.Status)
                .HasColumnName("status")
                .HasColumnType("varchar(32)")
                .HasMaxLength(32)
                .IsRequired();

            entity.Property(installation => installation.AccessTokenCiphertext)
                .HasColumnName("access_token_ciphertext")
                .HasColumnType("bytea");

            entity.Property(installation => installation.AccessTokenNonce)
                .HasColumnName("access_token_nonce")
                .HasColumnType("bytea");

            entity.Property(installation => installation.AccessTokenTag)
                .HasColumnName("access_token_tag")
                .HasColumnType("bytea");

            entity.Property(installation => installation.TokenKeyVersion)
                .HasColumnName("token_key_version")
                .HasColumnType("integer");

            entity.Property(installation => installation.AccessScope)
                .HasColumnName("access_scope")
                .HasColumnType("jsonb");

            entity.Property(installation => installation.Subscription)
                .HasColumnName("subscription")
                .HasColumnType("jsonb");

            entity.Property(installation => installation.InstalledAt)
                .HasColumnName("installed_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.Property(installation => installation.ActivatedAt)
                .HasColumnName("activated_at")
                .HasColumnType("timestamp with time zone");

            entity.Property(installation => installation.DeactivatedAt)
                .HasColumnName("deactivated_at")
                .HasColumnType("timestamp with time zone");

            entity.Property(installation => installation.LastContextAt)
                .HasColumnName("last_context_at")
                .HasColumnType("timestamp with time zone");

            entity.Property(installation => installation.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.Property(installation => installation.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.HasIndex(installation => installation.AppId)
                .HasDatabaseName("ix_installations_app_id");

            entity.HasIndex(installation => installation.Status)
                .HasDatabaseName("ix_installations_status");

            entity.HasIndex(installation => installation.UpdatedAt)
                .HasDatabaseName("ix_installations_updated_at");
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages");

            entity.HasKey(message => message.Id)
                .HasName("pk_outbox_messages");

            entity.Property(message => message.Id)
                .HasColumnName("id")
                .HasColumnType("uuid")
                .ValueGeneratedNever();

            entity.Property(message => message.RequestId)
                .HasColumnName("request_id")
                .HasColumnType("varchar(128)")
                .HasMaxLength(128)
                .IsRequired();

            entity.Property(message => message.AccountId)
                .HasColumnName("account_id")
                .HasColumnType("uuid")
                .IsRequired();

            entity.Property(message => message.EventType)
                .HasColumnName("event_type")
                .HasColumnType("varchar(128)")
                .HasMaxLength(128)
                .IsRequired();

            entity.Property(message => message.Payload)
                .HasColumnName("payload")
                .HasColumnType("jsonb")
                .IsRequired();

            entity.Property(message => message.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamp with time zone")
                .IsRequired();

            entity.Property(message => message.PublishedAt)
                .HasColumnName("published_at")
                .HasColumnType("timestamp with time zone");

            entity.Property(message => message.PublishAttempts)
                .HasColumnName("publish_attempts")
                .HasColumnType("integer")
                .HasDefaultValue(0)
                .IsRequired();

            entity.Property(message => message.LastError)
                .HasColumnName("last_error")
                .HasColumnType("text");

            entity.HasIndex(message => message.AccountId)
                .HasDatabaseName("ix_outbox_messages_account_id");

            entity.HasIndex(message => message.RequestId)
                .IsUnique()
                .HasDatabaseName("ux_outbox_messages_request_id");

            entity.HasIndex(message => message.CreatedAt)
                .HasDatabaseName("ix_outbox_messages_created_at");

            entity.HasIndex(message => new
            {
                message.PublishedAt,
                message.CreatedAt
            })
                .HasDatabaseName("ix_outbox_messages_unpublished");

            entity.HasOne<Installation>()
                .WithMany()
                .HasForeignKey(message => message.AccountId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_outbox_messages_installation");
        });

        base.OnModelCreating(modelBuilder);
    }
}
