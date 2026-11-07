using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AskVest.Modules.Ledger.Contracts;
using AskVest.SharedKernel;

namespace AskVest.Modules.Ledger.Services;

internal static class JournalValidation
{
    private const decimal MaxAmount = 10_000_000_000m;
    private const int MaxScale = 18;

    public static Error? ValidateShape(PostJournalRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
        {
            return Invalid("idempotencyKey", "The idempotency key is required and must be at most 200 characters.");
        }

        if (string.IsNullOrWhiteSpace(request.ReasonCode) || request.ReasonCode.Length > 64)
        {
            return Invalid("reasonCode", "The reason code is required and must be at most 64 characters.");
        }

        if (string.IsNullOrWhiteSpace(request.ReferenceType) || request.ReferenceType.Length > 64)
        {
            return Invalid("referenceType", "The reference type is required and must be at most 64 characters.");
        }

        if (request.Lines is null || request.Lines.Count < 2)
        {
            return Invalid("lines", "A journal requires at least two lines.");
        }

        return null;
    }

    public static Error? ValidateLine(LedgerLine line, Book book, IReadOnlyDictionary<string, int> assetScales)
    {
        var account = line.Account;
        if (account.Book != book)
        {
            return new Error(
                ErrorCodes.LedgerBookMismatch,
                "Every line must reference an account in the journal's book.",
                ErrorType.BusinessRule);
        }

        if (account.Kind == AccountKind.SystemPracticeIssuance && book != Book.Practice)
        {
            return new Error(
                ErrorCodes.LedgerBookMismatch,
                "The practice issuance account only exists in the practice book.",
                ErrorType.BusinessRule);
        }

        var ownerKindValid = account.OwnerType == AccountOwnerType.User
            ? (account.Kind is AccountKind.UserAvailable or AccountKind.UserHeld) && account.OwnerId != Guid.Empty
            : (account.Kind is AccountKind.SystemClearing or AccountKind.SystemFees or AccountKind.SystemPracticeIssuance)
                && account.OwnerId == Guid.Empty;
        if (!ownerKindValid)
        {
            return Invalid("lines", "The account reference does not match its owner type and kind.");
        }

        if (string.IsNullOrWhiteSpace(line.AssetCode) || line.AssetCode.Length > 16)
        {
            return Invalid("assetCode", "The asset code is required and must be at most 16 characters.");
        }

        if (!assetScales.TryGetValue(line.AssetCode, out var scale))
        {
            return new Error(
                ErrorCodes.LedgerAssetUnknown,
                "The asset is not registered.",
                ErrorType.BusinessRule);
        }

        return ValidateAmount(line.Amount, scale);
    }

    public static Error? ValidateAmount(decimal amount, int scale)
    {
        if (amount == 0m || decimal.Abs(amount) >= MaxAmount)
        {
            return new Error(
                ErrorCodes.LedgerAmountInvalid,
                "The amount must be non-zero and within ten integer digits.",
                ErrorType.BusinessRule);
        }

        if (scale < 0 || scale > MaxScale || amount != Math.Round(amount, scale))
        {
            return new Error(
                ErrorCodes.LedgerAmountInvalid,
                "The amount has more fractional digits than the asset scale allows.",
                ErrorType.BusinessRule);
        }

        return null;
    }

    public static Error? ValidateZeroSum(IReadOnlyList<LedgerLine> lines)
    {
        var sums = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            sums[line.AssetCode] = sums.GetValueOrDefault(line.AssetCode) + line.Amount;
        }

        return sums.Values.Any(sum => sum != 0m)
            ? new Error(
                ErrorCodes.LedgerUnbalanced,
                "The journal entries do not sum to zero per asset.",
                ErrorType.BusinessRule)
            : null;
    }

    public static string ContentHash(
        Book book,
        string reasonCode,
        string referenceType,
        Guid? referenceId,
        Guid? reversesJournalId,
        IReadOnlyList<LedgerLine> lines)
    {
        var canonical = new StringBuilder()
            .Append(LedgerText.ToDb(book)).Append('|')
            .Append(reasonCode).Append('|')
            .Append(referenceType).Append('|')
            .Append(referenceId?.ToString("N") ?? string.Empty).Append('|')
            .Append(reversesJournalId?.ToString("N") ?? string.Empty);
        foreach (var line in lines.Select(CanonicalLine).OrderBy(l => l, StringComparer.Ordinal))
        {
            canonical.Append('\n').Append(line);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private static string CanonicalLine(LedgerLine line)
    {
        var account = line.Account;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{LedgerText.ToDb(account.Book)}|{LedgerText.ToDb(account.OwnerType)}|{account.OwnerId:N}|{LedgerText.ToDb(account.Kind)}|{line.AssetCode}|{FormatAmount(line.Amount)}");
    }

    private static string FormatAmount(decimal amount)
        => amount.ToString("0.############################", CultureInfo.InvariantCulture);

    private static Error Invalid(string field, string message)
        => new(ErrorCodes.ValidationFailed, message, ErrorType.Validation, [new ValidationFailure(field, message)]);
}
