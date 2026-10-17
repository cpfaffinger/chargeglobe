using ChargeGlobe.Application.Evses;
using ChargeGlobe.Application.Settings;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Persistence;

/// <summary>
/// The implausible ad-hoc prices: per active EVSE the newest ad-hoc price of any source, judged in SQL with the same rule
/// as <see cref="Domain.Charging.PriceBounds.IssueOf"/>; the summary groups these rows by operator and price.
/// </summary>
public sealed class PriceQuality(AppDbContext db, IAppConfig config, TimeProvider timeProvider) : IPriceQuality
{
    private const int MaxExamples = 3;

    public async Task<ImplausiblePrices> ImplausibleAsync(CancellationToken cancellationToken)
    {
        var bounds = PricePlausibility.From(config);
        var rows = await ImplausibleEvsesAsync(cancellationToken);
        List<OperatorImplausiblePrices> operators = [.. rows
            .GroupBy(row => row.OperatorId)
            .Select(group => new OperatorImplausiblePrices(
                group.Key,
                group.First().OperatorName,
                group.Count(),
                [.. group
                    .GroupBy(row => (row.PricePerKwh, row.PricePerMinute, row.SessionFee, row.Reason))
                    .OrderByDescending(prices => prices.Count())
                    .Take(MaxExamples)
                    .Select(prices => new PriceSample(
                        prices.Key.PricePerKwh, prices.Key.PricePerMinute, prices.Key.SessionFee, prices.Count(), prices.Key.Reason, bounds.IntendedKwh(prices.Key.PricePerKwh)))],
                group.Max(row => row.ObservedAt)))
            .OrderByDescending(item => item.Evses)
            .ThenBy(item => item.OperatorId, StringComparer.Ordinal)];
        int Count(string reason) => rows.Count(row => row.Reason == reason);
        return new ImplausiblePrices(
            timeProvider.GetUtcNow(), bounds.MinKwh, bounds.MaxKwh, rows.Count, Count("NO_AMOUNT"), Count("BELOW_MIN"), Count("ABOVE_MAX"), operators);
    }

    public async Task<IReadOnlyList<ImplausibleEvse>> ImplausibleEvsesAsync(CancellationToken cancellationToken)
    {
        var bounds = PricePlausibility.From(config);
        var (min, max) = (bounds.MinKwh, bounds.MaxKwh);
        var rows = await db.Database.SqlQuery<EvseRow>($"""
            WITH newest AS (
                SELECT DISTINCT ON (p.evse_id) p.evse_id, p.source_key, p.price_per_kwh, p.price_per_minute, p.session_fee,
                       p.blocking_fee_per_minute, p.free_of_charge, p.observed_at
                FROM evse_price_current p
                WHERE p.tariff_kind = 'AD_HOC'
                ORDER BY p.evse_id, p.observed_at DESC),
            judged AS (
                SELECT n.*, e.evse_id_display, l.name AS location_name, l.city,
                       CASE WHEN e.identity_kind = 'EVSE_ID'
                            THEN upper(split_part(e.evse_id_display, '*', 1) || '*' || split_part(e.evse_id_display, '*', 2))
                            ELSE (SELECT o.country || '*' || o.operator_id FROM operator o
                                  WHERE o.id = coalesce(e.operator_id, l.operator_id) AND o.operator_id IS NOT NULL) END AS operator,
                       CASE WHEN n.free_of_charge THEN NULL
                            WHEN n.price_per_kwh > 0 AND n.price_per_kwh < {min} THEN 'BELOW_MIN'
                            WHEN n.price_per_kwh > {max} THEN 'ABOVE_MAX'
                            WHEN coalesce(n.price_per_kwh, 0) = 0 AND coalesce(n.price_per_minute, 0) = 0 AND coalesce(n.session_fee, 0) = 0
                                 AND coalesce(n.blocking_fee_per_minute, 0) = 0 THEN 'NO_AMOUNT' END AS reason
                FROM newest n
                JOIN evse e ON e.id = n.evse_id AND e.lifecycle = 'ACTIVE'
                JOIN location l ON l.id = e.location_id AND l.lifecycle = 'ACTIVE')
            SELECT j.operator AS "OperatorId",
                   (SELECT o.name FROM operator o WHERE o.country || '*' || o.operator_id = j.operator LIMIT 1) AS "OperatorName",
                   j.evse_id_display AS "EvseId", j.location_name AS "LocationName", j.city AS "City", j.reason AS "Reason",
                   j.price_per_kwh AS "PricePerKwh", j.price_per_minute AS "PricePerMinute", j.session_fee AS "SessionFee",
                   j.blocking_fee_per_minute AS "BlockingFeePerMinute", j.source_key AS "Source", j.observed_at AS "ObservedAt"
            FROM judged j
            WHERE j.reason IS NOT NULL
            ORDER BY j.operator NULLS LAST, j.evse_id_display
            """).ToListAsync(cancellationToken);
        return [.. rows.Select(row => new ImplausibleEvse(
            row.OperatorId, row.OperatorName, row.EvseId, row.LocationName, row.City, row.Reason, row.PricePerKwh, row.PricePerMinute, row.SessionFee,
            row.BlockingFeePerMinute, row.Source, row.ObservedAt, bounds.IntendedKwh(row.PricePerKwh)))];
    }

    private sealed record EvseRow(
        string? OperatorId,
        string? OperatorName,
        string EvseId,
        string? LocationName,
        string? City,
        string Reason,
        decimal? PricePerKwh,
        decimal? PricePerMinute,
        decimal? SessionFee,
        decimal? BlockingFeePerMinute,
        string Source,
        DateTimeOffset ObservedAt);
}
