using ChargeGlobe.Application.Evses;
using ChargeGlobe.Application.Settings;
using Microsoft.EntityFrameworkCore;

namespace ChargeGlobe.Infrastructure.Persistence;

/// <summary>
/// The implausible ad-hoc prices: per active EVSE the newest ad-hoc price of any source, with the same rule as
/// <see cref="Domain.Charging.PriceBounds.Accepts"/> in SQL, grouped by operator and price.
/// </summary>
public sealed class PriceQuality(AppDbContext db, IAppConfig config, TimeProvider timeProvider) : IPriceQuality
{
    private const int MaxExamples = 3;

    public async Task<ImplausiblePrices> ImplausibleAsync(CancellationToken cancellationToken)
    {
        var bounds = PricePlausibility.From(config);
        var (min, max) = (bounds.MinKwh, bounds.MaxKwh);
        var rows = await db.Database.SqlQuery<PriceRow>($"""
            WITH newest AS (
                SELECT DISTINCT ON (p.evse_id) p.evse_id, p.price_per_kwh, p.price_per_minute, p.session_fee, p.free_of_charge, p.observed_at
                FROM evse_price_current p
                WHERE p.tariff_kind = 'AD_HOC'
                ORDER BY p.evse_id, p.observed_at DESC),
            bad AS (
                SELECT n.*,
                       CASE WHEN e.identity_kind = 'EVSE_ID'
                            THEN upper(split_part(e.evse_id_display, '*', 1) || '*' || split_part(e.evse_id_display, '*', 2))
                            ELSE (SELECT o.country || '*' || o.operator_id FROM operator o
                                  WHERE o.id = coalesce(e.operator_id, l.operator_id) AND o.operator_id IS NOT NULL) END AS operator
                FROM newest n
                JOIN evse e ON e.id = n.evse_id AND e.lifecycle = 'ACTIVE'
                JOIN location l ON l.id = e.location_id AND l.lifecycle = 'ACTIVE'
                WHERE NOT n.free_of_charge
                  AND ((n.price_per_kwh > 0 AND (n.price_per_kwh < {min} OR n.price_per_kwh > {max}))
                       OR (coalesce(n.price_per_kwh, 0) = 0 AND coalesce(n.price_per_minute, 0) = 0 AND coalesce(n.session_fee, 0) = 0)))
            SELECT b.operator AS "Operator", b.price_per_kwh AS "PricePerKwh", b.price_per_minute AS "PricePerMinute", b.session_fee AS "SessionFee",
                   count(*)::int AS "Evses", max(b.observed_at) AS "LastObservedAt",
                   (SELECT o.name FROM operator o WHERE o.country || '*' || o.operator_id = b.operator LIMIT 1) AS "Name"
            FROM bad b
            GROUP BY b.operator, b.price_per_kwh, b.price_per_minute, b.session_fee
            """).ToListAsync(cancellationToken);
        List<OperatorImplausiblePrices> operators = [.. rows
            .GroupBy(row => row.Operator)
            .Select(group => new OperatorImplausiblePrices(
                group.Key,
                group.First().Name,
                group.Sum(row => row.Evses),
                [.. group.OrderByDescending(row => row.Evses).Take(MaxExamples).Select(row => new PriceSample(row.PricePerKwh, row.PricePerMinute, row.SessionFee, row.Evses))],
                group.Max(row => row.LastObservedAt)))
            .OrderByDescending(item => item.Evses)
            .ThenBy(item => item.OperatorId, StringComparer.Ordinal)];
        return new ImplausiblePrices(timeProvider.GetUtcNow(), min, max, operators.Sum(item => item.Evses), operators);
    }

    private sealed record PriceRow(string? Operator, decimal? PricePerKwh, decimal? PricePerMinute, decimal? SessionFee, int Evses, DateTimeOffset LastObservedAt, string? Name);
}
