using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChargeGlobe.Application.Identity;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Application.Tariffs;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Infrastructure.Identity;
using ChargeGlobe.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Persistence;

/// <summary>
/// Providers, networks and tariff versions in <c>emp</c>, <c>charging_network</c> and <c>tariff_plan</c>. An import is
/// one transaction per source: validate everything, then upsert networks and providers, open new tariff versions and end
/// the replaced or vanished ones.
/// </summary>
public sealed partial class TariffCatalog(AppDbContext db, AuditLog audit, IAppConfig config, TimeProvider timeProvider) : ITariffCatalog
{
    /// <summary>Highest amount of a price component; protects the comparison from typos such as 45 instead of 0.45.</summary>
    public const decimal MaxAmount = 1000;

    /// <summary>Longest price hint of a network.</summary>
    public const int MaxNoteLength = 300;

    /// <summary>Range of the markup on the operator's price: discounts up to half, commissions up to the double.</summary>
    public const decimal MinMarkupPercent = -50;

    public const decimal MaxMarkupPercent = 100;

    public async Task<IReadOnlyList<EmpView>> EmpsAsync(CancellationToken cancellationToken) =>
        await db.Emps.AsNoTracking()
            .Where(emp => db.TariffPlans.Any(plan => plan.EmpId == emp.Id && plan.ValidTo == null))
            .OrderBy(emp => emp.Name)
            .ThenBy(emp => emp.Key)
            .Select(emp => new EmpView(emp.Key, emp.Name, emp.Website, emp.Country, db.TariffPlans.Count(plan => plan.EmpId == emp.Id && plan.ValidTo == null)))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyCollection<string>> EmpKeysAsync(CancellationToken cancellationToken) =>
        await db.Emps.AsNoTracking().Select(emp => emp.Key).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<NetworkView>> NetworksAsync(CancellationToken cancellationToken) =>
        (await db.ChargingNetworks.AsNoTracking().OrderBy(network => network.Key).ToListAsync(cancellationToken))
        .Select(network => new NetworkView(network.Key, network.Name, network.Country, network.Operators, network.PriceNote, network.PriceUrl, network.Sites.Count))
        .ToList();

    public async Task<IReadOnlyList<TariffView>> TariffsAsync(string? emp, bool history, CancellationToken cancellationToken)
    {
        var rows = await db.TariffPlans.AsNoTracking()
            .Where(plan => history || plan.ValidTo == null)
            .Join(db.Emps, plan => plan.EmpId, item => item.Id, (plan, item) => new { Plan = plan, item.Key, item.Name })
            .Where(row => emp == null || row.Key == emp)
            .ToListAsync(cancellationToken);
        var cutoff = StaleBefore();
        return [.. rows
            .OrderBy(row => row.Key, StringComparer.Ordinal)
            .ThenBy(row => row.Plan.Key, StringComparer.Ordinal)
            .ThenByDescending(row => row.Plan.ValidFrom)
            .Select(row => View(row.Plan, row.Key, row.Name, cutoff))];
    }

    public Task<int> StaleCountAsync(CancellationToken cancellationToken)
    {
        var cutoff = StaleBefore();
        return db.TariffPlans.CountAsync(
            plan => plan.ValidTo == null && !((plan.SourceDate != null && plan.SourceDate >= cutoff) || (plan.CheckedOn != null && plan.CheckedOn >= cutoff)),
            cancellationToken);
    }

    public Task<int> CheckSoonCountAsync(CancellationToken cancellationToken)
    {
        var (stale, soon) = (StaleBefore(), CheckSoonBefore());
        return db.TariffPlans.CountAsync(
            plan => plan.ValidTo == null
                && ((plan.SourceDate != null && plan.SourceDate >= stale) || (plan.CheckedOn != null && plan.CheckedOn >= stale))
                && !((plan.SourceDate != null && plan.SourceDate >= soon) || (plan.CheckedOn != null && plan.CheckedOn >= soon)),
            cancellationToken);
    }

    private DateOnly Today() => DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

    /// <summary>Tariffs whose "Stand" and last check both lie before this day (or are unknown) are stale.</summary>
    private DateOnly StaleBefore() => Today().AddMonths(-config.Get<int>(SettingsCatalog.TariffStaleMonths));

