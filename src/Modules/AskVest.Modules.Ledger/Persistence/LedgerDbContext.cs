using AskVest.Modules.Ledger.Contracts;
using AskVest.Modules.Ledger.Persistence.Entities;
using AskVest.Modules.Ledger.Services;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Ledger.Persistence;

public class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public const string Schema = "ledger";
    public const string HistoryTable = "__ef_migrations_history";

    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Journal> Journals => Set<Journal>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<AccountBalance> AccountBalances => Set<AccountBalance>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Asset>(entity =>
        {
            entity.ToTable("assets");
            entity.HasKey(asset => asset.Code);
            entity.Property(asset => asset.Code).HasMaxLength(16).IsRequired();
            entity.Property(asset => asset.Kind).HasConversion(v => LedgerText.ToDb(v), v => LedgerText.AssetKindFromDb(v)).HasMaxLength(16).IsRequired();
            entity.Property(asset => asset.Scale).IsRequired();
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_assets_kind",
                "kind IN ('fiat', 'stablecoin', 'token')"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_assets_scale",
                "scale BETWEEN 0 AND 18"));
        });

        modelBuilder.Entity<Account>(entity =>
        {
            entity.ToTable("accounts");
            entity.HasKey(account => account.Id);
            entity.Property(account => account.Book).HasConversion(v => LedgerText.ToDb(v), v => LedgerText.BookFromDb(v)).HasMaxLength(16).IsRequired();
            entity.Property(account => account.OwnerType).HasConversion(v => LedgerText.ToDb(v), v => LedgerText.OwnerTypeFromDb(v)).HasMaxLength(16).IsRequired();
            entity.Property(account => account.Kind).HasConversion(v => LedgerText.ToDb(v), v => LedgerText.AccountKindFromDb(v)).HasMaxLength(32).IsRequired();
            entity.Property(account => account.AssetCode).HasMaxLength(16).IsRequired();
            entity.HasIndex(account => new { account.Book, account.OwnerType, account.OwnerId, account.Kind, account.AssetCode }).IsUnique();
            entity.HasIndex(account => new { account.Id, account.Book }).IsUnique();
            entity.HasIndex(account => new { account.Id, account.AssetCode }).IsUnique();
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_accounts_book",
                "book IN ('practice', 'real')"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_accounts_kind",
                "kind IN ('user_available', 'user_held', 'system_clearing', 'system_fees', 'system_practice_issuance')"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_accounts_owner",
                "(owner_type = 'user' AND kind IN ('user_available', 'user_held') " +
                "AND owner_id <> '00000000-0000-0000-0000-000000000000') " +
                "OR (owner_type = 'system' AND kind IN ('system_clearing', 'system_fees', 'system_practice_issuance') " +
                "AND owner_id = '00000000-0000-0000-0000-000000000000')"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_accounts_allow_negative",
                "allow_negative = (owner_type = 'system')"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_accounts_issuance_book",
                "kind <> 'system_practice_issuance' OR book = 'practice'"));
        });

        modelBuilder.Entity<Journal>(entity =>
        {
            entity.ToTable("journals");
            entity.HasKey(journal => journal.Id);
            entity.Property(journal => journal.Book).HasConversion(v => LedgerText.ToDb(v), v => LedgerText.BookFromDb(v)).HasMaxLength(16).IsRequired();
            entity.Property(journal => journal.ReasonCode).HasMaxLength(64).IsRequired();
            entity.Property(journal => journal.ReferenceType).HasMaxLength(64).IsRequired();
            entity.Property(journal => journal.IdempotencyKey).HasMaxLength(200).IsRequired();
            entity.HasIndex(journal => journal.IdempotencyKey).IsUnique();
            entity.Property(journal => journal.ContentHash).HasColumnType("char(64)").IsRequired();
            entity.HasIndex(journal => journal.ReversesJournalId).IsUnique();
            entity.HasIndex(journal => new { journal.Id, journal.Book }).IsUnique();
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_journals_book",
                "book IN ('practice', 'real')"));
        });

        modelBuilder.Entity<JournalEntry>(entity =>
        {
            entity.ToTable("journal_entries");
            entity.HasKey(entry => entry.Id);
            entity.Property(entry => entry.Book).HasConversion(v => LedgerText.ToDb(v), v => LedgerText.BookFromDb(v)).HasMaxLength(16).IsRequired();
            entity.Property(entry => entry.AssetCode).HasMaxLength(16).IsRequired();
            entity.Property(entry => entry.Amount).HasColumnType("numeric(28,18)").IsRequired();
            entity.HasIndex(entry => new { entry.JournalId, entry.LineNumber }).IsUnique();
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_journal_entries_amount",
                "amount <> 0"));
        });

        modelBuilder.Entity<AccountBalance>(entity =>
        {
            entity.ToTable("account_balances");
            entity.HasKey(balance => balance.AccountId);
            entity.Property(balance => balance.Balance).HasColumnType("numeric(28,18)").IsRequired();
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_account_balances_negative",
                "allow_negative OR balance >= 0"));
        });
    }
}
