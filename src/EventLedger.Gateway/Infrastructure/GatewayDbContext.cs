using EventLedger.Gateway.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventLedger.Gateway.Infrastructure;

public class GatewayDbContext(DbContextOptions<GatewayDbContext> options) : DbContext(options)
{
    public DbSet<EventRecord> Events => Set<EventRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EventRecord>(entity =>
        {
            entity.ToTable("events", t =>
            {
                t.HasCheckConstraint("CK_events_amount_positive", "CAST(\"amount\" AS REAL) > 0");
                t.HasCheckConstraint("CK_events_type_valid", "\"type\" IN ('CREDIT', 'DEBIT')");
            });

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.EventId).HasColumnName("event_id");
            entity.Property(e => e.AccountId).HasColumnName("account_id");
            entity.Property(e => e.Amount).HasColumnName("amount");
            entity.Property(e => e.Currency).HasColumnName("currency");
            entity.Property(e => e.EventTimestamp).HasColumnName("event_timestamp");
            entity.Property(e => e.MetadataJson).HasColumnName("metadata_json");
            entity.Property(e => e.ReceivedAt).HasColumnName("received_at");

            entity.HasIndex(e => e.EventId).IsUnique();
            entity.HasIndex(e => new { e.AccountId, e.EventTimestamp });

            entity.Property(e => e.Type)
                .HasColumnName("type")
                .HasConversion(
                    type => type.ToWireString(),
                    value => TransactionTypeExtensions.ParseWireString(value));
        });
    }
}
