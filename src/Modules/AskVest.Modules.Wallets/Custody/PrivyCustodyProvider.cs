using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AskVest.Modules.Wallets.Configuration;
using AskVest.Modules.Wallets.Services;
using AskVest.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Wallets.Custody;

/// <summary>
/// The Privy custody adapter. One Privy wallet per user per chain family
/// serves every chain in that family; the wallet id is the provider
/// reference and the address is the deposit instruction. Deposits are
/// discovered by polling wallet transaction history round-robin; Privy has
/// no chain-wide feed, so the scan state lives in memory and restarts
/// rescan recent pages (dedupe downstream makes replay safe). Withdrawals
/// are ERC-20 transfers from the configured treasury wallet on EVM chains;
/// other families reject definitively. No key material exists here: every
/// signature happens inside Privy.
/// </summary>
internal sealed partial class PrivyCustodyProvider(
    HttpClient http,
    IOptions<WalletsOptions> options,
    ApprovedStablecoins approvedStablecoins,
    TimeProvider timeProvider,
    ILogger<PrivyCustodyProvider> logger)
    : ICustodyProvider
{
    internal const string ProviderKey = "privy";

    internal const string HttpClientName = "AskVest.Privy";

    private const string EvmFamily = "ethereum";
    private const string Erc20TransferSelector = "0xa9059cbb";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // CAIP-2 chain id to the chain names Privy's transaction and balance
    // endpoints accept. Only names in Privy's history enum are listed;
    // PrivyOptions:HistoryChainNames overrides or extends this table.
    private static readonly Dictionary<string, string> DefaultHistoryChainNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["eip155:1"] = "ethereum",
            ["eip155:10"] = "optimism",
            ["eip155:56"] = "bsc",
            ["eip155:137"] = "polygon",
            ["eip155:143"] = "monad",
            ["eip155:8453"] = "base",
            ["eip155:84532"] = "base_sepolia",
            ["eip155:4217"] = "tempo",
            ["eip155:59144"] = "linea",
            ["eip155:11155111"] = "sepolia",
            ["eip155:42161"] = "arbitrum",
            ["eip155:43114"] = "avalanche",
        };

    private static readonly HashSet<string> FinalStatuses = new(StringComparer.Ordinal)
    {
        "confirmed", "finalized",
    };

    private static readonly HashSet<string> FailedStatuses = new(StringComparer.Ordinal)
    {
        "failed", "execution_reverted",
    };

    private readonly ConcurrentDictionary<string, DepositScanState> _scans = new(StringComparer.Ordinal);

    public string ProviderName => ProviderKey;

    public IReadOnlyList<string> SupportedChains
        => options.Value.Custody.RouteProviders
            .Where(route => string.Equals(route.Value, ProviderKey, StringComparison.Ordinal)
                && IsEip155Chain(route.Key))
            .Select(route => route.Key)
            .OrderBy(chainId => chainId, StringComparer.Ordinal)
            .ToList();

    public bool SupportsMemo => false;

    public string FinalityRule => "privy_confirmed_or_finalized";

    public async Task<Result<CustodyDepositInstruction>> GetDepositInstructionAsync(
        string userReference,
        string chainId,
        CancellationToken cancellationToken = default)
    {
        if (!IsEip155Chain(chainId))
        {
            return Result.Failure<CustodyDepositInstruction>(Unavailable("unsupported_chain_family"));
        }

        if (!ExternalIdPattern().IsMatch(userReference))
        {
            return Result.Failure<CustodyDepositInstruction>(Unavailable("user_reference_invalid"));
        }

        var existing = await FindWalletByExternalIdAsync(userReference, cancellationToken);
        if (existing is not null)
        {
            return Result.Success(new CustodyDepositInstruction(existing.Address, null, existing.Id));
        }

        var privy = options.Value.Custody.Privy;
        var request = new
        {
            chain_type = EvmFamily,
            external_id = userReference,
            display_name = "AskVest deposit",
            owner_id = string.IsNullOrEmpty(privy.OwnerKeyQuorumId) ? null : privy.OwnerKeyQuorumId,
        };
        using var message = JsonContent.Create(request, options: JsonOptions);
        using var idempotency = new HttpRequestMessage(HttpMethod.Post, "/v1/wallets") { Content = message };
        idempotency.Headers.TryAddWithoutValidation("privy-idempotency-key", IdempotencyKey(userReference));
        var response = await SendAsync(idempotency, cancellationToken);
        if (!response.IsSuccess)
        {
            return Result.Failure<CustodyDepositInstruction>(MapHttpFailure(response));
        }

        var wallet = DeserializeBody<PrivyWalletResponse>(response);
        if (wallet is null || string.IsNullOrWhiteSpace(wallet.Id) || string.IsNullOrWhiteSpace(wallet.Address))
        {
            return Result.Failure<CustodyDepositInstruction>(Unavailable("wallet_response_invalid"));
        }

        return Result.Success(new CustodyDepositInstruction(wallet.Address, null, wallet.Id));
    }

    public async Task<Result<CustodyDepositPage>> ListDepositsAsync(
        string chainId,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        if (!TryHistoryChainName(chainId, out var historyChain))
        {
            return Result.Failure<CustodyDepositPage>(Unavailable("chain_not_supported"));
        }

        var scan = _scans.GetOrAdd(chainId, _ => new DepositScanState());
        if (scan.Wallets is null || scan.WalletsRefreshedAt < timeProvider.GetUtcNow()
            - TimeSpan.FromSeconds(options.Value.Custody.Privy.WalletListRefreshSeconds))
        {
            var wallets = await ListWalletsAsync(cancellationToken);
            if (wallets is null)
            {
                return Result.Failure<CustodyDepositPage>(Unavailable("wallet_list_failed"));
            }

            scan.ReplaceWallets(wallets, timeProvider.GetUtcNow());
        }

        var deposits = new List<CustodyDeposit>();
        var walletCount = scan.Wallets!.Count;
        var batchSize = Math.Min(Math.Max(1, options.Value.Custody.Privy.WalletScanBatchSize), walletCount);
        for (var scanned = 0; scanned < batchSize; scanned++)
        {
            var index = scan.NextIndex % walletCount;
            var wallet = scan.Wallets[index];
            scan.NextIndex = (index + 1) % walletCount;

            var page = await GetWalletTransactionsAsync(wallet, historyChain, cancellationToken);
            if (page is null)
            {
                // Skip this wallet for the cycle; the next poll retries it.
                continue;
            }

            foreach (var transaction in page.Transactions ?? [])
            {
                var deposit = MapDeposit(chainId, wallet, transaction);
                if (deposit is not null)
                {
                    deposits.Add(deposit);
                }
            }

            wallet.Cursor = page.NextCursor;
        }

        return Result.Success(new CustodyDepositPage(deposits, null));
    }

    public async Task<CustodySubmitResult> SubmitWithdrawalAsync(
        CustodyWithdrawRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsEip155Chain(request.ChainId))
        {
            return new CustodySubmitResult(CustodySubmitKind.Rejected, null, "unsupported_chain_family");
        }

        var privy = options.Value.Custody.Privy;
        if (string.IsNullOrWhiteSpace(privy.TreasuryWalletId))
        {
            return new CustodySubmitResult(CustodySubmitKind.Rejected, null, "treasury_not_configured");
        }

        if (!IsEvmAddress(request.TokenContractAddress) || !IsEvmAddress(request.DestinationAddress))
        {
            return new CustodySubmitResult(CustodySubmitKind.Rejected, null, "invalid_address");
        }

        var baseUnits = ToBaseUnits(request.Quantity, request.TokenDecimals);
        if (baseUnits is null)
        {
            return new CustodySubmitResult(CustodySubmitKind.Rejected, null, "amount_precision");
        }

        var body = new PrivySendTransactionRequest(
            "eth_sendTransaction",
            request.ChainId,
            EvmFamily,
            request.OurReference,
            new PrivySendTransactionParams(new PrivyTransactionRequest(
                request.TokenContractAddress,
                Erc20Transfer(request.DestinationAddress, baseUnits.Value),
                "0x0")));

        using var message = JsonContent.Create(body, options: JsonOptions);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/wallets/{privy.TreasuryWalletId}/rpc") { Content = message };
        httpRequest.Headers.TryAddWithoutValidation("privy-idempotency-key", request.OurReference);

        var response = await SendAsync(httpRequest, cancellationToken);
        if (!response.IsSuccess)
        {
            var clientError = response.StatusCode is >= 400 and < 500 and not 429;
            return clientError
                ? new CustodySubmitResult(CustodySubmitKind.Rejected, null, $"http_{response.StatusCode}")
                : new CustodySubmitResult(CustodySubmitKind.Unknown, null, null);
        }

        var rpc = DeserializeBody<PrivyRpcResponse>(response);
        if (rpc?.Data is null)
        {
            return new CustodySubmitResult(CustodySubmitKind.Unknown, null, null);
        }

        var reference = rpc.Data.TransactionId ?? rpc.Data.Hash;
        return string.IsNullOrWhiteSpace(reference)
            ? new CustodySubmitResult(CustodySubmitKind.Unknown, null, null)
            : new CustodySubmitResult(CustodySubmitKind.Acknowledged, reference, null);
    }

    public async Task<CustodyWithdrawalStatus> GetWithdrawalStatusAsync(
        string ourReference,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/v1/transactions?reference_id={Uri.EscapeDataString(ourReference)}");
        var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccess)
        {
            return new CustodyWithdrawalStatus(CustodyWithdrawalStatusKind.Unavailable, null, null);
        }

        var transactions = DeserializeBody<PrivyTransactionsByReferenceResponse>(response);
        var transaction = transactions?.Transactions is { Count: > 0 } found ? found[0] : null;
        if (transaction is null)
        {
            return new CustodyWithdrawalStatus(CustodyWithdrawalStatusKind.NotFound, null, null);
        }

        var status = transaction.Status ?? string.Empty;
        var kind = status switch
        {
            var s when FinalStatuses.Contains(s) => CustodyWithdrawalStatusKind.Completed,
            var s when FailedStatuses.Contains(s) => CustodyWithdrawalStatusKind.Failed,
            "provider_error" => CustodyWithdrawalStatusKind.Unavailable,
            _ => CustodyWithdrawalStatusKind.Pending,
        };
        return new CustodyWithdrawalStatus(kind, transaction.TransactionHash, status);
    }

    public async Task<Result<IReadOnlyList<CustodyHolding>>> GetHoldingsAsync(
        CancellationToken cancellationToken = default)
    {
        var wallets = await ListWalletsAsync(cancellationToken);
        if (wallets is null)
        {
            return Result.Failure<IReadOnlyList<CustodyHolding>>(Unavailable("wallet_list_failed"));
        }

        var totals = new Dictionary<(string ChainId, string Contract), decimal>();
        foreach (var wallet in wallets.Take(200))
        {
            using var balanceRequest = new HttpRequestMessage(
                HttpMethod.Get, $"/v1/wallets/{wallet.Id}/balance");
            var response = await SendAsync(balanceRequest, cancellationToken);
            if (!response.IsSuccess || response.Body is null)
            {
                continue;
            }

            var balances = DeserializeBody<PrivyBalanceResponse>(response);
            foreach (var balance in balances?.Balances ?? [])
            {
                if (balance is null || !TryCaip2ToChainId(balance.Chain, out var chainId)
                    || string.IsNullOrWhiteSpace(balance.Asset)
                    || !TryParseQuantity(balance.RawValue, balance.RawValueDecimals, out var quantity))
                {
                    continue;
                }

                var contract = ResolveTokenContract(chainId, balance.Asset);
                var key = (chainId, contract);
                totals[key] = totals.GetValueOrDefault(key) + quantity;
            }
        }

        var holdings = totals
            .Select(pair => new CustodyHolding(pair.Key.ChainId, pair.Key.Contract, pair.Value))
            .ToList();
        return Result.Success<IReadOnlyList<CustodyHolding>>(holdings);
    }

    public async Task<Result<bool>> IsInternalAddressAsync(
        string chainId,
        string address,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/v1/wallets?address={Uri.EscapeDataString(address)}&limit=5");
        var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccess || response.Body is null)
        {
            return Result.Failure<bool>(Unavailable("address_lookup_failed"));
        }

        var wallets = DeserializeBody<PrivyWalletListResponse>(response);
        return Result.Success(wallets?.Data is { Count: > 0 });
    }

    private async Task<PrivyWallet?> FindWalletByExternalIdAsync(
        string externalId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/v1/wallets?chain_type={EvmFamily}&external_id={Uri.EscapeDataString(externalId)}&limit=1");
        var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccess || response.Body is null)
        {
            return null;
        }

        var wallets = DeserializeBody<PrivyWalletListResponse>(response);
        return wallets?.Data is { Count: > 0 } data ? data[0] : null;
    }

    private async Task<List<PrivyWallet>?> ListWalletsAsync(CancellationToken cancellationToken)
    {
        var wallets = new List<PrivyWallet>();
        string? cursor = null;
        for (var page = 0; page < Math.Max(1, options.Value.Custody.Privy.WalletListMaxPages); page++)
        {
            var url = $"/v1/wallets?chain_type={EvmFamily}&limit=100"
                + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await SendAsync(request, cancellationToken);
            if (!response.IsSuccess || response.Body is null)
            {
                return null;
            }

            var result = DeserializeBody<PrivyWalletListResponse>(response);
            if (result?.Data is null)
            {
                return null;
            }

            wallets.AddRange(result.Data);
            cursor = result.NextCursor;
            if (cursor is null)
            {
                break;
            }
        }

        return wallets;
    }

    private async Task<PrivyTransactionsResponse?> GetWalletTransactionsAsync(
        ScanWallet wallet,
        string historyChain,
        CancellationToken cancellationToken)
    {
        var url = $"/v1/wallets/{wallet.Id}/transactions?chain={Uri.EscapeDataString(historyChain)}&limit=100"
            + (wallet.Cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(wallet.Cursor)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccess || response.Body is null)
        {
            return null;
        }

        return DeserializeBody<PrivyTransactionsResponse>(response);
    }

    private CustodyDeposit? MapDeposit(string chainId, ScanWallet wallet, PrivyTransaction transaction)
    {
        var details = transaction.Details;
        if (details?.Type != "transfer_received"
            || details.Recipient is null
            || !string.Equals(details.Recipient, wallet.Address, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var depositId = transaction.TransactionHash ?? transaction.PrivyTransactionId;
        if (string.IsNullOrWhiteSpace(depositId))
        {
            return null;
        }

        var depositChain = string.IsNullOrWhiteSpace(transaction.Caip2) ? chainId : transaction.Caip2;
        if (!TryParseQuantity(details.RawValue, details.RawValueDecimals, out var quantity)
            || string.IsNullOrWhiteSpace(details.Asset))
        {
            return null;
        }

        return new CustodyDeposit(
            ProviderKey,
            $"{depositChain}:{depositId}",
            details.Recipient,
            depositChain,
            ResolveTokenContract(depositChain, details.Asset),
            quantity,
            details.Sender ?? string.Empty,
            transaction.TransactionHash,
            FinalStatuses.Contains(transaction.Status ?? string.Empty)
                ? CustodyDepositFinality.Final
                : CustodyDepositFinality.Pending,
            DateTimeOffset.FromUnixTimeMilliseconds(transaction.CreatedAt));
    }

    /// <summary>
    /// Maps a Privy asset field to a token contract. Canonical aliases resolve
    /// through the approved stablecoin table; anything already shaped like an
    /// EVM address passes through; other aliases pass through unchanged and
    /// are ignored downstream as unknown tokens.
    /// </summary>
    private string ResolveTokenContract(string chainId, string asset)
    {
        if (IsEvmAddress(asset))
        {
            return asset;
        }

        foreach (var entry in approvedStablecoins.Entries)
        {
            if (!string.Equals(entry.ChainId, chainId, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var alias in entry.Aliases)
            {
                if (string.Equals(alias, asset, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Contract;
                }
            }
        }

        return asset;
    }

    private bool TryHistoryChainName(string chainId, out string historyChain)
    {
        var names = options.Value.Custody.Privy.HistoryChainNames;
        if (names.TryGetValue(chainId, out var configured) && !string.IsNullOrWhiteSpace(configured))
        {
            historyChain = configured;
            return true;
        }

        if (DefaultHistoryChainNames.TryGetValue(chainId, out var builtin))
        {
            historyChain = builtin;
            return true;
        }

        historyChain = string.Empty;
        return false;
    }

    private bool TryCaip2ToChainId(string? chain, out string chainId)
    {
        if (!string.IsNullOrWhiteSpace(chain) && chain.Contains(':', StringComparison.Ordinal))
        {
            chainId = chain;
            return true;
        }

        // Balance endpoints report chain names, not CAIP-2 ids; reverse the
        // history map so holdings land on the same chain ids deposits use.
        var match = DefaultHistoryChainNames
            .Concat(options.Value.Custody.Privy.HistoryChainNames)
            .FirstOrDefault(pair => string.Equals(pair.Value, chain, StringComparison.OrdinalIgnoreCase));
        if (match.Key is not null)
        {
            chainId = match.Key;
            return true;
        }

        chainId = string.Empty;
        return false;
    }

    private static bool TryParseQuantity(string? rawValue, int decimals, out decimal quantity)
    {
        quantity = 0m;
        if (rawValue is null || decimals is < 0 or > 36
            || !decimal.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw))
        {
            return false;
        }

        var divisor = 1m;
        for (var i = 0; i < decimals; i++)
        {
            divisor *= 10m;
        }

        quantity = raw / divisor;
        return true;
    }

    private static BigInteger? ToBaseUnits(decimal quantity, int decimals)
    {
        if (decimals is < 0 or > 36 || quantity < 0m)
        {
            return null;
        }

        var scaled = quantity;
        for (var i = 0; i < decimals; i++)
        {
            scaled *= 10m;
        }

        var truncated = decimal.Truncate(scaled);
        return truncated == scaled ? (BigInteger)truncated : null;
    }

    private static string Erc20Transfer(string destination, BigInteger baseUnits)
    {
        var addressHex = destination[2..];
        var amountHex = baseUnits.ToString("x", CultureInfo.InvariantCulture);
        return string.Concat(
            Erc20TransferSelector,
            addressHex.PadLeft(64, '0'),
            amountHex.PadLeft(64, '0'));
    }

    private static bool IsEip155Chain(string chainId)
        => chainId.StartsWith("eip155:", StringComparison.Ordinal);

    private static bool IsEvmAddress(string address)
        => address.Length == 42
            && address.StartsWith("0x", StringComparison.Ordinal)
            && address[2..].All(Uri.IsHexDigit);

    private static string IdempotencyKey(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static T? DeserializeBody<T>(PrivyResponse response)
    {
        if (response.Body is null)
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(response.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static Error Unavailable(string reason)
        => new(ErrorCodes.WalletUnavailable, $"Custody provider is unavailable: {reason}.", ErrorType.Unavailable);

    private Error MapHttpFailure(PrivyResponse response)
    {
        if (response.StatusCode is 401 or 403)
        {
            logger.LogError("Privy custody authentication failed with status {StatusCode}.", (int)response.StatusCode);
            return Unavailable("authentication_failed");
        }

        if ((int)response.StatusCode >= 500 || response.StatusCode == 0)
        {
            return Unavailable("provider_error");
        }

        return Unavailable($"http_{(int)response.StatusCode}");
    }

    private async Task<PrivyResponse> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var body = response.Content is null
                ? null
                : await response.Content.ReadAsStringAsync(cancellationToken);
            return new PrivyResponse((int)response.StatusCode, body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PrivyResponse(0, null);
        }
        catch (HttpRequestException)
        {
            return new PrivyResponse(0, null);
        }
    }

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex ExternalIdPattern();

    private sealed record PrivyResponse(int StatusCode, string? Body)
    {
        public bool IsSuccess => StatusCode is >= 200 and < 300;
    }

    private sealed class DepositScanState
    {
        public List<ScanWallet>? Wallets { get; private set; }

        public DateTimeOffset WalletsRefreshedAt { get; private set; }

        public int NextIndex { get; set; }

        public void ReplaceWallets(List<PrivyWallet> wallets, DateTimeOffset refreshedAt)
        {
            Wallets = wallets
                .Select(wallet => new ScanWallet(wallet.Id, wallet.Address))
                .ToList();
            WalletsRefreshedAt = refreshedAt;
            NextIndex = 0;
        }
    }

    private sealed class ScanWallet(string id, string address)
    {
        public string Id { get; } = id;

        public string Address { get; } = address;

        public string? Cursor { get; set; }
    }
}
