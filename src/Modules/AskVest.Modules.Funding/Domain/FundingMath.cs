using System.Security.Cryptography;
using System.Text;

namespace AskVest.Modules.Funding.Domain;

/// <summary>
/// Pure funding math: conversion quote computation, terms hashes, deposit
/// and withdrawal limit evaluation, the fresh-deposit lock and the fiat
/// reserves comparison. No I/O, fully deterministic.
/// </summary>
public static class FundingMath
{
    /// <summary>
    /// Computes a firm conversion quote. The receive amount is the target
    /// amount after the spread, rounded down to the target scale; the fee is
    /// a share of the receive amount rounded half up; the user nets
    /// receive minus fee.
    /// </summary>
    public static ConversionQuote ComputeQuote(
        string pair,
        string sourceCurrency,
        string targetCurrency,
        decimal sourceAmount,
        decimal midRate,
        int sourceScale,
        int targetScale,
        int spreadBps,
        int feeBps)
    {
        var effectiveRate = RoundToScale(
            midRate * (10_000m - spreadBps) / 10_000m, sourceScale + targetScale + 4);
        var receive = RoundDown(sourceAmount * effectiveRate, targetScale);
        var fee = RoundHalfUp(receive * feeBps / 10_000m, targetScale);
        var net = receive - fee;
        var termsHash = Hash($"conversion|{pair}|{FundingText.Canonical(sourceAmount)}|{FundingText.Canonical(midRate)}|{spreadBps}|{feeBps}|{FundingText.Canonical(receive)}|{FundingText.Canonical(fee)}|{FundingText.Canonical(net)}");
        return new ConversionQuote(
            pair, sourceCurrency, targetCurrency, sourceAmount, midRate,
            effectiveRate, receive, fee, net, termsHash);
    }

    /// <summary>
    /// Withdrawal preview terms hash. Any change to amount, fee, total,
    /// beneficiary display, currency or expiry inputs changes the hash.
    /// </summary>
    public static string WithdrawalTermsHash(
        Guid beneficiaryId,
        string currency,
        decimal receiveAmount,
        decimal fee,
        decimal totalDebited,
        DateTimeOffset expiresAt)
        => Hash(
            $"withdrawal|{beneficiaryId:N}|{currency}|{FundingText.Canonical(receiveAmount)}|{FundingText.Canonical(fee)}|{FundingText.Canonical(totalDebited)}|{expiresAt.UtcTicks}");

    /// <summary>
    /// Cash withdrawable now: the available balance minus deposits credited
    /// inside the fresh-deposit lock window, never below zero.
    /// </summary>
    public static decimal WithdrawableAfterFreshDepositLock(
        decimal available,
        decimal recentlyCredited)
        => Math.Max(0m, available - recentlyCredited);

    /// <summary>
    /// Fiat reserves: only a shortfall matters. Owed is user available plus
    /// user held plus system fees per currency; held is the provider
    /// settlement balance. Returns the shortfall (0 when covered or in
    /// excess).
    /// </summary>
    public static decimal ReservesShortfall(decimal owed, decimal held)
        => owed > held ? owed - held : 0m;

    public static bool ReservesMatch(decimal owed, decimal held, decimal tolerance)
        => Math.Abs(owed - held) <= tolerance;

    public static decimal RoundDown(decimal value, int scale)
        => decimal.Round(value, scale, MidpointRounding.ToZero);

    public static decimal RoundHalfUp(decimal value, int scale)
        => decimal.Round(value, scale, MidpointRounding.AwayFromZero);

    private static decimal RoundToScale(decimal value, int scale)
        => decimal.Round(value, scale, MidpointRounding.AwayFromZero);

    private static string Hash(string canonical)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed record ConversionQuote(
    string Pair,
    string SourceCurrency,
    string TargetCurrency,
    decimal SourceAmount,
    decimal MidRate,
    decimal EffectiveRate,
    decimal ReceiveAmount,
    decimal FeeAmount,
    decimal NetAmount,
    string TermsHash);
