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
            entity.ToTable("transactions", t => t.HasCheckConstraint("CK_Amount_Positive", "CAST(\"Amount\" AS REAL) > 0"));

            entity.HasIndex(e => e.EventId).IsUnique();
            entity.HasIndex(e => e.AccountId);

            entity.Property(e => e.Type)
                .HasConversion(
                    type => type == TransactionType.Credit ? "CREDIT" : "DEBIT",
                    value => value == "CREDIT" ? TransactionType.Credit : TransactionType.Debit);
        });
    }
}
