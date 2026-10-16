using System.Globalization;
using ChargeGlobe.Application.Settings;
using ChargeGlobe.Application.Sources;
using ChargeGlobe.Domain.Provenance;

namespace ChargeGlobe.Application.Addresses;

public enum AddressImportOutcome
{
    /// <summary>The last check is younger than <c>address.refresh_days</c>.</summary>
    NotDue,

    /// <summary>None of the expected reference dates is online.</summary>
    NoRelease,

    /// <summary>The imported reference date is the newest.</summary>
    UpToDate,

    Imported,
}

public sealed record AddressImportResult(AddressImportOutcome Outcome, DateOnly? ReferenceDate, int Addresses);

/// <summary>
/// Keeps the addresses of the geocoder at the newest reference date of the BEV address register: checks every
/// <c>address.refresh_days</c>, downloads a newer release, replaces all addresses and updates the attribution of the
/// source <c>bev-address</c>. Runs in one worker at a time (job lease <see cref="JobName"/>).
/// </summary>
public sealed class AddressImporter(
    IAddressRegister register, IAddressStore store, ISourceRegistry sources, IAppConfig config, TimeProvider timeProvider)
{
    public const string JobName = "address-import";

    public const string SourceKey = "bev-address";

    /// <summary>The source row with the attribution the licence (CC BY 4.0) asks for, with the reference date of the data.</summary>
    public static SourceDescription Source(DateOnly referenceDate) =>
        new(
            SourceKey,
            "BEV Adressregister",
            SourceTier.OFFICIAL,
            "CC-BY-4.0",
            string.Create(CultureInfo.InvariantCulture, $"© Österreichisches Adressregister, Stichtagsdaten vom {referenceDate:dd.MM.yyyy}"),
            []);

    public async Task<AddressImportResult> RunAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var state = await store.StateAsync(cancellationToken);
        if (state is not null && now - state.CheckedAt < TimeSpan.FromDays(config.Get<int>(SettingsCatalog.AddressRefreshDays)))
        {
            return new AddressImportResult(AddressImportOutcome.NotDue, state.ReferenceDate, state.Addresses);
        }

        var release = await register.FindNewestAsync(cancellationToken);
        if (release is null)
        {
            return new AddressImportResult(AddressImportOutcome.NoRelease, state?.ReferenceDate, state?.Addresses ?? 0);
        }

        if (state is not null && release.ReferenceDate <= state.ReferenceDate)
        {
            await store.MarkCheckedAsync(now, cancellationToken);
            return new AddressImportResult(AddressImportOutcome.UpToDate, state.ReferenceDate, state.Addresses);
        }

        await using var file = await register.OpenAsync(release, cancellationToken);
        var count = await store.ReplaceAsync(file.ReadAsync(cancellationToken), file.ReferenceDate, now, cancellationToken);
        await sources.SyncAsync([Source(file.ReferenceDate)], cancellationToken);
        return new AddressImportResult(AddressImportOutcome.Imported, file.ReferenceDate, count);
    }
}
