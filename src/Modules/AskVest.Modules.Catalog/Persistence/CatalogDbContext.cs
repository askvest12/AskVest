using AskVest.Modules.Catalog.Domain;
using AskVest.Modules.Catalog.Domain.Enums;
using AskVest.Modules.Catalog.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Catalog.Persistence;

public class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public const string Schema = "catalog";
    public const string HistoryTable = "__ef_migrations_history";

    public DbSet<Chain> Chains => Set<Chain>();
    public DbSet<Issuer> Issuers => Set<Issuer>();
    public DbSet<Instrument> Instruments => Set<Instrument>();
    public DbSet<Token> Tokens => Set<Token>();
    public DbSet<TokenCountry> TokenCountries => Set<TokenCountry>();
    public DbSet<CatalogEvent> CatalogEvents => Set<CatalogEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Chain>(entity =>
        {
            entity.ToTable("chains");
            entity.HasKey(chain => chain.Id);
            entity.Property(chain => chain.Id).HasMaxLength(64).IsRequired();
            entity.Property(chain => chain.Family).HasMaxLength(32).IsRequired();
            entity.Property(chain => chain.Name).HasMaxLength(200).IsRequired();
            entity.Property(chain => chain.NativeAssetSymbol).HasMaxLength(16).IsRequired();
            entity.Property(chain => chain.ExplorerLinkTemplate).HasMaxLength(500).IsRequired();
            entity.Property(chain => chain.Status)
                .HasConversion(v => CatalogText.ToDb(v), v => CatalogText.ChainStatusFromDb(v))
                .HasMaxLength(16).IsRequired();
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_chains_status",
                "status IN ('enabled', 'disabled')"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_chains_native_decimals",
                "native_decimals BETWEEN 0 AND 36"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_chains_confirmations",
                "required_confirmations >= 0"));
        });

        modelBuilder.Entity<Issuer>(entity =>
        {
            entity.ToTable("issuers");
            entity.HasKey(issuer => issuer.Id);
            entity.Property(issuer => issuer.Id).HasMaxLength(64).IsRequired();
            entity.Property(issuer => issuer.LegalName).HasMaxLength(300).IsRequired();
            entity.Property(issuer => issuer.Jurisdiction).HasMaxLength(8).IsRequired();
            entity.Property(issuer => issuer.Website).HasMaxLength(500);
            entity.Property(issuer => issuer.RegulatoryStatus).HasMaxLength(1000);
            entity.Property(issuer => issuer.Status)
                .HasConversion(v => CatalogText.ToDb(v), v => CatalogText.IssuerStatusFromDb(v))
                .HasMaxLength(16).IsRequired();
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_issuers_status",
                "status IN ('approved', 'suspended')"));
        });

        modelBuilder.Entity<Instrument>(entity =>
        {
            entity.ToTable("instruments");
            entity.HasKey(instrument => instrument.Id);
            entity.Property(instrument => instrument.Id).HasMaxLength(64).IsRequired();
            entity.Property(instrument => instrument.Kind)
                .HasConversion(v => CatalogText.ToDb(v), v => CatalogText.InstrumentKindFromDb(v))
                .HasMaxLength(16).IsRequired();
            entity.Property(instrument => instrument.Name).HasMaxLength(300).IsRequired();
            entity.Property(instrument => instrument.Symbol).HasMaxLength(32).IsRequired();
            entity.Property(instrument => instrument.Isin).HasMaxLength(12);
            entity.Property(instrument => instrument.ListingExchange).HasMaxLength(64);
            entity.Property(instrument => instrument.CountryOfListing).HasMaxLength(2).IsRequired();
            entity.Property(instrument => instrument.QuoteCurrency).HasMaxLength(8).IsRequired();
            entity.Property(instrument => instrument.Sector).HasMaxLength(100);
            entity.Property(instrument => instrument.ShortDescription).HasMaxLength(2000);
            entity.Property(instrument => instrument.LogoLink).HasMaxLength(500);
            entity.Property(instrument => instrument.Status)
                .HasConversion(v => CatalogText.ToDb(v), v => CatalogText.InstrumentStatusFromDb(v))
                .HasMaxLength(16).IsRequired();
            entity.HasIndex(instrument => instrument.Status);
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_instruments_kind",
                "kind IN ('stock', 'etf')"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_instruments_status",
                "status IN ('active', 'delisted')"));
        });

        modelBuilder.Entity<Token>(entity =>
        {
            entity.ToTable("tokens");
            entity.HasKey(token => token.Id);
            entity.Property(token => token.InstrumentId).HasMaxLength(64).IsRequired();
            entity.Property(token => token.IssuerId).HasMaxLength(64).IsRequired();
            entity.Property(token => token.ChainId).HasMaxLength(64).IsRequired();
            entity.Property(token => token.Standard).HasMaxLength(32).IsRequired();
            entity.Property(token => token.CanonicalAddress).HasMaxLength(256).IsRequired();
            entity.Property(token => token.Caip19).HasMaxLength(300).IsRequired();
            entity.HasIndex(token => token.Caip19).IsUnique();
            entity.Property(token => token.Symbol).HasMaxLength(32).IsRequired();
            entity.Property(token => token.Decimals).IsRequired();
            entity.Property(token => token.LedgerAssetCode).HasMaxLength(16).IsRequired();
            entity.HasIndex(token => token.LedgerAssetCode).IsUnique();
            entity.Property(token => token.Status)
                .HasConversion(v => CatalogText.ToDb(v), v => CatalogText.TokenStatusFromDb(v))
                .HasMaxLength(16).IsRequired();
            entity.Property(token => token.GatewayKey).HasMaxLength(100).IsRequired();
            entity.Property(token => token.ScheduleKind)
                .HasConversion(v => CatalogText.ToDb(v), v => CatalogText.TradingScheduleKindFromDb(v))
                .HasMaxLength(32).IsRequired();
            entity.Property(token => token.ExternalTransfers)
                .HasConversion(v => CatalogText.ToDb(v), v => CatalogText.ExternalTransferPolicyFromDb(v))
                .HasMaxLength(32).IsRequired();
            entity.Property(token => token.ScheduleTimeZone).HasMaxLength(64);
            entity.Property(token => token.ScheduleNote).HasMaxLength(500);
            entity.Property(token => token.ClaimType)
                .HasConversion(v => CatalogText.ToDb(v), v => CatalogText.ClaimTypeFromDb(v))
                .HasMaxLength(32).IsRequired();
            entity.Property(token => token.ClaimDescription).HasMaxLength(2000).IsRequired();
            entity.Property(token => token.BackingDescription).HasMaxLength(2000).IsRequired();
            entity.Property(token => token.CustodianDescription).HasMaxLength(2000).IsRequired();
            entity.Property(token => token.RedemptionDescription).HasMaxLength(2000).IsRequired();
            entity.Property(token => token.DividendPolicy)
                .HasConversion(v => CatalogText.ToDb(v), v => CatalogText.DividendPolicyFromDb(v))
                .HasMaxLength(32).IsRequired();
            entity.Property(token => token.DividendNote).HasMaxLength(1000);
            entity.Property(token => token.VotingNote).HasMaxLength(1000);
            entity.Property(token => token.TransferRestrictions).HasMaxLength(2000).IsRequired();
            entity.Property(token => token.NoIssuerFees).IsRequired();
            entity.Property(token => token.RiskStatements).IsRequired();
            entity.Property(token => token.DisclosureDocumentLink).HasMaxLength(500).IsRequired();
            entity.Property(token => token.ReviewerLabel).HasMaxLength(100);
            entity.Property(token => token.EverListed).IsRequired();
            entity.HasIndex(token => token.Status);
            entity.HasIndex(token => new { token.InstrumentId, token.Status });
            entity.HasIndex(token => token.ChainId);
            entity.HasOne<Instrument>()
                .WithMany()
                .HasForeignKey(token => token.InstrumentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Issuer>()
                .WithMany()
                .HasForeignKey(token => token.IssuerId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Chain>()
                .WithMany()
                .HasForeignKey(token => token.ChainId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_tokens_status",
                "status IN ('draft', 'listed', 'suspended', 'delisted')"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_tokens_decimals",
                "decimals BETWEEN 0 AND 18"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_tokens_issuer_fees",
                "no_issuer_fees OR cardinality(issuer_fees) > 0"));
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_tokens_risk_statements",
                "cardinality(risk_statements) >= 3"));
        });

        modelBuilder.Entity<TokenCountry>(entity =>
        {
            entity.ToTable("token_countries");
            entity.HasKey(country => new { country.TokenId, country.CountryCode });
            entity.Property(country => country.CountryCode).HasMaxLength(2).IsRequired();
            entity.HasOne<Token>()
                .WithMany()
                .HasForeignKey(country => country.TokenId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_token_countries_code",
                "country_code ~ '^[A-Z]{2}$'"));
        });

        modelBuilder.Entity<CatalogEvent>(entity =>
        {
            entity.ToTable("catalog_events");
            entity.HasKey(catalogEvent => catalogEvent.Id);
            entity.Property(catalogEvent => catalogEvent.EventType)
                .HasConversion(v => CatalogText.ToDb(v), v => CatalogText.CatalogEventTypeFromDb(v))
                .HasMaxLength(16).IsRequired();
            entity.Property(catalogEvent => catalogEvent.SubjectType).HasMaxLength(16).IsRequired();
            entity.Property(catalogEvent => catalogEvent.SubjectId).HasMaxLength(64).IsRequired();
            entity.Property(catalogEvent => catalogEvent.DataVersion).HasMaxLength(64);
            entity.Property(catalogEvent => catalogEvent.CorrelationId).HasMaxLength(64);
            entity.HasIndex(catalogEvent => catalogEvent.OccurredAt);
            entity.ToTable(table => table.HasCheckConstraint(
                "ck_catalog_events_type",
                "event_type IN ('imported', 'listed', 'suspended', 'relisted', 'delisted', 'updated')"));
        });
    }
}
