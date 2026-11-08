using AskVest.IntegrationTests.Infrastructure;
using AskVest.Modules.Ledger.Persistence;
using AskVest.Platform;
using Npgsql;
using Shouldly;
using Xunit;

namespace AskVest.IntegrationTests.Ledger;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class LedgerSchemaTests
{
    private readonly PostgresFixture _fixture;

    public LedgerSchemaTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Migrations_Reapply_As_NoOp_And_Seeded_Assets_Exist()
    {
        var applied = await MigrationRunner.RunAsync(
            _fixture.OwnerConnectionString,
            [connectionString => new LedgerDbContext(LedgerDbContextOptions.Create(connectionString))],
            TestContext.Current.CancellationToken);
        applied.ShouldBeEmpty();

        var rows = await Database.QueryAsync(
            _fixture.OwnerConnectionString,
            "SELECT code, kind, scale FROM ledger.assets WHERE code IN ('USD', 'USDC') ORDER BY code",
            TestContext.Current.CancellationToken);
        rows.Count.ShouldBe(2);
        rows[0]["code"].ShouldBe("USD");
        rows[0]["kind"].ShouldBe("fiat");
        rows[0]["scale"].ShouldBe(6);
        rows[1]["code"].ShouldBe("USDC");
        rows[1]["kind"].ShouldBe("stablecoin");
        rows[1]["scale"].ShouldBe(6);
    }

    [Fact]
    public async Task Database_Rejects_Unbalanced_Journal_At_Commit()
    {
        var (journal, debit, credit) = await NewJournalWithAccountsAsync();
        await ExpectSqlErrorAsync(
            $"""
            BEGIN;
            INSERT INTO ledger.journals (id, book, reason_code, reference_type, idempotency_key, content_hash, posted_at)
            VALUES ('{journal}', 'real', 'test', 'test', 'unbalanced-1', '{Hash()}', now());
            INSERT INTO ledger.journal_entries (id, journal_id, book, account_id, asset_code, amount, line_number)
            VALUES ('{Guid.CreateVersion7()}', '{journal}', 'real', '{debit}', 'USD', 100, 1),
                   ('{Guid.CreateVersion7()}', '{journal}', 'real', '{credit}', 'USD', -40, 2);
            COMMIT;
            """,
            "does not sum to zero");
    }

    [Fact]
    public async Task Database_Rejects_Journal_With_Single_Entry()
    {
        var (journal, debit, _) = await NewJournalWithAccountsAsync();
        await ExpectSqlErrorAsync(
            $"""
            BEGIN;
            INSERT INTO ledger.journals (id, book, reason_code, reference_type, idempotency_key, content_hash, posted_at)
            VALUES ('{journal}', 'real', 'test', 'test', 'single-1', '{Hash()}', now());
            INSERT INTO ledger.journal_entries (id, journal_id, book, account_id, asset_code, amount, line_number)
            VALUES ('{Guid.CreateVersion7()}', '{journal}', 'real', '{debit}', 'USD', 100, 1);
            COMMIT;
            """,
            "at least two entries");
    }

    [Fact]
    public async Task Database_Rejects_Zero_Amount_Entry()
    {
        var (journal, debit, credit) = await NewJournalWithAccountsAsync();
        await ExpectSqlErrorAsync(
            $"""
            INSERT INTO ledger.journals (id, book, reason_code, reference_type, idempotency_key, content_hash, posted_at)
            VALUES ('{journal}', 'real', 'test', 'test', 'zero-1', '{Hash()}', now());
            INSERT INTO ledger.journal_entries (id, journal_id, book, account_id, asset_code, amount, line_number)
            VALUES ('{Guid.CreateVersion7()}', '{journal}', 'real', '{debit}', 'USD', 0, 1);
            """,
            "ck_journal_entries_amount");
    }

