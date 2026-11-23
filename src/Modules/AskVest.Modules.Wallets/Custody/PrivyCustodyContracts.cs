using System.Text.Json.Serialization;

namespace AskVest.Modules.Wallets.Custody;

// Wire shapes for the Privy REST API. Only the fields the custody adapter
// reads are modeled; unknown fields are ignored by the serializer.

internal sealed record PrivyWalletListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<PrivyWallet>? Data,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

internal sealed record PrivyWallet(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("chain_type")] string? ChainType,
    [property: JsonPropertyName("external_id")] string? ExternalId);

internal sealed record PrivyWalletResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("chain_type")] string? ChainType);

internal sealed record PrivyTransactionsResponse(
    [property: JsonPropertyName("transactions")] IReadOnlyList<PrivyTransaction>? Transactions,
    [property: JsonPropertyName("next_cursor")] string? NextCursor);

internal sealed record PrivyTransaction(
    [property: JsonPropertyName("caip2")] string? Caip2,
    [property: JsonPropertyName("transaction_hash")] string? TransactionHash,
    [property: JsonPropertyName("privy_transaction_id")] string? PrivyTransactionId,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("created_at")] long CreatedAt,
    [property: JsonPropertyName("wallet_id")] string? WalletId,
    [property: JsonPropertyName("details")] PrivyTransactionDetails? Details);

internal sealed record PrivyTransactionDetails(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("sender")] string? Sender,
    [property: JsonPropertyName("recipient")] string? Recipient,
    [property: JsonPropertyName("asset")] string? Asset,
    [property: JsonPropertyName("raw_value")] string? RawValue,
    [property: JsonPropertyName("raw_value_decimals")] int RawValueDecimals);

internal sealed record PrivySendTransactionRequest(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("caip2")] string Caip2,
    [property: JsonPropertyName("chain_type")] string ChainType,
    [property: JsonPropertyName("reference_id")] string ReferenceId,
    [property: JsonPropertyName("params")] PrivySendTransactionParams Params);

internal sealed record PrivySendTransactionParams(
    [property: JsonPropertyName("transaction")] PrivyTransactionRequest Transaction);

internal sealed record PrivyTransactionRequest(
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("data")] string Data,
    [property: JsonPropertyName("value")] string Value);

internal sealed record PrivyRpcResponse(
    [property: JsonPropertyName("data")] PrivyRpcData? Data);

internal sealed record PrivyRpcData(
    [property: JsonPropertyName("hash")] string? Hash,
    [property: JsonPropertyName("transaction_id")] string? TransactionId,
    [property: JsonPropertyName("caip2")] string? Caip2);

internal sealed record PrivyTransactionsByReferenceResponse(
    [property: JsonPropertyName("transactions")] IReadOnlyList<PrivyReferenceTransaction>? Transactions);

internal sealed record PrivyReferenceTransaction(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("caip2")] string? Caip2,
    [property: JsonPropertyName("transaction_hash")] string? TransactionHash,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("created_at")] long CreatedAt);

internal sealed record PrivyBalanceResponse(
    [property: JsonPropertyName("balances")] IReadOnlyList<PrivyBalance>? Balances);

internal sealed record PrivyBalance(
    [property: JsonPropertyName("chain")] string? Chain,
    [property: JsonPropertyName("asset")] string? Asset,
    [property: JsonPropertyName("raw_value")] string? RawValue,
    [property: JsonPropertyName("raw_value_decimals")] int RawValueDecimals);
