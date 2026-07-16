using EventLedger.AccountService.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventLedger.AccountService.Infrastructure;

public class AccountDbContext(DbContextOptions<AccountDbContext> options) : DbContext(options)
{
    public DbSet<TransactionRecord> Transactions => Set<TransactionRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TransactionRecord>(entity =>
        {
            entity.ToTable("transactions", t =>
            {
                t.HasCheckConstraint("CK_transactions_amount_positive", "CAST(\"amount\" AS REAL) > 0");
                t.HasCheckConstraint("CK_transactions_type_valid", "\"type\" IN ('CREDIT', 'DEBIT')");
            });

            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.EventId).HasColumnName("event_id");
            entity.Property(e => e.AccountId).HasColumnName("account_id");
            entity.Property(e => e.Amount).HasColumnName("amount");
            entity.Property(e => e.AppliedAt).HasColumnName("applied_at");

            entity.HasIndex(e => e.EventId).IsUnique();
            entity.HasIndex(e => e.AccountId);

            entity.Property(e => e.Type)
                .HasColumnName("type")
                .HasConversion(
                    type => type.ToWireString(),
                    value => TransactionTypeExtensions.ParseWireString(value));
        });
    }
}