    [Fact]
    public async Task Database_Rejects_Entry_Account_From_Another_Book()
    {
        var journal = Guid.CreateVersion7();
        var practiceAccount = await NewAccountAsync(practiceBook: true);
        var realAccount = await NewAccountAsync(practiceBook: false);
        await ExpectSqlErrorAsync(
            $"""
            INSERT INTO ledger.journals (id, book, reason_code, reference_type, idempotency_key, content_hash, posted_at)
            VALUES ('{journal}', 'real', 'test', 'test', 'bookmix-1', '{Hash()}', now());
            INSERT INTO ledger.journal_entries (id, journal_id, book, account_id, asset_code, amount, line_number)
            VALUES ('{Guid.CreateVersion7()}', '{journal}', 'real', '{practiceAccount}', 'USD', 100, 1),
                   ('{Guid.CreateVersion7()}', '{journal}', 'real', '{realAccount}', 'USD', -100, 2);
            """,
            "fk_journal_entries_account_book");
    }

    [Fact]
    public async Task Database_Rejects_Entry_Asset_Different_From_Account_Asset()
    {
        var journal = Guid.CreateVersion7();
        var usdAccount = await NewAccountAsync(assetCode: "USD");
        var usdcAccount = await NewAccountAsync(assetCode: "USDC");
        await ExpectSqlErrorAsync(
            $"""
            INSERT INTO ledger.journals (id, book, reason_code, reference_type, idempotency_key, content_hash, posted_at)
            VALUES ('{journal}', 'real', 'test', 'test', 'assetmix-1', '{Hash()}', now());
            INSERT INTO ledger.journal_entries (id, journal_id, book, account_id, asset_code, amount, line_number)
            VALUES ('{Guid.CreateVersion7()}', '{journal}', 'real', '{usdAccount}', 'USDC', 100, 1),
                   ('{Guid.CreateVersion7()}', '{journal}', 'real', '{usdcAccount}', 'USDC', -100, 2);
            """,
            "fk_journal_entries_account_asset");
    }

    [Fact]
    public async Task Database_Rejects_Negative_User_Balance()
    {
        var account = await NewAccountAsync();
        await ExpectSqlErrorAsync(
            $"""
            INSERT INTO ledger.account_balances (account_id, balance, allow_negative, version, updated_at)
            VALUES ('{account}', -5, false, 1, now());
            """,
            "ck_account_balances_negative");
    }

    [Fact]
    public async Task Database_Rejects_Duplicate_Idempotency_Key()
    {
        var (journal1, debit, credit) = await NewJournalWithAccountsAsync();
        await ExpectSqlErrorAsync(
            $"""
            INSERT INTO ledger.journals (id, book, reason_code, reference_type, idempotency_key, content_hash, posted_at)
            VALUES ('{journal1}', 'real', 'test', 'test', 'dupkey-1', '{Hash()}', now());
            INSERT INTO ledger.journal_entries (id, journal_id, book, account_id, asset_code, amount, line_number)
            VALUES ('{Guid.CreateVersion7()}', '{journal1}', 'real', '{debit}', 'USD', 10, 1),
                   ('{Guid.CreateVersion7()}', '{journal1}', 'real', '{credit}', 'USD', -10, 2);
            INSERT INTO ledger.journals (id, book, reason_code, reference_type, idempotency_key, content_hash, posted_at)
            VALUES ('{Guid.CreateVersion7()}', 'real', 'test', 'test', 'dupkey-1', '{Hash()}', now());
            """,
            "ix_journals_idempotency_key");

        await DeleteJournalAsync(journal1);
    }

    [Fact]
    public async Task Database_Rejects_Second_Reversal_Of_One_Journal()
    {
        var (journal, debit, credit) = await NewJournalWithAccountsAsync();
        await Database.ExecuteAsync(
            _fixture.OwnerConnectionString,
            $"""
            INSERT INTO ledger.journals (id, book, reason_code, reference_type, idempotency_key, content_hash, posted_at)
            VALUES ('{journal}', 'real', 'test', 'test', 'orig-1', '{Hash()}', now());
            INSERT INTO ledger.journal_entries (id, journal_id, book, account_id, asset_code, amount, line_number)
            VALUES ('{Guid.CreateVersion7()}', '{journal}', 'real', '{debit}', 'USD', 10, 1),
                   ('{Guid.CreateVersion7()}', '{journal}', 'real', '{credit}', 'USD', -10, 2);
            """,
            TestContext.Current.CancellationToken);
        await ExpectSqlErrorAsync(
            $"""
            INSERT INTO ledger.journals (id, book, reason_code, reference_type, idempotency_key, content_hash, reverses_journal_id, posted_at)
            VALUES ('{Guid.CreateVersion7()}', 'real', 'reversal', 'test', 'rev-1', '{Hash()}', '{journal}', now()),
                   ('{Guid.CreateVersion7()}', 'real', 'reversal', 'test', 'rev-2', '{Hash()}', '{journal}', now());
            """,
            "ix_journals_reverses_journal_id");

        await DeleteJournalAsync(journal);
    }

