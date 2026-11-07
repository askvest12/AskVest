using System.Text.RegularExpressions;
using AskVest.Modules.Catalog.Chains;
using AskVest.Modules.Catalog.Configuration;
using AskVest.Modules.Catalog.Domain;
using AskVest.Modules.Catalog.Domain.Enums;
using AskVest.Modules.Catalog.Import.Data;
using AskVest.Modules.Catalog.Persistence;
using AskVest.Modules.Catalog.Persistence.Entities;
using AskVest.Modules.Compliance.Contracts;
using AskVest.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AskVest.Modules.Catalog.Import;

/// <summary>
/// Turns reviewed data files into catalog writes. It validates everything
/// first, collects every problem, and only mutates the DbContext when the
/// caller decides to commit. Removing a file entry downgrades the row
/// (delisted, disabled or suspended); rows are never deleted.
/// </summary>
internal sealed partial class CatalogImportValidator(
    CatalogDbContext db,
    ChainFamilyRegistry chainFamilies,
    IComplianceEligibility compliance,
    IOptions<CatalogOptions> options,
    TimeProvider timeProvider,
    ILogger<CatalogImportValidator> logger)
{
    private static readonly Regex SlugPattern = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex CountryPattern = new("^[A-Z]{2}$", RegexOptions.Compiled);
    private static readonly Regex AssetCodePattern = new("^[A-Z0-9][A-Z0-9._-]{0,15}$", RegexOptions.Compiled);
    private static readonly Regex HttpsPattern = new("^https://", RegexOptions.Compiled);

    private readonly List<TokenPlan> _tokenPlans = [];

    public async Task<CatalogImportResult> ApplyAsync(
        IReadOnlyList<CatalogDataSet> dataSets,
        bool apply,
        CancellationToken cancellationToken = default)
    {
        var result = new CatalogImportResult();
        var now = timeProvider.GetUtcNow();
        _tokenPlans.Clear();

        var chainData = Flatten(dataSets, set => set.Chains.Chains, set => set.Chains.Version);
        var issuerData = Flatten(dataSets, set => set.Issuers.Issuers, set => set.Issuers.Version);
        var instrumentData = Flatten(dataSets, set => set.Instruments.Instruments, set => set.Instruments.Version);

        var existingChains = await db.Chains.ToListAsync(cancellationToken);
        var existingIssuers = await db.Issuers.ToListAsync(cancellationToken);
        var existingInstruments = await db.Instruments.ToListAsync(cancellationToken);
        var existingTokens = await db.Tokens.ToListAsync(cancellationToken);
        var existingCountries = await db.TokenCountries.ToListAsync(cancellationToken);

        var chainFiles = IndexUnique(chainData, "chain", result);
        var issuerFiles = IndexUnique(issuerData, "issuer", result);
        var instrumentFiles = IndexUnique(instrumentData, "instrument", result);

        ValidateChains(chainFiles, existingChains, result);
        ValidateIssuers(issuerFiles, result);
        ValidateInstruments(instrumentFiles, result);

        var fileTokenKeys = new HashSet<string>(StringComparer.Ordinal);
        var fileAssetCodes = new HashSet<string>(StringComparer.Ordinal);
        var claimedTokenIds = new HashSet<Guid>();
        foreach (var (instrument, instrumentVersion) in instrumentFiles.Values)
        {
            foreach (var token in instrument.Tokens)
            {
                ValidateToken(
                    instrument, instrumentVersion, token, chainFiles, issuerFiles, existingChains, existingIssuers,
                    existingTokens, result, fileTokenKeys, fileAssetCodes, claimedTokenIds, now);
            }
        }

        if (!result.IsValid || !apply)
        {
            return result;
        }

        ApplyChains(chainFiles, existingChains, result, now);
        ApplyIssuers(issuerFiles, existingIssuers, result, now);
        ApplyInstruments(instrumentFiles, existingInstruments, result, now);
        ApplyTokenUpserts(result, now, existingCountries);
        ApplyTokenRemovals(existingTokens, result, now);

        foreach (var warning in result.Warnings)
        {
            logger.LogWarning("Catalog import warning: {Warning}", warning);
        }

        return result;
    }

    private static List<(T Item, string Version)> Flatten<T>(
        IReadOnlyList<CatalogDataSet> dataSets,
        Func<CatalogDataSet, IEnumerable<T>> select,
        Func<CatalogDataSet, string> version)
        => dataSets.SelectMany(set => select(set).Select(item => (item, version(set)))).ToList();

    private static Dictionary<string, (T Item, string Version)> IndexUnique<T>(
        List<(T Item, string Version)> items,
        string kind,
        CatalogImportResult result)
    {
        var index = new Dictionary<string, (T, string)>();
        foreach (var (item, version) in items)
        {
            var id = item switch
            {
                ChainData chain => chain.Id,
                IssuerData issuer => issuer.Id,
                InstrumentData instrument => instrument.Id,
                _ => throw new InvalidOperationException("Unsupported catalog data type."),
            };
            if (!index.TryAdd(id, (item, version)))
            {
                result.Problems.Add($"duplicate {kind} id '{id}' in catalog data files");
            }
        }

        return index;
    }

    private static bool TryParseEnum<TEnum>(string value, Func<string, TEnum> parse, out TEnum result)
    {
        try
        {
            result = parse(value);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            result = default!;
            return false;
        }
    }

    private void ValidateChains(
        Dictionary<string, (ChainData Item, string Version)> files,
        List<Chain> existing,
        CatalogImportResult result)
    {
        foreach (var (id, (data, _)) in files)
        {
            var label = $"chain '{id}'";
            if (!chainFamilies.TryGet(data.Family, out var handler) || handler is null)
            {
                result.Problems.Add($"{label}: no chain family handler for family '{data.Family}'");
                continue;
            }

            if (!handler.IsValidChainId(id))
            {
                result.Problems.Add($"{label}: chain id is not valid for family '{data.Family}'");
            }

            if (string.IsNullOrWhiteSpace(data.Name) || data.Name.Length > 200)
            {
                result.Problems.Add($"{label}: name is required and must be at most 200 characters");
            }

            if (string.IsNullOrWhiteSpace(data.NativeAssetSymbol) || data.NativeAssetSymbol.Length > 16)
            {
                result.Problems.Add($"{label}: native asset symbol is required and must be at most 16 characters");
            }

            if (data.NativeDecimals is < 0 or > 36)
            {
                result.Problems.Add($"{label}: native decimals must be between 0 and 36");
            }

            if (string.IsNullOrWhiteSpace(data.ExplorerLinkTemplate)
                || data.ExplorerLinkTemplate.Length > 500
                || !data.ExplorerLinkTemplate.Contains("{address}", StringComparison.Ordinal))
            {
                result.Problems.Add($"{label}: explorer link template must contain the {{address}} placeholder");
            }

            if (data.RequiredConfirmations < 0)
            {
                result.Problems.Add($"{label}: required confirmations must be zero or more");
            }

            if (!TryParseEnum(data.Status, CatalogText.ChainStatusFromDb, out _))
            {
                result.Problems.Add($"{label}: status '{data.Status}' is not valid");
            }
        }
    }

    private static void ValidateIssuers(
        Dictionary<string, (IssuerData Item, string Version)> files,
        CatalogImportResult result)
    {
        foreach (var (id, (data, _)) in files)
        {
            var label = $"issuer '{id}'";
            if (!SlugPattern.IsMatch(id) || id.Length > 64)
            {
                result.Problems.Add($"{label}: id must be a lowercase slug of at most 64 characters");
            }

            if (string.IsNullOrWhiteSpace(data.LegalName) || data.LegalName.Length > 200)
            {
                result.Problems.Add($"{label}: legal name is required and must be at most 200 characters");
            }

            if (string.IsNullOrWhiteSpace(data.Jurisdiction) || data.Jurisdiction.Length > 100)
            {
                result.Problems.Add($"{label}: jurisdiction is required and must be at most 100 characters");
            }

            if (!TryParseEnum(data.Status, CatalogText.IssuerStatusFromDb, out _))
            {
                result.Problems.Add($"{label}: status '{data.Status}' is not valid");
            }

            ValidateReviewDates(label, data.ReviewedAt, data.ReviewExpiresAt, result);
            ValidateLinks(label, data.Website, data.LegalDocumentLinks, result);
        }
    }

    private static void ValidateInstruments(
        Dictionary<string, (InstrumentData Item, string Version)> files,
        CatalogImportResult result)
    {
        foreach (var (id, (data, _)) in files)
        {
            var label = $"instrument '{id}'";
            if (!SlugPattern.IsMatch(id) || id.Length > 64)
            {
                result.Problems.Add($"{label}: id must be a lowercase slug of at most 64 characters");
            }

            if (!TryParseEnum(data.Kind, CatalogText.InstrumentKindFromDb, out _))
            {
                result.Problems.Add($"{label}: kind '{data.Kind}' is not valid");
            }

            if (!TryParseEnum(data.Status, CatalogText.InstrumentStatusFromDb, out _))
            {
                result.Problems.Add($"{label}: status '{data.Status}' is not valid");
            }

            if (string.IsNullOrWhiteSpace(data.Name) || data.Name.Length > 200)
            {
                result.Problems.Add($"{label}: name is required and must be at most 200 characters");
            }

            if (string.IsNullOrWhiteSpace(data.Symbol) || data.Symbol.Length > 32)
            {
                result.Problems.Add($"{label}: symbol is required and must be at most 32 characters");
            }

            if (data.LogoLink is not null && !HttpsPattern.IsMatch(data.LogoLink))
            {
                result.Problems.Add($"{label}: logo link must be an https URL");
            }

            if (!CountryPattern.IsMatch(data.CountryOfListing))
            {
                result.Problems.Add($"{label}: country of listing must be an ISO alpha-2 code");
            }

            if (data.QuoteCurrency.Length != 3)
            {
                result.Problems.Add($"{label}: quote currency must be a three letter code");
            }
        }
    }

    private void ValidateToken(
        InstrumentData instrument,
        string instrumentVersion,
        TokenData token,
        Dictionary<string, (ChainData Item, string Version)> chainFiles,
        Dictionary<string, (IssuerData Item, string Version)> issuerFiles,
        List<Chain> existingChains,
        List<Issuer> existingIssuers,
        List<Token> existingTokens,
        CatalogImportResult result,
        HashSet<string> fileTokenKeys,
        HashSet<string> fileAssetCodes,
        HashSet<Guid> claimedTokenIds,
        DateTimeOffset now)
    {
        var label = $"token on instrument '{instrument.Id}' (chain '{token.ChainId}', address '{token.Address}')";

        var issuerEntry = issuerFiles.TryGetValue(token.IssuerId, out var issuerFile) ? issuerFile.Item : null;
        var existingIssuer = existingIssuers.FirstOrDefault(issuer => issuer.Id == token.IssuerId);
        if (issuerEntry is null && existingIssuer is null)
        {
            result.Problems.Add($"{label}: issuer '{token.IssuerId}' does not exist");
        }

        var chainEntry = chainFiles.TryGetValue(token.ChainId, out var chainFile) ? chainFile.Item : null;
        var existingChain = existingChains.FirstOrDefault(chain => chain.Id == token.ChainId);
        if (chainEntry is null && existingChain is null)
        {
            result.Problems.Add($"{label}: chain '{token.ChainId}' does not exist");
        }

        var family = chainEntry?.Family ?? existingChain?.Family;
        var handler = family is not null && chainFamilies.TryGet(family, out var resolved) ? resolved : null;
        if ((chainEntry is not null || existingChain is not null) && handler is null)
        {
            result.Problems.Add($"{label}: chain family '{family}' has no handler");
        }
        else if (handler is not null && !handler.IsValidChainId(token.ChainId))
        {
            result.Problems.Add($"{label}: chain id is not valid for family '{family}'");
        }

        string? canonicalAddress = null;
        string? caip19 = null;
        if (handler is not null)
        {
            var canonical = handler.CanonicalizeAddress(token.ChainId, token.Address);
            if (canonical.IsFailure)
            {
                result.Problems.Add($"{label}: {canonical.Error!.Message}");
            }
            else
            {
                canonicalAddress = canonical.Value;
                caip19 = handler.BuildAssetId(token.ChainId, token.Standard, canonicalAddress);
            }
        }

        if (caip19 is not null && !fileTokenKeys.Add(caip19))
        {
            result.Problems.Add($"{label}: duplicate CAIP-19 asset id in catalog data files");
        }

        var assetCode = token.LedgerAssetCode.Trim().ToUpperInvariant();
        if (!AssetCodePattern.IsMatch(assetCode))
        {
            result.Problems.Add($"{label}: ledger asset code '{token.LedgerAssetCode}' is not valid");
        }

        if (assetCode.Length > 0 && !fileAssetCodes.Add(assetCode))
        {
            result.Problems.Add($"{label}: duplicate ledger asset code '{assetCode}' in catalog data files");
        }

        if (token.Decimals is < 0 or > 18)
        {
            result.Problems.Add($"{label}: decimals must be between 0 and 18");
        }

        if (!TryParseEnum(token.Status, CatalogText.TokenStatusFromDb, out var status))
        {
            result.Problems.Add($"{label}: status '{token.Status}' is not valid");
            status = TokenStatus.Draft;
        }

        var disclosuresComplete = ValidateDisclosures(label, token, result);
        var allowedCountries = ValidateCountries(label, token, result);
        ValidateSchedule(label, token.Schedule, result);
        ValidateReviewDates(label, token.ReviewedAt, token.ReviewExpiresAt, result);

        if (!TryParseEnum(token.ClaimType, CatalogText.ClaimTypeFromDb, out var claimType))
        {
            result.Problems.Add($"{label}: claim type '{token.ClaimType}' is not valid");
            claimType = ClaimType.Other;
        }

        if (!TryParseEnum(token.DividendPolicy, CatalogText.DividendPolicyFromDb, out var dividendPolicy))
        {
            result.Problems.Add($"{label}: dividend policy '{token.DividendPolicy}' is not valid");
            dividendPolicy = DividendPolicy.Other;
        }

        if (!TryParseEnum(token.Schedule.Kind, CatalogText.TradingScheduleKindFromDb, out var scheduleKind))
        {
            result.Problems.Add($"{label}: trading schedule kind '{token.Schedule.Kind}' is not valid");
            scheduleKind = TradingScheduleKind.Always;
        }

        var existing = MatchExistingToken(existingTokens, caip19, assetCode, claimedTokenIds, label, result);
        var wasEverListed = token.EverListed || existing?.EverListed == true;

        if (existing is not null)
        {
            ValidateTransition(label, status, existing, result);
            ValidateImmutability(label, instrument.Id, token, canonicalAddress, assetCode, existing, result);
        }
        else if ((status is TokenStatus.Suspended or TokenStatus.Delisted) && !token.EverListed)
        {
            result.Problems.Add($"{label}: a new token cannot start as '{token.Status}'");
        }

        var enteringListed = status == TokenStatus.Listed && existing?.Status != TokenStatus.Listed;
        if (enteringListed && !wasEverListed)
        {
            EvaluateListingGuard(
                label, token, instrument, issuerEntry, existingIssuer, chainEntry, existingChain,
                handler, disclosuresComplete, allowedCountries, result, now);
        }

        if (status == TokenStatus.Listed)
        {
            EnforceListedRequirements(label, token, disclosuresComplete, allowedCountries, result);
        }

        foreach (var country in allowedCountries)
        {
            if (!compliance.IsRealMoneyCountry(country))
            {
                result.Warnings.Add($"{label}: allowed country '{country}' is not on the real money country list");
            }
        }

        _tokenPlans.Add(new TokenPlan
        {
            InstrumentId = instrument.Id,
            Version = instrumentVersion,
            Data = token,
            Status = status,
            ScheduleKind = scheduleKind,
            ClaimType = claimType,
            DividendPolicy = dividendPolicy,
            CanonicalAddress = canonicalAddress ?? string.Empty,
            Caip19 = caip19 ?? string.Empty,
            AssetCode = assetCode,
            Countries = allowedCountries,
            Existing = existing,
        });
    }

    private static Token? MatchExistingToken(
        List<Token> existingTokens,
        string? caip19,
        string assetCode,
        HashSet<Guid> claimedTokenIds,
        string label,
        CatalogImportResult result)
    {
        var byAsset = existingTokens.FirstOrDefault(t => t.LedgerAssetCode == assetCode);
        var byCaip = caip19 is null ? null : existingTokens.FirstOrDefault(t => t.Caip19 == caip19);

        if (byCaip is not null && byAsset is not null && byCaip.Id != byAsset.Id)
        {
            result.Problems.Add($"{label}: matches two different stored tokens");
            return null;
        }

        var match = byCaip ?? byAsset;
        if (match is not null && !claimedTokenIds.Add(match.Id))
        {
            result.Problems.Add($"{label}: two file tokens resolve to stored token '{match.Id}'");
            return null;
        }

        return match;
    }

    private static void ValidateTransition(
        string label,
        TokenStatus desired,
        Token existing,
        CatalogImportResult result)
    {
        if (existing.Status == TokenStatus.Delisted && desired != TokenStatus.Delisted)
        {
            result.Problems.Add($"{label}: token '{existing.Id}' is delisted and cannot change status");
            return;
        }

        var legal = existing.Status == desired
            || (existing.Status, desired) switch
            {
                (TokenStatus.Draft, TokenStatus.Listed) => true,
                (TokenStatus.Listed, TokenStatus.Suspended) => true,
                (TokenStatus.Suspended, TokenStatus.Listed) => true,
                (TokenStatus.Listed, TokenStatus.Delisted) => true,
                (TokenStatus.Suspended, TokenStatus.Delisted) => true,
                _ => false,
            };
        if (!legal)
        {
            result.Problems.Add(
                $"{label}: status transition '{CatalogText.ToDb(existing.Status)}' to '{CatalogText.ToDb(desired)}' is not allowed");
        }
    }

    private static void ValidateImmutability(
        string label,
        string instrumentId,
        TokenData token,
        string? canonicalAddress,
        string assetCode,
        Token existing,
        CatalogImportResult result)
    {
        if (!existing.EverListed)
        {
            return;
        }

        var immutable = new (string Name, string File, string Stored)[]
        {
            ("instrument", instrumentId, existing.InstrumentId),
            ("issuer", token.IssuerId, existing.IssuerId),
            ("chain", token.ChainId, existing.ChainId),
            ("contract address", canonicalAddress ?? string.Empty, existing.CanonicalAddress),
            ("decimals", token.Decimals.ToString(System.Globalization.CultureInfo.InvariantCulture),
                existing.Decimals.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("ledger asset code", assetCode, existing.LedgerAssetCode),
        };
        foreach (var (name, fileValue, storedValue) in immutable)
        {
            if (!string.Equals(fileValue, storedValue, StringComparison.Ordinal))
            {
                result.Problems.Add($"{label}: {name} cannot change once the token has been listed");
            }
        }
    }

    private void EvaluateListingGuard(
        string label,
        TokenData token,
        InstrumentData instrument,
        IssuerData? issuerEntry,
        Issuer? existingIssuer,
        ChainData? chainEntry,
        Chain? existingChain,
        IChainFamilyHandler? handler,
        bool disclosuresComplete,
        List<string> allowedCountries,
        CatalogImportResult result,
        DateTimeOffset now)
    {
        var issuerApproved = issuerEntry is not null
            ? TryParseEnum(issuerEntry.Status, CatalogText.IssuerStatusFromDb, out var issuerStatus)
                && issuerStatus == IssuerStatus.Approved
            : existingIssuer?.Status == IssuerStatus.Approved;

        var chainEnabled = chainEntry is not null
            ? TryParseEnum(chainEntry.Status, CatalogText.ChainStatusFromDb, out var chainStatus)
                && chainStatus == ChainStatus.Enabled
            : existingChain?.Status == ChainStatus.Enabled;

        var instrumentActive = TryParseEnum(instrument.Status, CatalogText.InstrumentStatusFromDb, out var instrumentStatus)
            && instrumentStatus == InstrumentStatus.Active;

        var input = new ListingGuardInput(
            IssuerApproved: issuerApproved,
            ChainEnabled: chainEnabled,
            ChainFamilySupported: handler is not null,
            DisclosuresComplete: disclosuresComplete,
            AllowedCountryCount: allowedCountries.Count,
            HasGatewayKey: !string.IsNullOrWhiteSpace(token.GatewayKey),
            Decimals: token.Decimals,
            ReviewedAt: token.ReviewedAt,
            ReviewExpiresAt: token.ReviewExpiresAt,
            Now: now,
            ReviewMaxMonths: options.Value.ReviewMaxMonths,
            InstrumentActive: instrumentActive);

        foreach (var failure in ListingGuard.Evaluate(input))
        {
            result.Problems.Add($"{label}: cannot be listed: {failure}");
        }
    }

    private static void EnforceListedRequirements(
        string label,
        TokenData token,
        bool disclosuresComplete,
        List<string> allowedCountries,
        CatalogImportResult result)
    {
        if (!disclosuresComplete)
        {
            result.Problems.Add($"{label}: listed token has incomplete disclosures");
        }

        if (allowedCountries.Count == 0)
        {
            result.Problems.Add($"{label}: listed token must allow at least one country");
        }

        if (string.IsNullOrWhiteSpace(token.GatewayKey) || token.GatewayKey.Length > 100)
        {
            result.Problems.Add($"{label}: listed token requires a gateway key of at most 100 characters");
        }
    }

    private static bool ValidateDisclosures(string label, TokenData token, CatalogImportResult result)
    {
        var complete = true;
        void Require(string name, string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            {
                result.Problems.Add($"{label}: disclosure '{name}' is required and must be at most {maxLength} characters");
                complete = false;
            }
        }

        Require("claimDescription", token.ClaimDescription, 2000);
        Require("backingDescription", token.BackingDescription, 2000);
        Require("custodianDescription", token.CustodianDescription, 2000);
        Require("redemptionDescription", token.RedemptionDescription, 2000);
        Require("transferRestrictions", token.TransferRestrictions, 2000);
        Require("dividendNote", token.DividendNote, 1000);
        Require("votingNote", token.VotingNote, 1000);

        if (token.NoIssuerFees)
        {
            if (token.IssuerFees.Count > 0)
            {
                result.Problems.Add($"{label}: issuer fees must be empty when noIssuerFees is true");
                complete = false;
            }
        }
        else if (token.IssuerFees.Count == 0)
        {
            result.Problems.Add($"{label}: issuer fees are required unless noIssuerFees is true");
            complete = false;
        }

        if (token.RiskStatements.Count < 3)
        {
            result.Problems.Add($"{label}: at least three risk statements are required");
            complete = false;
        }

        if (string.IsNullOrWhiteSpace(token.DisclosureDocumentLink)
            || token.DisclosureDocumentLink.Length > 500
            || !HttpsPattern.IsMatch(token.DisclosureDocumentLink))
        {
            result.Problems.Add($"{label}: disclosure document link must be an https URL");
            complete = false;
        }

        return complete;
    }

    private static List<string> ValidateCountries(string label, TokenData token, CatalogImportResult result)
    {
        var countries = new List<string>();
        foreach (var raw in token.AllowedCountries)
        {
            var code = raw.Trim().ToUpperInvariant();
            if (!CountryPattern.IsMatch(code))
            {
                result.Problems.Add($"{label}: allowed country '{raw}' is not an ISO alpha-2 code");
                continue;
            }

            if (!countries.Contains(code, StringComparer.Ordinal))
            {
                countries.Add(code);
            }
        }

        return countries;
    }

    private static void ValidateSchedule(string label, TradingScheduleData schedule, CatalogImportResult result)
    {
        if (!TryParseEnum(schedule.Kind, CatalogText.TradingScheduleKindFromDb, out var kind))
        {
            result.Problems.Add($"{label}: trading schedule kind '{schedule.Kind}' is not valid");
            return;
        }

        if (kind != TradingScheduleKind.Always)
        {
            if (string.IsNullOrWhiteSpace(schedule.TimeZone)
                || !TimeZoneInfo.TryFindSystemTimeZoneById(schedule.TimeZone, out _))
            {
                result.Problems.Add($"{label}: trading schedule time zone must be a valid IANA name");
            }
        }

        if (schedule.Note is not null && schedule.Note.Length > 500)
        {
            result.Problems.Add($"{label}: trading schedule note must be at most 500 characters");
        }
    }

    private static void ValidateReviewDates(
        string label,
        DateTimeOffset? reviewedAt,
        DateTimeOffset? reviewExpiresAt,
        CatalogImportResult result)
    {
        if (reviewedAt is not null && reviewExpiresAt is not null && reviewExpiresAt <= reviewedAt)
        {
            result.Problems.Add($"{label}: review expiry must be after the review date");
        }
    }

    private static void ValidateLinks(
        string label,
        string? website,
        List<string> documentLinks,
        CatalogImportResult result)
    {
        if (website is not null && (website.Length > 500 || !HttpsPattern.IsMatch(website)))
        {
            result.Problems.Add($"{label}: website must be an https URL");
        }

        foreach (var link in documentLinks)
        {
            if (link.Length > 500 || !HttpsPattern.IsMatch(link))
            {
                result.Problems.Add($"{label}: legal document link must be an https URL");
            }
        }
    }
}
