using System.Text.Json;
using ChargeGlobe.Providers.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace ChargeGlobe.Providers.TariffCatalog;

/// <summary>
/// The curated tariff catalog (docs/adr/0013): JSON files under <c>catalog/&lt;country&gt;/</c> in the format of
/// <see cref="TariffCatalogSnapshot"/>, one per provider plus the networks of the operators, each tariff with the URL and
/// date of the price sheet it comes from. Compiled into the assembly; a change ships with the next deploy.
/// </summary>
public sealed class CuratedTariffCatalog : ITariffSource
{
    public const string SourceKey = "tariff-catalog";

    private const string Prefix = "tariff-catalog/";

    public string Key => SourceKey;

    /// <summary>The names of the catalog files, e.g. <c>at/evn.json</c>.</summary>
    public static IReadOnlyList<string> Files() =>
        [.. Resources().Keys.Order(StringComparer.Ordinal)];

    /// <summary>One catalog file.</summary>
    public static TariffCatalogSnapshot Read(string file)
    {
        using var stream = typeof(CuratedTariffCatalog).Assembly.GetManifestResourceStream(Resources()[file])!;
        return JsonSerializer.Deserialize<TariffCatalogSnapshot>(stream, TariffCatalogSnapshot.Json)!;
    }

    /// <summary>The catalog resources by file name with '/' (MSBuild may write '\' for the folders).</summary>
    private static Dictionary<string, string> Resources() =>
        typeof(CuratedTariffCatalog).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
            .ToDictionary(name => name[Prefix.Length..].Replace((char)92, '/'), StringComparer.Ordinal);

    public Task<TariffCatalogSnapshot> FetchAsync(CancellationToken cancellationToken) =>
        Task.FromResult(TariffCatalogSnapshot.Merge(Files().Select(Read)));
}

public static class TariffCatalogServiceCollectionExtensions
{
    public static IServiceCollection AddCuratedTariffCatalog(this IServiceCollection services) =>
        services.AddSingleton<ITariffSource, CuratedTariffCatalog>();
}