    [Fact]
    public async Task Database_Rejects_Practice_Issuance_Account_In_Real_Book()
    {
        await ExpectSqlErrorAsync(
            $"""
            INSERT INTO ledger.accounts (id, book, owner_type, owner_id, kind, asset_code, allow_negative, created_at)
            VALUES ('{Guid.CreateVersion7()}', 'real', 'system', '00000000-0000-0000-0000-000000000000',
                    'system_practice_issuance', 'USD', true, now());
            """,
            "ck_accounts_issuance_book");
    }

    [Fact]
    public async Task Database_Rejects_Duplicate_System_Account()
    {
        var kind = "system_clearing";
        var sql = $$"""
            INSERT INTO ledger.accounts (id, book, owner_type, owner_id, kind, asset_code, allow_negative, created_at)
            VALUES ('{{Guid.CreateVersion7()}}', 'real', 'system', '00000000-0000-0000-0000-000000000000',
                    '{{kind}}', 'USD', true, now());
            INSERT INTO ledger.accounts (id, book, owner_type, owner_id, kind, asset_code, allow_negative, created_at)
            VALUES ('{{Guid.CreateVersion7()}}', 'real', 'system', '00000000-0000-0000-0000-000000000000',
                    '{{kind}}', 'USD', true, now());
            """;
        await ExpectSqlErrorAsync(sql, "ix_accounts_book_owner_type_owner_id_kind_asset_code");
    }

    [Fact]
    public async Task Application_Role_Cannot_Mutate_Or_Alter_Ledger_Journals()
    {
        var journal = Guid.CreateVersion7();
        var (debit, credit) = await NewAccountPairAsync();
        await Database.ExecuteAsync(
            _fixture.OwnerConnectionString,
            $"""
            INSERT INTO ledger.journals (id, book, reason_code, reference_type, idempotency_key, content_hash, posted_at)
            VALUES ('{journal}', 'real', 'test', 'test', 'priv-1', '{Hash()}', now());
            INSERT INTO ledger.journal_entries (id, journal_id, book, account_id, asset_code, amount, line_number)
            VALUES ('{Guid.CreateVersion7()}', '{journal}', 'real', '{debit}', 'USD', 10, 1),
                   ('{Guid.CreateVersion7()}', '{journal}', 'real', '{credit}', 'USD', -10, 2);
            """,
            TestContext.Current.CancellationToken);

        await ExpectSqlErrorAsync(
            _fixture.AppConnectionString,
            $"UPDATE ledger.journals SET reason_code = 'tampered' WHERE id = '{journal}'",
            "permission denied");
        await ExpectSqlErrorAsync(
            _fixture.AppConnectionString,
            $"DELETE FROM ledger.journal_entries WHERE journal_id = '{journal}'",
            "permission denied");
        await ExpectSqlErrorAsync(
            _fixture.AppConnectionString,
            "ALTER TABLE ledger.journals ADD COLUMN sneaky text",
            "must be owner");

        var rows = await Database.QueryAsync(
            _fixture.AppConnectionString,
            $"SELECT count(*) AS n FROM ledger.journals WHERE id = '{journal}'",
            TestContext.Current.CancellationToken);
        Convert.ToInt64(rows[0]["n"]).ShouldBe(1L);

        await DeleteJournalAsync(journal);
    }

