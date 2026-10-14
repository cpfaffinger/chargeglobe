using ChargeGlobe.Application.Evses;
using ChargeGlobe.Application.Locations;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Domain.Charging;
using ChargeGlobe.Domain.Provenance;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ChargeGlobe.Infrastructure.Persistence;

/// <summary>
/// EVSE detail read model: master data with the current status, status intervals and the hourly occupancy from the
/// continuous aggregate <c>evse_occupancy_hourly</c>.
/// </summary>
public sealed class EvseQueries(AppDbContext db, IAppConfig config, TimeProvider timeProvider) : IEvseQueries
{
    /// <summary>Postgres error "invalid_parameter_value", raised for an unknown time zone.</summary>
    private const string InvalidParameterValue = "22023";

    public async Task<EvseView?> FindAsync(string evseId, CancellationToken cancellationToken)
    {
        var evses = db.Evses.AsNoTracking().Include(item => item.Connectors);
        // Same normalization as EvseId (separators *, - and blanks), but also for surrogates (XX*CG*E…).
        var normalized = string.Concat(evseId.ToUpperInvariant().Where(character => character is not ('*' or '-') && !char.IsWhiteSpace(character)));
        var evse = Guid.TryParse(evseId, out var id)
            ? await evses.FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            : await evses.FirstOrDefaultAsync(item => item.EvseIdNorm == normalized, cancellationToken);
        if (evse is null)
        {
            return null;
        }

        var location = await db.Locations.AsNoTracking().SingleAsync(item => item.Id == evse.LocationId, cancellationToken);
        var operatorId = evse.OperatorId ?? location.OperatorId;
        var @operator = operatorId is { } key
            ? await db.Operators.AsNoTracking()
                .Where(item => item.Id == key)
                .Select(item => new EvseOperator(item.Id, item.Name, item.OperatorId))
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        var current = await db.EvseStatusCurrent.AsNoTracking().FirstOrDefaultAsync(item => item.EvseId == evse.Id, cancellationToken);
        var maxAge = TimeSpan.FromMinutes(config.Get<int>(SettingsCatalog.MapStatusMaxAgeMinutes));
        var status = current is null
            ? new EvseCurrentStatus(nameof(EvseStatus.UNKNOWN), null, null, null, true)
            : new EvseCurrentStatus(
                current.Status.ToString(), current.Since, current.ObservedAt, current.SourceKey, current.IsStale(timeProvider.GetUtcNow(), maxAge));

        return new EvseView(
            evse.Id,
            evse.EvseIdDisplay,
            evse.IdentityKind == IdentityKind.EVSE_ID,
            evse.Lifecycle.ToString(),
            new EvseLocation(
                location.Id,
                location.Name,
                location.Address,
                location.PostalCode,
                location.City,
                location.Country,
                location.Coordinates.Latitude,
                location.Coordinates.Longitude,
                location.RegionCode),
            @operator,
            evse.PhysicalReference,
            evse.MaxPowerKw,
            evse.PowerType?.ToString(),
            [.. evse.Connectors.OrderBy(connector => connector.Ordinal).Select(connector => new ConnectorDetail(
                connector.Standard.ToString(), connector.Format?.ToString(), connector.PowerType?.ToString(), connector.MaxPowerKw))],
            status,
            evse.FirstSeen,
            evse.LastSeen,
            evse.LastConfirmedAt);
    }

    public async Task<IReadOnlyList<StatusInterval>> StatusHistoryAsync(
        Guid id, DateTimeOffset from, DateTimeOffset to, string? source, CancellationToken cancellationToken)
    {
        var intervals = await db.EvseStatusIntervals.AsNoTracking()
            .Where(interval => interval.EvseId == id
                && interval.ValidFrom < to
                && (interval.ValidTo == null || interval.ValidTo > from)
                && (source == null || interval.SourceKey == source))
            .OrderBy(interval => interval.ValidFrom)
            .ToListAsync(cancellationToken);
        return [.. intervals.Select(interval => new StatusInterval(
            interval.Status.ToString(), interval.ValidFrom, interval.ValidTo, interval.SourceKey, interval.Confidence))];
    }

    public async Task<IReadOnlyList<OccupancyCell>?> OccupancyAsync(
        Guid id, DateTimeOffset from, DateTimeOffset to, string timeZone, CancellationToken cancellationToken)
    {
        try
        {
            // Fails for an unknown zone even when the EVSE has no observations.
            await db.Database.SqlQuery<DateTime>($"SELECT now() AT TIME ZONE {timeZone} AS \"Value\"").SingleAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == InvalidParameterValue)
        {
            return null;
        }

        var rows = await db.Database
            .SqlQuery<OccupancyRow>($"""
                SELECT extract(isodow FROM bucket AT TIME ZONE {timeZone})::int AS "Weekday",
                       extract(hour FROM bucket AT TIME ZONE {timeZone})::int AS "Hour",
                       sum(obs_charging)::bigint AS "Charging",
                       sum(obs_total)::bigint AS "Total"
                FROM evse_occupancy_hourly
                WHERE evse_id = {id} AND bucket >= {from} AND bucket < {to}
                GROUP BY 1, 2
                ORDER BY 1, 2
                """)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(row => new OccupancyCell(row.Weekday, row.Hour, row.Charging, row.Total))];
    }

    public async Task<IReadOnlyList<SourceAttribution>> AttributionAsync(Guid id, CancellationToken cancellationToken)
    {
        var keys = await db.SourceRecords
            .Where(record => record.EntityType == EntityType.EVSE && record.EntityId == id)
            .Select(record => record.SourceKey)
            .Distinct()
            .ToListAsync(cancellationToken);
        return await db.Sources
            .Where(source => keys.Contains(source.Key))
            .OrderBy(source => source.Key)
            .Select(source => new SourceAttribution(source.Key, source.AttributionText, source.License))
            .ToListAsync(cancellationToken);
    }

    /// <summary>One row of the occupancy query; property names are the column aliases.</summary>
    private sealed class OccupancyRow
    {
        public int Weekday { get; set; }

        public int Hour { get; set; }

        public long Charging { get; set; }

        public long Total { get; set; }
    }
}
