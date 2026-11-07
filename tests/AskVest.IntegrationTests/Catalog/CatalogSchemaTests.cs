using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Catalog.Persistence;
using AskVest.Platform;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Catalog;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class CatalogSchemaTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Migrations_Reapply_As_NoOp()
    {
        var applied = await MigrationRunner.RunAsync(
            fixture.OwnerConnectionString,
            [connectionString => new CatalogDbContext(CatalogDbContextOptions.Create(connectionString))],
            TestContext.Current.CancellationToken);
        applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task Database_Rejects_Duplicate_Caip19()
    {
        await InsertChainAsync("eip155:1");
        await InsertIssuerAsync("issuer-a");
        await InsertInstrumentAsync("instr-a");
        await InsertTokenAsync("11111111-1111-7111-8111-111111111111", caip19: "eip155:1/erc20:0xAAA");

        var exception = await Should.ThrowAsync<PostgresException>(() =>
            Database.ExecuteAsync(
                fixture.OwnerConnectionString,
                TokenInsertSql(
                    "aaaaaaaa-1111-7111-8111-111111111111",
                    caip19: "eip155:1/erc20:0xAAA",
                    assetCode: "ASTB"),
                TestContext.Current.CancellationToken));
        exception.Message.ShouldContain("ix_tokens_caip19");
    }

    [Fact]
    public async Task Database_Rejects_Duplicate_Ledger_Asset_Code()
    {
        await InsertChainAsync("eip155:1");
        await InsertIssuerAsync("issuer-a");
        await InsertInstrumentAsync("instr-a");
        await InsertTokenAsync("bbbbbbbb-1111-7111-8111-111111111111", assetCode: "SAME");

        var exception = await Should.ThrowAsync<PostgresException>(() =>
            Database.ExecuteAsync(
                fixture.OwnerConnectionString,
                TokenInsertSql(
                    "cccccccc-1111-7111-8111-111111111111",
                    caip19: "eip155:1/erc20:0xBBB",
                    assetCode: "SAME"),
                TestContext.Current.CancellationToken));
        exception.Message.ShouldContain("ix_tokens_ledger_asset_code");
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("LISTED")]
    public async Task Database_Rejects_Invalid_Token_Status(string status)
    {
        await InsertChainAsync("eip155:1");
        await InsertIssuerAsync("issuer-a");
        await InsertInstrumentAsync("instr-a");

        var exception = await Should.ThrowAsync<PostgresException>(() =>
            Database.ExecuteAsync(
                fixture.OwnerConnectionString,
                TokenInsertSql(
                    "33333333-3333-7333-8333-333333333333",
                    status: status),
                TestContext.Current.CancellationToken));
        exception.Message.ShouldContain("ck_tokens_status");
    }

    [Theory]
    [InlineData(19)]
    [InlineData(-1)]
    public async Task Database_Rejects_Decimals_Above_Eighteen(int decimals)
    {
        await InsertChainAsync("eip155:1");
        await InsertIssuerAsync("issuer-a");
        await InsertInstrumentAsync("instr-a");

        var exception = await Should.ThrowAsync<PostgresException>(() =>
            Database.ExecuteAsync(
                fixture.OwnerConnectionString,
                TokenInsertSql(
                    "44444444-4444-7444-8444-444444444444",
                    decimals: decimals),
                TestContext.Current.CancellationToken));
        exception.Message.ShouldContain("ck_tokens_decimals");
    }

    [Fact]
    public async Task Database_Rejects_Orphan_Token_References()
    {
        var exception = await Should.ThrowAsync<PostgresException>(() =>
            Database.ExecuteAsync(
                fixture.OwnerConnectionString,
                TokenInsertSql(
                    "55555555-5555-7555-8555-555555555555",
                    caip19: "eip155:1/erc20:0xDDD",
                    assetCode: "ORPH",
                    instrumentId: "missing-instr",
                    issuerId: "missing-issuer",
                    chainId: "eip155:999"),
                TestContext.Current.CancellationToken));
        exception.Message.ShouldContain("foreign key constraint");
    }

    [Fact]
    public async Task Application_Role_Cannot_Write_Any_Catalog_Table()
    {
        var rows = await Database.QueryAsync(
            fixture.AppConnectionString,
            "SELECT count(*) AS n FROM catalog.chains",
            TestContext.Current.CancellationToken);
        Convert.ToInt64(rows[0]["n"]).ShouldBeGreaterThanOrEqualTo(0L);

        await ExpectDeniedAsync("INSERT INTO catalog.chains (id, family, name, native_asset_symbol, native_decimals, explorer_link_template, required_confirmations, status, created_at, updated_at) VALUES ('eip155:999', 'evm', 'x', 'X', 18, 'https://x/{address}', 1, 'enabled', now(), now())");
        await ExpectDeniedAsync("UPDATE catalog.chains SET name = 'x' WHERE id = 'eip155:1'");
        await ExpectDeniedAsync("DELETE FROM catalog.chains");
        await ExpectDeniedAsync("INSERT INTO catalog.issuers (id, legal_name, jurisdiction, status, created_at, updated_at) VALUES ('x', 'x', 'US', 'approved', now(), now())");
        await ExpectDeniedAsync("INSERT INTO catalog.instruments (id, kind, name, symbol, country_of_listing, quote_currency, status, created_at, updated_at) VALUES ('x', 'stock', 'x', 'X', 'US', 'USD', 'active', now(), now())");
        await ExpectDeniedAsync("INSERT INTO catalog.tokens (id) VALUES ('00000000-0000-0000-0000-000000000000')");
        await ExpectDeniedAsync("INSERT INTO catalog.token_countries (token_id, country_code) VALUES ('00000000-0000-0000-0000-000000000000', 'NG')");
        await ExpectDeniedAsync("INSERT INTO catalog.catalog_events (id, event_type, subject_type, subject_id, occurred_at) VALUES ('00000000-0000-0000-0000-000000000000', 'imported', 'token', 'x', now())");
        await ExpectDeniedAsync("DELETE FROM catalog.catalog_events");
    }

    private async Task ExpectDeniedAsync(string sql)
    {
        var exception = await Should.ThrowAsync<PostgresException>(() =>
            Database.ExecuteAsync(fixture.AppConnectionString, sql, TestContext.Current.CancellationToken));
        exception.Message.ShouldContain("permission denied");
    }

    private async Task InsertChainAsync(string chainId)
    {
        var existing = await Database.QueryAsync(
            fixture.OwnerConnectionString,
            $"SELECT id FROM catalog.chains WHERE id = '{chainId}'",
            TestContext.Current.CancellationToken);
        if (existing.Count > 0)
        {
            return;
        }

        await Database.ExecuteAsync(
            fixture.OwnerConnectionString,
            $$"""
            INSERT INTO catalog.chains (id, family, name, native_asset_symbol, native_decimals, explorer_link_template, required_confirmations, status, created_at, updated_at)
            VALUES ('{{chainId}}', 'evm', 'Test chain', 'ETH', 18, 'https://explorer.example/{address}', 12, 'enabled', now(), now());
            """,
            TestContext.Current.CancellationToken);
    }

    private async Task InsertIssuerAsync(string issuerId)
    {
        var existing = await Database.QueryAsync(
            fixture.OwnerConnectionString,
            $"SELECT id FROM catalog.issuers WHERE id = '{issuerId}'",
            TestContext.Current.CancellationToken);
        if (existing.Count > 0)
        {
            return;
        }

        await Database.ExecuteAsync(
            fixture.OwnerConnectionString,
            $"""
            INSERT INTO catalog.issuers (id, legal_name, jurisdiction, legal_document_links, status, created_at, updated_at)
            VALUES ('{issuerId}', 'Test Issuer', 'US', ARRAY[]::text[], 'approved', now(), now());
            """,
            TestContext.Current.CancellationToken);
    }

    private async Task InsertInstrumentAsync(string instrumentId)
    {
        var existing = await Database.QueryAsync(
            fixture.OwnerConnectionString,
            $"SELECT id FROM catalog.instruments WHERE id = '{instrumentId}'",
            TestContext.Current.CancellationToken);
        if (existing.Count > 0)
        {
            return;
        }

        await Database.ExecuteAsync(
            fixture.OwnerConnectionString,
            $"""
            INSERT INTO catalog.instruments (id, kind, name, symbol, country_of_listing, quote_currency, status, created_at, updated_at)
            VALUES ('{instrumentId}', 'stock', 'Test Corp', 'TST', 'US', 'USD', 'active', now(), now());
            """,
            TestContext.Current.CancellationToken);
    }

    private Task InsertTokenAsync(
        string id,
        string caip19 = "eip155:1/erc20:0xCCC",
        string assetCode = "TSTX",
        string status = "listed",
        int decimals = 6)
        => Database.ExecuteAsync(
            fixture.OwnerConnectionString,
            TokenInsertSql(id, caip19, assetCode, status, decimals),
            TestContext.Current.CancellationToken);

    private static string TokenInsertSql(
        string id,
        string caip19 = "eip155:1/erc20:0xCCC",
        string assetCode = "TSTX",
        string status = "listed",
        int decimals = 6,
        string instrumentId = "instr-a",
        string issuerId = "issuer-a",
        string chainId = "eip155:1")
        => $"""
            INSERT INTO catalog.tokens (
                id, instrument_id, issuer_id, chain_id, standard, canonical_address, caip19,
                symbol, decimals, ledger_asset_code, status, gateway_key, schedule_kind,
                claim_type, claim_description, backing_description, custodian_description,
                redemption_description, dividend_policy, voting_rights, transfer_restrictions,
                issuer_fees, no_issuer_fees, risk_statements, disclosure_document_link,
                ever_listed, created_at, updated_at)
            VALUES (
                '{id}', '{instrumentId}', '{issuerId}', '{chainId}', 'erc20', '0xCCC', '{caip19}',
                'TST', {decimals}, '{assetCode}', '{status}', 'gateway-test', 'always',
                'direct_share_claim', 'claim', 'backing', 'custodian',
                'redemption', 'distributed', true, 'none',
                ARRAY['fee']::text[], false, ARRAY['r1','r2','r3']::text[], 'https://docs.example/token',
                true, now(), now());
            """;
}