    [Fact]
    public async Task Application_Role_Can_Insert_And_Select_Journals()
    {
        var (journal, debit, credit) = await NewJournalWithAccountsAsync();
        await Database.ExecuteAsync(
            _fixture.AppConnectionString,
            $"""
            INSERT INTO ledger.journals (id, book, reason_code, reference_type, idempotency_key, content_hash, posted_at)
            VALUES ('{journal}', 'real', 'test', 'test', 'appinsert-1', '{Hash()}', now());
            INSERT INTO ledger.journal_entries (id, journal_id, book, account_id, asset_code, amount, line_number)
            VALUES ('{Guid.CreateVersion7()}', '{journal}', 'real', '{debit}', 'USD', 10, 1),
                   ('{Guid.CreateVersion7()}', '{journal}', 'real', '{credit}', 'USD', -10, 2);
            """,
            TestContext.Current.CancellationToken);
        var rows = await Database.QueryAsync(
            _fixture.AppConnectionString,
            $"SELECT count(*) AS n FROM ledger.journal_entries WHERE journal_id = '{journal}'",
            TestContext.Current.CancellationToken);
        Convert.ToInt64(rows[0]["n"]).ShouldBe(2L);

        // The raw inserts bypass balance maintenance, so the rows must go or
        // every later reconciliation cycle reports a phantom mismatch.
        await DeleteJournalAsync(journal);
    }

    private async Task DeleteJournalAsync(Guid journal)
    {
        await Database.ExecuteAsync(
            _fixture.OwnerConnectionString,
            $"DELETE FROM ledger.journal_entries WHERE journal_id = '{journal}'; " +
            $"DELETE FROM ledger.journals WHERE id = '{journal}';",
            TestContext.Current.CancellationToken);
    }

    private async Task<(Guid Journal, Guid Debit, Guid Credit)> NewJournalWithAccountsAsync()
    {
        var journal = Guid.CreateVersion7();
        var (debit, credit) = await NewAccountPairAsync();
        return (journal, debit, credit);
    }

    private async Task<(Guid Debit, Guid Credit)> NewAccountPairAsync()
    {
        var debit = await NewAccountAsync(kind: "system_clearing");
        var credit = await NewAccountAsync(kind: "user_available", user: true);
        return (debit, credit);
    }

    private async Task<Guid> NewAccountAsync(
        string kind = "system_clearing",
        bool user = false,
        string assetCode = "USD",
        bool practiceBook = false)
    {
        var book = practiceBook ? "practice" : "real";
        var ownerType = user ? "user" : "system";
        var ownerId = user ? Guid.CreateVersion7() : Guid.Empty;
        var allowNegative = user ? "false" : "true";
        var existing = await Database.QueryAsync(
            _fixture.OwnerConnectionString,
            $"""
            SELECT id FROM ledger.accounts
            WHERE book = '{book}' AND owner_type = '{ownerType}' AND owner_id = '{ownerId}'
                  AND kind = '{kind}' AND asset_code = '{assetCode}'
            """,
            TestContext.Current.CancellationToken);
        if (existing.Count > 0)
        {
            return (Guid)existing[0]["id"]!;
        }

        var id = Guid.CreateVersion7();
        await Database.ExecuteAsync(
            _fixture.OwnerConnectionString,
            $"""
            INSERT INTO ledger.accounts (id, book, owner_type, owner_id, kind, asset_code, allow_negative, created_at)
            VALUES ('{id}', '{book}', '{ownerType}', '{ownerId}', '{kind}', '{assetCode}', {allowNegative}, now());
            """,
            TestContext.Current.CancellationToken);
        return id;
    }

    private static string Hash() => new string('a', 64);

    private static async Task ExpectSqlErrorAsync(string connectionString, string sql, string expectedFragment)
    {
        var exception = await Should.ThrowAsync<PostgresException>(() =>
            Database.ExecuteAsync(connectionString, sql, TestContext.Current.CancellationToken));
        exception.Message.ShouldContain(expectedFragment);
    }

    private Task ExpectSqlErrorAsync(string sql, string expectedFragment)
        => ExpectSqlErrorAsync(_fixture.OwnerConnectionString, sql, expectedFragment);
}