    /// <summary>Half the span of <see cref="StaleBefore"/>: tariffs last seen before this day are to be checked soon.</summary>
    private DateOnly CheckSoonBefore()
    {
        var today = Today();
        return today.AddDays(-(today.DayNumber - StaleBefore().DayNumber) / 2);
    }

    public async Task<CatalogImportResult> ImportAsync(string sourceKey, TariffCatalogSnapshot snapshot, Actor? actor, CancellationToken cancellationToken)
    {
        var known = await db.ChargingNetworks.AsNoTracking()
            .Where(network => network.SourceKey != sourceKey)
            .Select(network => network.Key)
            .ToListAsync(cancellationToken);
        var issues = Validate(snapshot, known);
        if (issues.Count > 0)
        {
            return new CatalogImportResult(0, 0, 0, 0, 0, 0, issues);
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var networks = await db.ChargingNetworks.Where(network => network.SourceKey == sourceKey).ToDictionaryAsync(network => network.Key, cancellationToken);
        foreach (var record in snapshot.Networks)
        {
            if (!networks.Remove(record.Key, out var network))
            {
                network = ChargingNetwork.Create(record.Key, record.Name, record.Country, record.Operators, sourceKey);
                db.ChargingNetworks.Add(network);
            }

            network.Update(record.Name, record.Country, record.Operators, sourceKey);
            network.SetPriceNote(record.PriceNote, record.PriceUrl, (record.Sites ?? []).Select(site => new NetworkSite(site.Name.Trim(), site.Latitude, site.Longitude, site.Url.Trim())));
        }

        // Networks the source no longer lists go; zones that name them cover nothing.
        db.ChargingNetworks.RemoveRange(networks.Values);

        List<string> empKeys = [.. snapshot.Emps.Select(emp => emp.Key)];
        var emps = await db.Emps.Where(emp => empKeys.Contains(emp.Key)).ToDictionaryAsync(emp => emp.Key, cancellationToken);
        var open = await db.TariffPlans.Where(plan => plan.SourceKey == sourceKey && plan.ValidTo == null).ToListAsync(cancellationToken);
        var (created, changed, unchanged) = (0, 0, 0);
        var seen = new HashSet<Guid>();
        foreach (var record in snapshot.Emps)
        {
            if (emps.TryGetValue(record.Key, out var emp))
            {
                emp.Update(record.Name, record.Website, record.Country);
            }
            else
            {
                emp = Emp.Create(record.Key, record.Name, record.Website, record.Country);
                db.Emps.Add(emp);
            }

            foreach (var tariff in record.Tariffs)
            {
                var content = ContentOf(tariff);
                var hash = Hash(content);
                var current = open.FirstOrDefault(plan => plan.EmpId == emp.Id && plan.Key == tariff.Key);
                if (current is not null)
                {
                    seen.Add(current.Id);
                    if (current.ContentHash == hash)
                    {
                        current.Check(tariff.CheckedOn);
                        unchanged++;
                        continue;
                    }

                    current.Close(now);
                    changed++;
                }
                else
                {
                    created++;
                }

                db.TariffPlans.Add(TariffPlan.Create(emp.Id, sourceKey, content, hash, now, tariff.CheckedOn));
            }
        }

        var vanished = open.Where(plan => !seen.Contains(plan.Id)).ToList();
        vanished.ForEach(plan => plan.Close(now));
        if (actor is not null)
        {
            audit.Record("tariff.import", $"tariff-source:{sourceKey}", actor);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CatalogImportResult(snapshot.Networks.Count, snapshot.Emps.Count, created, changed, vanished.Count, unchanged, []);
    }

    /// <summary>Every rule of the catalog format; the messages name the record (docs/06, "Tarife").</summary>
    public static List<string> Validate(TariffCatalogSnapshot snapshot, IReadOnlyCollection<string> otherNetworks)
    {
        var issues = new List<string>();
        var networkKeys = new HashSet<string>(otherNetworks, StringComparer.Ordinal);
        foreach (var network in snapshot.Networks)
        {
            var where = $"network '{network.Key}'";
            Require(Emp.IsKey(network.Key), $"{where}: the key must have 2 to 40 lower-case letters, digits or '-'.");
            Require(networkKeys.Add(network.Key), $"{where}: the key is used twice.");
            Require(!string.IsNullOrWhiteSpace(network.Name), $"{where}: a name is required.");
            Require(IsCountry(network.Country), $"{where}: the country must be ISO 3166-1 alpha-2, e.g. AT.");
            Require(network.Operators is { Count: > 0 } operators && operators.All(IsOperator), $"{where}: operators must be IDs such as AT*EVN.");
            Require(network.PriceNote is null || network.PriceNote.Trim().Length is > 0 and <= MaxNoteLength, $"{where}: priceNote has 1 to {MaxNoteLength} characters.");
            Require(network.PriceUrl is null || IsWebUrl(network.PriceUrl), $"{where}: priceUrl must be an http(s) URL.");
            Require(
                network.Sites is null || (network.PriceNote is not null && network.Sites.All(site => !string.IsNullOrWhiteSpace(site.Name) && site.Latitude is >= -90 and <= 90 && site.Longitude is >= -180 and <= 180 && IsWebUrl(site.Url))),
                $"{where}: sites need a priceNote, a name, coordinates and an http(s) URL.");
        }

        var empKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var emp in snapshot.Emps)
        {
            var where = $"emp '{emp.Key}'";
            Require(Emp.IsKey(emp.Key), $"{where}: the key must have 2 to 40 lower-case letters, digits or '-'.");
            Require(empKeys.Add(emp.Key), $"{where}: the key is used twice.");
            Require(!string.IsNullOrWhiteSpace(emp.Name), $"{where}: a name is required.");
            Require(IsCountry(emp.Country), $"{where}: the country must be ISO 3166-1 alpha-2.");
            var tariffKeys = new HashSet<string>(StringComparer.Ordinal);
            Require(emp.Tariffs is not null, $"{where}: tariffs are required (may be empty).");
            foreach (var tariff in emp.Tariffs ?? [])
            {
                var at = $"{where}, tariff '{tariff.Key}'";
                Require(Emp.IsKey(tariff.Key), $"{at}: the key must have 2 to 40 lower-case letters, digits or '-'.");
                Require(tariffKeys.Add(tariff.Key), $"{at}: the key is used twice.");
                Require(!string.IsNullOrWhiteSpace(tariff.Name), $"{at}: a name is required.");
                Require(tariff.Currency is not null && CurrencyPattern().IsMatch(tariff.Currency), $"{at}: the currency must be ISO 4217, e.g. EUR.");
                Require(tariff.MonthlyFee is null or (>= 0 and <= MaxAmount), $"{at}: the monthly fee must lie between 0 and {MaxAmount}.");
                Require(tariff.Markets is { Count: > 0 } markets && markets.All(IsCountry), $"{at}: markets must be countries such as AT.");
                Require(IsWebUrl(tariff.SourceUrl), $"{at}: the source must be an http(s) URL.");
                Require(Enum.TryParse<TariffConfidence>(tariff.Confidence, false, out _), $"{at}: the confidence must be HIGH, MEDIUM or LOW.");
                Require(tariff.Zones is { Count: > 0 }, $"{at}: at least one zone is required.");
                foreach (var zone in tariff.Zones ?? [])
                {
                    var z = $"{at}, zone {zone.Kind}";
                    Require(Enum.TryParse<TariffZoneKind>(zone.Kind, false, out var kind), $"{z}: the kind must be OPERATOR, HOME, PARTNER or ROAMING.");
                    var networks = (zone.Networks ?? []).Concat(zone.ExcludedNetworks ?? []).ToList();
                    Require(networks.All(networkKeys.Contains), $"{z}: unknown networks {string.Join(", ", networks.Where(key => !networkKeys.Contains(key)))}.");
                    Require((zone.Operators ?? []).Concat(zone.ExcludedOperators ?? []).All(IsOperator), $"{z}: operators must be IDs such as AT*EVN.");
                    Require(kind == TariffZoneKind.ROAMING || (zone.Networks ?? []).Count + (zone.Operators ?? []).Count > 0, $"{z}: the zone must name networks or operators.");
                    var pricing = TariffPricing.FIXED;
                    Require(zone.Pricing is null || Enum.TryParse(zone.Pricing, false, out pricing), $"{z}: the pricing must be FIXED, OPERATOR_AD_HOC or UNKNOWN.");
                    var fixedPrices = pricing == TariffPricing.FIXED;
                    Require(!fixedPrices || zone.Prices is { Count: > 0 }, $"{z}: at least one price is required.");
                    Require(fixedPrices || zone.Prices is null or { Count: 0 }, $"{z}: a zone with the operator's or an unknown price has no prices of its own.");
                    Require(pricing == TariffPricing.OPERATOR_AD_HOC || zone.MarkupPercent is null && zone.MarkupSessionFee is null, $"{z}: markups need the pricing OPERATOR_AD_HOC.");
                    Require(zone.MarkupPercent is null or (>= MinMarkupPercent and <= MaxMarkupPercent), $"{z}: markupPercent must lie between {MinMarkupPercent} and {MaxMarkupPercent}.");
                    Require(zone.MarkupSessionFee is null or (>= 0 and <= MaxAmount), $"{z}: markupSessionFee must lie between 0 and {MaxAmount}.");
                    Require(zone.Currents is null || (zone.Currents.Count > 0 && zone.Currents.All(item => Enum.TryParse<TariffCurrent>(item, false, out _))), $"{z}: currents must be AC and/or DC.");
                    Require(zone.Countries is null || (zone.Countries.Count > 0 && zone.Countries.All(IsCountry)), $"{z}: countries must be ISO 3166-1 alpha-2 codes such as DE.");
                    foreach (var price in zone.Prices ?? [])
                    {
                        Require(Enum.TryParse<TariffCurrent>(price.Current, false, out _), $"{z}: the current must be AC or DC.");
                        Require((price.PricePerKwh ?? price.PricePerMinute ?? price.SessionFee) is not null, $"{z}: a price needs pricePerKwh, pricePerMinute or sessionFee.");
                        Require(price.MinKw is null or > 0, $"{z}: minKw must be above 0.");
                        var (start, end) = (ParseTime(price.StartTime), ParseTime(price.EndTime));
                        Require(
                            (price.StartTime is null && price.EndTime is null) || (start is not null && end is not null && start != end),
                            $"{z}: startTime and endTime come together as different times HH:mm.");
                        Require(price.MaxBlockingFee is null || (price.BlockingFeePerMinute is not null && price.MaxBlockingFee is >= 0 and <= MaxAmount), $"{z}: maxBlockingFee needs a blocking fee and lies between 0 and {MaxAmount}.");
                        Require(price.BlockingFrom is null || Enum.TryParse<TariffBlockingFrom>(price.BlockingFrom, false, out _), $"{z}: blockingFrom must be SESSION_START or CHARGING_END.");
                        Require(price.Days is null || (price.Days.Count > 0 && price.Days.All(day => day is >= 1 and <= 7) && price.Days.Distinct().Count() == price.Days.Count), $"{z}: days must be distinct ISO weekdays 1 to 7.");
                        var (blockingStart, blockingEnd) = (ParseTime(price.BlockingStartTime), ParseTime(price.BlockingEndTime));
                        Require(
                            (price.BlockingStartTime is null && price.BlockingEndTime is null) || (blockingStart is not null && blockingEnd is not null && blockingStart != blockingEnd && price.BlockingFeePerMinute is not null),
                            $"{z}: blockingStartTime and blockingEndTime come together as different times HH:mm with a blocking fee.");
                        Require(price.MaxBlockingMinutes is null || (price.MaxBlockingMinutes > 0 && price.BlockingFeePerMinute is not null), $"{z}: maxBlockingMinutes needs a blocking fee and lies above 0.");
                        Require(price.BlockingAfterMinutes is null or >= 0, $"{z}: blockingAfterMinutes must not be negative.");
                        Require(
                            new[] { price.PricePerKwh, price.PricePerMinute, price.SessionFee, price.BlockingFeePerMinute }.All(amount => amount is null or (>= 0 and <= MaxAmount)),
                            $"{z}: amounts must lie between 0 and {MaxAmount}.");
                    }
                }
            }
        }

        return issues;

        void Require(bool valid, string message)
        {
            if (!valid)
            {
                issues.Add(message);
            }
        }
    }

    /// <summary>An operator ID in any case, as the import stores it upper-case.</summary>
    private static bool IsOperator(string? value) => value is not null && ChargingNetwork.IsOperatorId(value.Trim().ToUpperInvariant());

    private static string? Clock(TimeOnly? time) => time?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A local time "HH:mm"; null for anything else.</summary>
    private static TimeOnly? ParseTime(string? value) =>
        TimeOnly.TryParseExact(value, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var time) ? time : null;

    private static bool IsWebUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme is "https" or "http";

    private static bool IsCountry(string? value) => value is { Length: 2 } && value.All(char.IsAsciiLetterUpper);

    /// <summary>The domain content of a tariff record of a source.</summary>
    public static TariffPlan.Content ContentOf(TariffRecord record) =>
        new(
            record.Key,
            record.Name.Trim(),
            record.Currency,
            record.MonthlyFee,
            record.Prerequisites,
            record.Markets,
            [.. record.Zones.Select(zone => new TariffZone(
                Enum.Parse<TariffZoneKind>(zone.Kind),
                zone.Networks ?? [],
                [.. (zone.Operators ?? []).Select(id => id.Trim().ToUpperInvariant())],
                zone.ExcludedNetworks ?? [],
                [.. (zone.ExcludedOperators ?? []).Select(id => id.Trim().ToUpperInvariant())],
                [.. (zone.Prices ?? []).Select(price => new TariffPriceElement(
                    Enum.Parse<TariffCurrent>(price.Current), price.MinKw, price.PricePerKwh, price.PricePerMinute, price.SessionFee,
                    price.BlockingFeePerMinute, price.BlockingAfterMinutes, ParseTime(price.StartTime), ParseTime(price.EndTime), price.MaxBlockingFee,
                    price.BlockingFrom is null ? TariffBlockingFrom.SESSION_START : Enum.Parse<TariffBlockingFrom>(price.BlockingFrom),
                    price.Days is null ? null : [.. price.Days.Order()], ParseTime(price.BlockingStartTime), ParseTime(price.BlockingEndTime), price.MaxBlockingMinutes))],
                zone.Notes,
                zone.Pricing is null ? TariffPricing.FIXED : Enum.Parse<TariffPricing>(zone.Pricing),
                zone.MarkupPercent,
                zone.MarkupSessionFee,
                zone.Currents is null ? null : [.. zone.Currents.Select(Enum.Parse<TariffCurrent>)],
                zone.Countries is null ? null : [.. zone.Countries.Distinct().Order(StringComparer.Ordinal)]))],
            record.Notes,
            record.SourceUrl,
            record.SourceDate,
            Enum.Parse<TariffConfidence>(record.Confidence));

    private static string Hash(TariffPlan.Content content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content, TariffCatalogSnapshot.Json))));

    private static TariffView View(TariffPlan plan, string emp, string empName, DateOnly staleBefore) =>
        new(
            plan.Id,
            emp,
            empName,
            plan.Key,
            plan.Name,
            plan.Currency,
            plan.MonthlyFee,
            plan.Prerequisites,
            plan.Markets,
            [.. plan.Zones.Select(zone => new ZoneView(
                zone.Kind.ToString(), zone.Networks, zone.Operators, zone.ExcludedNetworks, zone.ExcludedOperators,
                [.. zone.Prices.Select(price => new PriceElementView(
                    price.Current.ToString(), price.MinKw, price.PricePerKwh, price.PricePerMinute, price.SessionFee,
                    price.BlockingFeePerMinute, price.BlockingAfterMinutes, price.StartTime?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                    price.EndTime?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), price.MaxBlockingFee, price.BlockingFrom.ToString(),
                    price.Days, Clock(price.BlockingStartTime), Clock(price.BlockingEndTime), price.MaxBlockingMinutes))],
                zone.Notes,
                zone.Pricing.ToString(),
                zone.MarkupPercent,
                zone.MarkupSessionFee,
                zone.Currents is null ? null : [.. zone.Currents.Select(current => current.ToString())],
                zone.Countries))],
            plan.Notes,
            plan.SourceUrl,
            plan.SourceDate,
            plan.CheckedOn,
            plan.Confidence.ToString(),
            plan.ValidFrom,
            plan.ValidTo,
            !(plan.SourceDate >= staleBefore || plan.CheckedOn >= staleBefore));

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();
}
