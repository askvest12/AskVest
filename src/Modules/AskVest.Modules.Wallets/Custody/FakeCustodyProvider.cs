using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AskVest.Modules.Wallets.Configuration;
using AskVest.SharedKernel;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Wallets.Custody;

/// <summary>
/// The deterministic in-memory custody provider used in development and
/// tests. Deposit instructions derive from the user reference; deposits are
/// injected through the diagnostics probe; withdrawals are keyed by our
/// reference so repeated submissions resolve to the same stored withdrawal.
/// The scenario is mutable so probes and tests can arm failure modes. No key
/// material exists anywhere: this fake holds addresses and quantities only.
/// Refused in Production at startup.
/// </summary>
internal sealed class FakeCustodyProvider(IOptions<WalletsOptions> options, TimeProvider timeProvider)
    : ICustodyProvider
{
    internal const string ProviderKey = "fake";

    private readonly ConcurrentDictionary<string, CustodyDepositInstruction> _instructions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, InjectedDeposit> _deposits = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StoredWithdrawal> _withdrawals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _issuedAddresses = new(StringComparer.Ordinal);
    private long _depositSequence;

    public string ProviderName => ProviderKey;

    public IReadOnlyList<string> SupportedChains
        => options.Value.Custody.RouteProviders
            .Where(route => string.Equals(route.Value, ProviderKey, StringComparison.Ordinal))
            .Select(route => route.Key)
            .OrderBy(chainId => chainId, StringComparer.Ordinal)
            .ToList();

    public bool SupportsMemo => false;

    public string FinalityRule => "fake_finality";

    public Task<Result<CustodyDepositInstruction>> GetDepositInstructionAsync(
        string userReference,
        string chainId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!SupportedChains.Contains(chainId, StringComparer.Ordinal))
        {
            return Task.FromResult(Result.Failure<CustodyDepositInstruction>(new Error(
                ErrorCodes.WalletNotAvailable,
                "The chain has no custody provider.",
                ErrorType.BusinessRule)));
        }

        var key = $"{userReference}|{chainId}";
        var instruction = _instructions.GetOrAdd(key, _ =>
        {
            var digest = Hash(key);
            var address = $"fake-addr-{chainId}-{digest[..16]}";
            _issuedAddresses[address] = 1;
            return new CustodyDepositInstruction(
                address,
                null,
                $"fake-pref-{digest[..24]}");
        });
        return Task.FromResult(Result.Success(instruction));
    }

    public Task<Result<CustodyDepositPage>> ListDepositsAsync(
        string chainId,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var after = long.TryParse(cursor, out var value) ? value : 0L;
        var now = timeProvider.GetUtcNow();
        var deposits = _deposits.Values
            .Where(deposit => deposit.Record.ChainId == chainId && deposit.Sequence > after)
            .OrderBy(deposit => deposit.Sequence)
            .Select(deposit => WithEffectiveFinality(deposit.Record, now))
            .ToList();
        var nextCursor = deposits.Count == 0
            ? cursor
            : deposits[^1].ProviderDepositId["fake-dep-".Length..];
        return Task.FromResult(Result.Success(new CustodyDepositPage(deposits, nextCursor)));
    }

    /// <summary>
    /// Test and probe hook: injects a deposit to a specific destination.
    /// </summary>
    internal void InjectDepositTo(
        string chainId,
        string destinationAddress,
        string tokenContractAddress,
        decimal quantity,
        string senderAddress,
        CustodyDepositFinality finality)
    {
        var sequence = Interlocked.Increment(ref _depositSequence);
        var deposit = new CustodyDeposit(
            ProviderKey,
            $"fake-dep-{sequence}",
            destinationAddress,
            chainId,
            tokenContractAddress,
            quantity,
            senderAddress,
            $"fake-settle-in-{sequence}",
            finality,
            timeProvider.GetUtcNow());
        _deposits[$"{ProviderKey}|{deposit.ProviderDepositId}"] = new InjectedDeposit(sequence, deposit);
    }

    public Task<CustodySubmitResult> SubmitWithdrawalAsync(
        CustodyWithdrawRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scenario = options.Value.Custody.Fake.Scenario;
        if (string.Equals(scenario, "rejected", StringComparison.Ordinal))
        {
            return Task.FromResult(new CustodySubmitResult(
                CustodySubmitKind.Rejected, null, "custody_rejected"));
        }

        if (!string.Equals(scenario, "submit_timeout_lost", StringComparison.Ordinal))
        {
            _withdrawals[request.OurReference] = new StoredWithdrawal(
                request, timeProvider.GetUtcNow(), scenario);
        }

        return Task.FromResult(string.Equals(scenario, "submit_timeout_filled", StringComparison.Ordinal)
            ? new CustodySubmitResult(CustodySubmitKind.Unknown, null, null)
            : new CustodySubmitResult(
                CustodySubmitKind.Acknowledged,
                $"fake-wd-{request.OurReference}",
                null));
    }

    public Task<CustodyWithdrawalStatus> GetWithdrawalStatusAsync(
        string ourReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(options.Value.Custody.Fake.Scenario, "status_unavailable", StringComparison.Ordinal))
        {
            return Task.FromResult(new CustodyWithdrawalStatus(
                CustodyWithdrawalStatusKind.Unavailable, null, null));
        }

        if (!_withdrawals.TryGetValue(ourReference, out var stored))
        {
            return Task.FromResult(new CustodyWithdrawalStatus(
                CustodyWithdrawalStatusKind.NotFound, null, null));
        }

        if (string.Equals(stored.Scenario, "rejected", StringComparison.Ordinal))
        {
            return Task.FromResult(new CustodyWithdrawalStatus(
                CustodyWithdrawalStatusKind.Failed, null, "custody_rejected"));
        }

        var multiplier = stored.Scenario == "delayed" ? 4 : 1;
        var completesAt = stored.SubmittedAt
            + TimeSpan.FromSeconds(options.Value.Custody.Fake.CompletionDelaySeconds * multiplier);
        if (timeProvider.GetUtcNow() < completesAt)
        {
            return Task.FromResult(new CustodyWithdrawalStatus(
                CustodyWithdrawalStatusKind.Pending, null, null));
        }

        return Task.FromResult(new CustodyWithdrawalStatus(
            CustodyWithdrawalStatusKind.Completed,
            $"fake-settle-{ourReference}",
            null));
    }

    public Task<Result<IReadOnlyList<CustodyHolding>>> GetHoldingsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = timeProvider.GetUtcNow();
        var totals = new Dictionary<(string ChainId, string Token), decimal>();
        foreach (var deposit in _deposits.Values)
        {
            var record = WithEffectiveFinality(deposit.Record, now);
            if (record.Finality != CustodyDepositFinality.Final)
            {
                continue;
            }

            var key = (record.ChainId, record.TokenContractAddress);
            totals[key] = totals.GetValueOrDefault(key) + record.Quantity;
        }

        foreach (var withdrawal in _withdrawals.Values)
        {
            var multiplier = withdrawal.Scenario == "delayed" ? 4 : 1;
            var completesAt = withdrawal.SubmittedAt
                + TimeSpan.FromSeconds(options.Value.Custody.Fake.CompletionDelaySeconds * multiplier);
            if (now < completesAt)
            {
                continue;
            }

            var key = (withdrawal.Request.ChainId, withdrawal.Request.TokenContractAddress);
            totals[key] = totals.GetValueOrDefault(key) - withdrawal.Request.Quantity;
        }

        var mismatch = string.Equals(options.Value.Custody.Fake.Scenario, "holdings_mismatch", StringComparison.Ordinal);
        var holdings = totals
            .Select(pair => new CustodyHolding(
                pair.Key.ChainId,
                pair.Key.Token,
                mismatch ? pair.Value - 1m : pair.Value))
            .ToList();
        return Task.FromResult(Result.Success<IReadOnlyList<CustodyHolding>>(holdings));
    }

    public Task<Result<bool>> IsInternalAddressAsync(
        string chainId,
        string address,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var internalAddress = _issuedAddresses.ContainsKey(address)
            || address.Contains("fake-internal", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(Result.Success(internalAddress));
    }

    /// <summary>
    /// Test and probe hook: overrides the configured scenario at runtime. The
    /// fake is refused in Production, so this can never arm a live provider.
    /// </summary>
    internal void SetScenario(string scenario)
        => options.Value.Custody.Fake.Scenario = scenario;

    internal int WithdrawalCount(string ourReference)
        => _withdrawals.TryGetValue(ourReference, out _) ? 1 : 0;

    /// <summary>
    /// Pending deposits report final once the configured pending window has
    /// elapsed, mirroring a chain reaching its confirmation depth.
    /// </summary>
    private CustodyDeposit WithEffectiveFinality(CustodyDeposit deposit, DateTimeOffset now)
        => deposit.Finality == CustodyDepositFinality.Pending
            && now >= deposit.ObservedAt + TimeSpan.FromSeconds(options.Value.Custody.Fake.PendingFinalitySeconds)
                ? deposit with { Finality = CustodyDepositFinality.Final }
                : deposit;

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record InjectedDeposit(long Sequence, CustodyDeposit Record);

    private sealed record StoredWithdrawal(
        CustodyWithdrawRequest Request,
        DateTimeOffset SubmittedAt,
        string Scenario);
}
