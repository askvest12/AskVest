using AskVest.Modules.Catalog.Contracts;
using AskVest.Modules.Catalog.Domain;
using AskVest.Modules.Catalog.Domain.Enums;
using AskVest.Modules.Catalog.Import.Data;
using AskVest.Modules.Catalog.Persistence.Entities;
using AskVest.Platform;

namespace AskVest.Modules.Catalog.Import;

internal sealed partial class CatalogImportValidator
{
    private void ApplyChains(
        Dictionary<string, (ChainData Item, string Version)> files,
        List<Chain> existing,
        CatalogImportResult result,
        DateTimeOffset now)
    {
        foreach (var (id, (data, version)) in files)
        {
            var status = CatalogText.ChainStatusFromDb(data.Status);
            var chain = existing.FirstOrDefault(c => c.Id == id);
            if (chain is null)
            {
                db.Chains.Add(new Chain
                {
                    Id = id,
                    Family = data.Family,
                    Name = data.Name,
                    NativeAssetSymbol = data.NativeAssetSymbol,
                    NativeDecimals = data.NativeDecimals,
                    ExplorerLinkTemplate = data.ExplorerLinkTemplate,
                    RequiredConfirmations = data.RequiredConfirmations,
                    Status = status,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                result.Events.Add(Event(CatalogEventType.Imported, "chain", id, [], version, now));
                result.ChainsUpserted++;
                continue;
            }

            var changed = new List<string>();
            SetIfChanged(changed, "family", data.Family, chain.Family, v => chain.Family = v);
            SetIfChanged(changed, "name", data.Name, chain.Name, v => chain.Name = v);
            SetIfChanged(changed, "nativeAssetSymbol", data.NativeAssetSymbol, chain.NativeAssetSymbol, v => chain.NativeAssetSymbol = v);
            SetIfChanged(changed, "nativeDecimals", data.NativeDecimals, chain.NativeDecimals, v => chain.NativeDecimals = v);
            SetIfChanged(changed, "explorerLinkTemplate", data.ExplorerLinkTemplate, chain.ExplorerLinkTemplate, v => chain.ExplorerLinkTemplate = v);
            SetIfChanged(changed, "requiredConfirmations", data.RequiredConfirmations, chain.RequiredConfirmations, v => chain.RequiredConfirmations = v);
            SetIfChanged(changed, "status", status, chain.Status, v => chain.Status = v);
            if (changed.Count > 0)
            {
                chain.UpdatedAt = now;
                result.Events.Add(Event(CatalogEventType.Updated, "chain", id, changed, version, now));
            }

            result.ChainsUpserted++;
        }

        foreach (var chain in existing.Where(c => !files.ContainsKey(c.Id) && c.Status == ChainStatus.Enabled))
        {
            chain.Status = ChainStatus.Disabled;
            chain.UpdatedAt = now;
            result.Events.Add(Event(CatalogEventType.Updated, "chain", chain.Id, ["status"], null, now));
        }
    }

    private void ApplyIssuers(
        Dictionary<string, (IssuerData Item, string Version)> files,
        List<Issuer> existing,
        CatalogImportResult result,
        DateTimeOffset now)
    {
        foreach (var (id, (data, version)) in files)
        {
            var status = CatalogText.IssuerStatusFromDb(data.Status);
            var issuer = existing.FirstOrDefault(i => i.Id == id);
            if (issuer is null)
            {
                db.Issuers.Add(new Issuer
                {
                    Id = id,
                    LegalName = data.LegalName,
                    Jurisdiction = data.Jurisdiction,
                    Website = data.Website,
                    RegulatoryStatus = data.RegulatoryStatus,
                    LegalDocumentLinks = data.LegalDocumentLinks.ToList(),
                    Status = status,
                    ReviewedAt = data.ReviewedAt,
                    ReviewExpiresAt = data.ReviewExpiresAt,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                result.Events.Add(Event(CatalogEventType.Imported, "issuer", id, [], version, now));
                result.IssuersUpserted++;
                continue;
            }

            var changed = new List<string>();
            SetIfChanged(changed, "legalName", data.LegalName, issuer.LegalName, v => issuer.LegalName = v);
            SetIfChanged(changed, "jurisdiction", data.Jurisdiction, issuer.Jurisdiction, v => issuer.Jurisdiction = v);
            SetIfChanged(changed, "website", data.Website, issuer.Website, v => issuer.Website = v);
            SetIfChanged(changed, "regulatoryStatus", data.RegulatoryStatus, issuer.RegulatoryStatus, v => issuer.RegulatoryStatus = v);
            SetIfChanged(changed, "legalDocumentLinks", string.Join("|", data.LegalDocumentLinks), string.Join("|", issuer.LegalDocumentLinks), v => issuer.LegalDocumentLinks = data.LegalDocumentLinks.ToList());
            SetIfChanged(changed, "status", status, issuer.Status, v => issuer.Status = v);
            SetIfChanged(changed, "reviewedAt", data.ReviewedAt, issuer.ReviewedAt, v => issuer.ReviewedAt = v);
            SetIfChanged(changed, "reviewExpiresAt", data.ReviewExpiresAt, issuer.ReviewExpiresAt, v => issuer.ReviewExpiresAt = v);
            if (changed.Count > 0)
            {
                issuer.UpdatedAt = now;
                result.Events.Add(Event(CatalogEventType.Updated, "issuer", id, changed, version, now));
            }

            result.IssuersUpserted++;
        }

        foreach (var issuer in existing.Where(i => !files.ContainsKey(i.Id) && i.Status == IssuerStatus.Approved))
        {
            issuer.Status = IssuerStatus.Suspended;
            issuer.UpdatedAt = now;
            result.Events.Add(Event(CatalogEventType.Updated, "issuer", issuer.Id, ["status"], null, now));
        }
    }

    private void ApplyInstruments(
        Dictionary<string, (InstrumentData Item, string Version)> files,
        List<Instrument> existing,
        CatalogImportResult result,
        DateTimeOffset now)
    {
        foreach (var (id, (data, version)) in files)
        {
            var kind = CatalogText.InstrumentKindFromDb(data.Kind);
            var status = CatalogText.InstrumentStatusFromDb(data.Status);
            var instrument = existing.FirstOrDefault(i => i.Id == id);
            if (instrument is null)
            {
                db.Instruments.Add(new Instrument
                {
                    Id = id,
                    Kind = kind,
                    Name = data.Name,
                    Symbol = data.Symbol,
                    Isin = data.Isin,
                    ListingExchange = data.ListingExchange,
                    CountryOfListing = data.CountryOfListing,
                    QuoteCurrency = data.QuoteCurrency,
                    Sector = data.Sector,
                    ShortDescription = data.ShortDescription,
                    LogoLink = data.LogoLink,
                    Status = status,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                result.Events.Add(Event(CatalogEventType.Imported, "instrument", id, [], version, now));
                result.InstrumentsUpserted++;
                continue;
            }

            var changed = new List<string>();
            SetIfChanged(changed, "kind", kind, instrument.Kind, v => instrument.Kind = v);
            SetIfChanged(changed, "name", data.Name, instrument.Name, v => instrument.Name = v);
            SetIfChanged(changed, "symbol", data.Symbol, instrument.Symbol, v => instrument.Symbol = v);
            SetIfChanged(changed, "isin", data.Isin, instrument.Isin, v => instrument.Isin = v);
            SetIfChanged(changed, "listingExchange", data.ListingExchange, instrument.ListingExchange, v => instrument.ListingExchange = v);
            SetIfChanged(changed, "countryOfListing", data.CountryOfListing, instrument.CountryOfListing, v => instrument.CountryOfListing = v);
            SetIfChanged(changed, "quoteCurrency", data.QuoteCurrency, instrument.QuoteCurrency, v => instrument.QuoteCurrency = v);
            SetIfChanged(changed, "sector", data.Sector, instrument.Sector, v => instrument.Sector = v);
            SetIfChanged(changed, "shortDescription", data.ShortDescription, instrument.ShortDescription, v => instrument.ShortDescription = v);
            SetIfChanged(changed, "logoLink", data.LogoLink, instrument.LogoLink, v => instrument.LogoLink = v);
            SetIfChanged(changed, "status", status, instrument.Status, v => instrument.Status = v);
            if (changed.Count > 0)
            {
                instrument.UpdatedAt = now;
                result.Events.Add(Event(CatalogEventType.Updated, "instrument", id, changed, version, now));
            }

            result.InstrumentsUpserted++;
        }

        foreach (var instrument in existing.Where(i => !files.ContainsKey(i.Id) && i.Status == InstrumentStatus.Active))
        {
            instrument.Status = InstrumentStatus.Delisted;
            instrument.UpdatedAt = now;
            result.Events.Add(Event(CatalogEventType.Delisted, "instrument", instrument.Id, ["status"], null, now));
        }
    }

    private void ApplyTokenUpserts(
        CatalogImportResult result,
        DateTimeOffset now,
        List<TokenCountry> existingCountries)
    {
        foreach (var plan in _tokenPlans)
        {
            var data = plan.Data;
            if (plan.Existing is null)
            {
                var token = new Token
                {
                    Id = Guid.CreateVersion7(),
                    InstrumentId = plan.InstrumentId,
                    IssuerId = data.IssuerId,
                    ChainId = data.ChainId,
                    Standard = data.Standard,
                    CanonicalAddress = plan.CanonicalAddress,
                    Caip19 = plan.Caip19,
                    Symbol = data.Symbol,
                    Decimals = data.Decimals,
                    LedgerAssetCode = plan.AssetCode,
                    Status = plan.Status,
                    GatewayKey = data.GatewayKey ?? string.Empty,
                    ScheduleKind = plan.ScheduleKind,
                    ScheduleTimeZone = data.Schedule.TimeZone,
                    ScheduleNote = data.Schedule.Note,
                    ClaimType = plan.ClaimType,
                    ClaimDescription = data.ClaimDescription,
                    BackingDescription = data.BackingDescription,
                    CustodianDescription = data.CustodianDescription,
                    RedemptionDescription = data.RedemptionDescription,
                    DividendPolicy = plan.DividendPolicy,
                    DividendNote = data.DividendNote,
                    VotingRights = data.VotingRights,
                    VotingNote = data.VotingNote,
                    TransferRestrictions = data.TransferRestrictions,
                    IssuerFees = data.IssuerFees.ToList(),
                    NoIssuerFees = data.NoIssuerFees,
                    RiskStatements = data.RiskStatements.ToList(),
                    DisclosureDocumentLink = data.DisclosureDocumentLink,
                    ReviewedAt = data.ReviewedAt,
                    ReviewExpiresAt = data.ReviewExpiresAt,
                    ReviewerLabel = data.ReviewerLabel,
                    EverListed = data.EverListed || plan.Status == TokenStatus.Listed,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.Tokens.Add(token);
                AddCountries(token.Id, plan.Countries, existingCountries);
                var eventType = plan.Status == TokenStatus.Listed ? CatalogEventType.Listed : CatalogEventType.Imported;
                result.Events.Add(Event(eventType, "token", token.Id.ToString(), [], plan.Version, now));
                if (plan.Status == TokenStatus.Listed)
                {
                    result.OutboxMessages.Add(StatusMessage(token.Id, plan.Status));
                }

                result.AssetsToRegister.Add((plan.AssetCode, data.Decimals));
                result.TokensUpserted++;
                continue;
            }

            var existing = plan.Existing;
            var previousStatus = existing.Status;
            var changed = new List<string>();
            SetIfChanged(changed, "issuerId", data.IssuerId, existing.IssuerId, v => existing.IssuerId = v);
            SetIfChanged(changed, "chainId", data.ChainId, existing.ChainId, v => existing.ChainId = v);
            SetIfChanged(changed, "standard", data.Standard, existing.Standard, v => existing.Standard = v);
            SetIfChanged(changed, "canonicalAddress", plan.CanonicalAddress, existing.CanonicalAddress, v => existing.CanonicalAddress = v);
            SetIfChanged(changed, "caip19", plan.Caip19, existing.Caip19, v => existing.Caip19 = v);
            SetIfChanged(changed, "symbol", data.Symbol, existing.Symbol, v => existing.Symbol = v);
            SetIfChanged(changed, "decimals", data.Decimals, existing.Decimals, v => existing.Decimals = v);
            SetIfChanged(changed, "ledgerAssetCode", plan.AssetCode, existing.LedgerAssetCode, v => existing.LedgerAssetCode = v);
            SetIfChanged(changed, "gatewayKey", data.GatewayKey ?? string.Empty, existing.GatewayKey, v => existing.GatewayKey = v);
            SetIfChanged(changed, "scheduleKind", plan.ScheduleKind, existing.ScheduleKind, v => existing.ScheduleKind = v);
            SetIfChanged(changed, "scheduleTimeZone", data.Schedule.TimeZone, existing.ScheduleTimeZone, v => existing.ScheduleTimeZone = v);
            SetIfChanged(changed, "scheduleNote", data.Schedule.Note, existing.ScheduleNote, v => existing.ScheduleNote = v);
            SetIfChanged(changed, "claimType", plan.ClaimType, existing.ClaimType, v => existing.ClaimType = v);
            SetIfChanged(changed, "claimDescription", data.ClaimDescription, existing.ClaimDescription, v => existing.ClaimDescription = v);
            SetIfChanged(changed, "backingDescription", data.BackingDescription, existing.BackingDescription, v => existing.BackingDescription = v);
            SetIfChanged(changed, "custodianDescription", data.CustodianDescription, existing.CustodianDescription, v => existing.CustodianDescription = v);
            SetIfChanged(changed, "redemptionDescription", data.RedemptionDescription, existing.RedemptionDescription, v => existing.RedemptionDescription = v);
            SetIfChanged(changed, "dividendPolicy", plan.DividendPolicy, existing.DividendPolicy, v => existing.DividendPolicy = v);
            SetIfChanged(changed, "dividendNote", data.DividendNote, existing.DividendNote, v => existing.DividendNote = v);
            SetIfChanged(changed, "votingRights", data.VotingRights, existing.VotingRights, v => existing.VotingRights = v);
            SetIfChanged(changed, "votingNote", data.VotingNote, existing.VotingNote, v => existing.VotingNote = v);
            SetIfChanged(changed, "transferRestrictions", data.TransferRestrictions, existing.TransferRestrictions, v => existing.TransferRestrictions = v);
            SetIfChanged(changed, "issuerFees", string.Join("|", data.IssuerFees), string.Join("|", existing.IssuerFees), v => existing.IssuerFees = data.IssuerFees.ToList());
            SetIfChanged(changed, "noIssuerFees", data.NoIssuerFees, existing.NoIssuerFees, v => existing.NoIssuerFees = v);
            SetIfChanged(changed, "riskStatements", string.Join("|", data.RiskStatements), string.Join("|", existing.RiskStatements), v => existing.RiskStatements = data.RiskStatements.ToList());
            SetIfChanged(changed, "disclosureDocumentLink", data.DisclosureDocumentLink, existing.DisclosureDocumentLink, v => existing.DisclosureDocumentLink = v);
            SetIfChanged(changed, "reviewedAt", data.ReviewedAt, existing.ReviewedAt, v => existing.ReviewedAt = v);
            SetIfChanged(changed, "reviewExpiresAt", data.ReviewExpiresAt, existing.ReviewExpiresAt, v => existing.ReviewExpiresAt = v);
            SetIfChanged(changed, "reviewerLabel", data.ReviewerLabel, existing.ReviewerLabel, v => existing.ReviewerLabel = v);
            SetIfChanged(changed, "everListed", data.EverListed || plan.Status == TokenStatus.Listed, existing.EverListed, v => existing.EverListed = v);

            existing.InstrumentId = plan.InstrumentId;
            existing.Status = plan.Status;
            SyncCountries(existing.Id, plan.Countries, existingCountries);

            if (plan.Status != previousStatus)
            {
                var eventType = plan.Status switch
                {
                    TokenStatus.Listed when previousStatus == TokenStatus.Suspended => CatalogEventType.Relisted,
                    TokenStatus.Listed => CatalogEventType.Listed,
                    TokenStatus.Suspended => CatalogEventType.Suspended,
                    TokenStatus.Delisted => CatalogEventType.Delisted,
                    _ => CatalogEventType.Updated,
                };
                result.Events.Add(Event(eventType, "token", existing.Id.ToString(), ["status"], plan.Version, now));
                result.OutboxMessages.Add(StatusMessage(existing.Id, plan.Status));
            }
            else if (changed.Count > 0)
            {
                result.Events.Add(Event(CatalogEventType.Updated, "token", existing.Id.ToString(), changed, plan.Version, now));
            }

            result.AssetsToRegister.Add((plan.AssetCode, data.Decimals));
            result.TokensUpserted++;
        }
    }

    private void ApplyTokenRemovals(List<Token> existingTokens, CatalogImportResult result, DateTimeOffset now)
    {
        var keptIds = _tokenPlans.Where(plan => plan.Existing is not null)
            .Select(plan => plan.Existing!.Id)
            .ToHashSet();
        foreach (var token in existingTokens.Where(t => !keptIds.Contains(t.Id) && t.Status != TokenStatus.Delisted))
        {
            token.Status = TokenStatus.Delisted;
            token.UpdatedAt = now;
            token.EverListed = true;
            result.Events.Add(Event(CatalogEventType.Delisted, "token", token.Id.ToString(), ["status"], null, now));
            result.OutboxMessages.Add(StatusMessage(token.Id, token.Status));
            result.TokensDelisted++;
        }
    }

    private void AddCountries(Guid tokenId, List<string> countries, List<TokenCountry> existing)
    {
        foreach (var code in countries)
        {
            if (!existing.Any(c => c.TokenId == tokenId && c.CountryCode == code))
            {
                var row = new TokenCountry { TokenId = tokenId, CountryCode = code };
                db.TokenCountries.Add(row);
                existing.Add(row);
            }
        }
    }

    private void SyncCountries(Guid tokenId, List<string> countries, List<TokenCountry> existing)
    {
        var stale = existing.Where(c => c.TokenId == tokenId && !countries.Contains(c.CountryCode, StringComparer.Ordinal)).ToList();
        foreach (var row in stale)
        {
            db.TokenCountries.Remove(row);
            existing.Remove(row);
        }

        AddCountries(tokenId, countries, existing);
    }

    private static (string MessageType, object Payload, string? DedupeKey) StatusMessage(Guid tokenId, TokenStatus status)
        => (CatalogTokenStatusChangedMessage.MessageType,
            new CatalogTokenStatusChangedMessage(tokenId, CatalogText.ToDb(status)),
            $"{tokenId}:{CatalogText.ToDb(status)}");

    private static void SetIfChanged<T>(
        List<string> changed,
        string name,
        T fileValue,
        T storedValue,
        Action<T> apply)
    {
        if (!EqualityComparer<T>.Default.Equals(fileValue, storedValue))
        {
            apply(fileValue);
            changed.Add(name);
        }
    }

    private static CatalogEvent Event(
        CatalogEventType type,
        string subjectType,
        string subjectId,
        List<string> changedFields,
        string? dataVersion,
        DateTimeOffset now)
        => new()
        {
            Id = Guid.CreateVersion7(),
            EventType = type,
            SubjectType = subjectType,
            SubjectId = subjectId,
            OccurredAt = now,
            ChangedFields = changedFields,
            DataVersion = dataVersion,
        };
}
