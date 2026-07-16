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
            entity.ToTable("events", t => t.HasCheckConstraint("CK_Amount_Positive", "CAST(\"Amount\" AS REAL) > 0"));

            entity.HasIndex(e => e.EventId).IsUnique();
            entity.HasIndex(e => new { e.AccountId, e.EventTimestamp });

            entity.Property(e => e.Type)
                .HasConversion(
                    type => type == TransactionType.Credit ? "CREDIT" : "DEBIT",
                    value => value == "CREDIT" ? TransactionType.Credit : TransactionType.Debit);
        });
    }
}
