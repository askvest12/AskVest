using AskVest.Modules.Compliance.Configuration;
using AskVest.Modules.Compliance.Domain;
using AskVest.Modules.Compliance.Domain.Enums;
using AskVest.Modules.Compliance.Persistence;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Compliance.Services;

internal sealed record ConsentAcceptanceItem(string Type, string Version, string Sha256);

internal sealed record DocumentListing(
    string Type,
    string Version,
    Uri Url,
    string Sha256,
    string RequiredFor,
    bool Accepted);

internal sealed class ConsentService(
    ComplianceDbContext db,
    ProfileStore profileStore,
    IOptions<ComplianceOptions> options,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<DocumentListing>> GetDocumentsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var acceptedKeys = await AcceptedKeysAsync(userId, cancellationToken);
        return options.Value.Documents
            .Select(document => new DocumentListing(
                document.Type,
                document.Version,
                document.Url!,
                document.Sha256,
                document.RequiredFor,
                acceptedKeys.Contains(EligibilityEvaluator.ConsentKey(document.Type, document.Version))))
            .ToList();
    }

    public async Task<Result> AcceptAsync(
        Guid userId,
        IReadOnlyList<ConsentAcceptanceItem> items,
        string? ip,
        string? userAgent,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var configured = options.Value.Documents
            .ToDictionary(document => document.Type, StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (!configured.TryGetValue(item.Type, out var document)
                || !string.Equals(item.Version, document.Version, StringComparison.Ordinal)
                || !string.Equals(item.Sha256, document.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure(new Error(
                    ErrorCodes.ComplianceConsentVersionMismatch,
                    "The consent request does not match the current documents.",
                    ErrorType.Conflict));
            }
        }

        var acceptedKeys = await AcceptedKeysAsync(userId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var recorded = new List<string>();
        foreach (var item in items.DistinctBy(i => i.Type))
        {
            var key = EligibilityEvaluator.ConsentKey(item.Type, item.Version);
            if (acceptedKeys.Contains(key))
            {
                continue;
            }

            db.ConsentRecords.Add(new Persistence.Entities.ConsentRecord
            {
                Id = Guid.CreateVersion7(),
                UserId = userId,
                DocumentType = item.Type,
                Version = item.Version,
                ContentHash = item.Sha256,
                AcceptedAt = now,
                Ip = ip,
                UserAgent = userAgent,
            });
            recorded.Add(item.Type);
        }

        if (recorded.Count == 0)
        {
            return Result.Success();
        }

        profileStore.AddEvent(
            userId,
            ComplianceEventType.ConsentAccepted,
            correlationId,
            $"types={string.Join(',', recorded)}");
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<HashSet<string>> AcceptedKeysAsync(Guid userId, CancellationToken cancellationToken)
        => (await db.ConsentRecords.AsNoTracking()
                .Where(c => c.UserId == userId)
                .Select(c => c.DocumentType + "@" + c.Version)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
}
