# 04 – Provider-Interface & Anti-Corruption-Layer

## Ziel

Jede Quelle (amtliches Verzeichnis, Open-Data-Export, Web-Karte, App-Backend, OCPI-Feed) wird über **denselben Vertrag** angebunden. Der Adapter kennt die Quelle, der Rest des Systems kennt nur den Vertrag. Quell-Eigenheiten (Feldnamen, Status-Strings wie `"frei"`/`"besetzt"`/`"2"`, Koordinaten als Strings, Stecker als Freitext) werden ausschließlich im Adapter übersetzt – das ist der **Anti-Corruption-Layer (ACL)**.

```
Quelle ──HTTP──▶ Adapter (Providers.<Quelle>)
                   ├─ Client       : typed HttpClient, Auth, Rate-Limit, Polly
                   ├─ Raw DTOs     : 1:1 Abbild der Quell-Antwort (System.Text.Json)
                   ├─ Mapper       : Raw → Ingest-Modell (ProviderLocationSnapshot, …)
                   └─ Descriptor   : Key, Tier, Capabilities, Lizenz, Default-Intervalle
                 ──▶ Providers.Abstractions (Vertrag)
                 ──▶ Application (Identity Resolution, Merge, Persistenz)
```

## Vertrag (`ChargeGlobe.Providers.Abstractions`)

Quelltext: `src/ChargeGlobe.Providers.Abstractions` (`Contract.cs`, `IngestModel.cs`, `StatusMapping.cs`). Das Projekt referenziert aus `ChargeGlobe.Domain` nur das OCPI-Vokabular (`EvseStatus`, `ConnectorStandard`, `ConnectorFormat`, `PowerType`, `AccessType`), `SourceTier` und `GeoPoint` – keine Entitäten.

```csharp
public interface IProvider { ProviderDescriptor Descriptor { get; } }

public sealed record ProviderDescriptor(
    string Key,                       // "econtrol", "ocm", "osm", "evn", ...; Settings-Scope provider:<key>
    string DisplayName,
    SourceTier Tier,                  // OFFICIAL, OPEN_DATA, PUBLIC_WEB, APP_BACKEND, PARTNER
    ProviderCapabilities Capabilities,
    string License,                   // z. B. "CC BY 4.0", "ODbL", "ToS – nur Anzeige"
    string AttributionText,
    IReadOnlyList<string> Countries,  // ISO 3166-1 alpha-2
    PollDefaults PollDefaults,        // (TimeSpan? Inventory, TimeSpan? Status, TimeSpan? Prices)
    RateLimitPolicy RateLimit);       // (int PerMinute, int Burst, int? PerDay)

[Flags]
public enum ProviderCapabilities
{
    None = 0, StaticInventory = 1 << 0, EvseIds = 1 << 1, LiveStatusPerEvse = 1 << 2,
    LiveStatusPerLocation = 1 << 3, AdHocPrice = 1 << 4, EmpTariff = 1 << 5,
    BulkByBoundingBox = 1 << 6, BulkAll = 1 << 7, ByEvseId = 1 << 8, DeltaSince = 1 << 9, Ocpi = 1 << 10,
}

public interface IInventorySource : IProvider
{ IAsyncEnumerable<ProviderLocationSnapshot> FetchInventoryAsync(InventoryQuery query, CancellationToken ct); }

/// Belegt-Status. Der Adapter wählt selbst den billigsten Weg (bbox > bulk > ids).
public interface IStatusSource : IProvider
{ IAsyncEnumerable<ProviderEvseStatus> FetchStatusAsync(StatusQuery query, CancellationToken ct); }

public interface IPriceSource : IProvider
{ IAsyncEnumerable<ProviderPrice> FetchPricesAsync(PriceQuery query, CancellationToken ct); }

/// Ein billiger Request, der Erreichbarkeit und unverändertes Format zeigt.
public interface IProviderHealth : IProvider
{ Task<ProviderProbeResult> ProbeAsync(CancellationToken ct); }

public sealed record InventoryQuery(BoundingBox? Bbox, string? Country, DateTimeOffset? Since);
public sealed record StatusQuery(BoundingBox? Bbox, IReadOnlyCollection<ProviderEvseRef>? Evses);
public sealed record PriceQuery(BoundingBox? Bbox, IReadOnlyCollection<ProviderEvseRef>? Evses);
public sealed record ProviderEvseRef(string ExternalId, string? EvseId, Guid CanonicalEvseId);
public sealed record BoundingBox(double MinLongitude, double MinLatitude, double MaxLongitude, double MaxLatitude); // validiert, Contains(GeoPoint)
```

### Ingest-Modell (bewusst flach, kein Domain-Objekt)

```csharp
public sealed record ProviderLocationSnapshot(
    string ExternalId, string? Name, ProviderAddress? Address, GeoPoint Coordinates,
    string? OperatorName, string? OperatorId,     // z. B. "EVN" (aus EVSE-ID-Präfix oder Quelle)
    AccessType? Access, ProviderOpeningTimes? OpeningTimes,
    IReadOnlyList<ProviderEvseSnapshot> Evses,
    DateTimeOffset ObservedAt,
    JsonElement Raw);                             // Rohdatensatz für raw_snapshot

public sealed record ProviderAddress(string? Street, string? PostalCode, string? City, string Country);
public sealed record ProviderOpeningTimes(bool TwentyFourSeven, string? Description);

public sealed record ProviderEvseSnapshot(
    string ExternalId,
    string? EvseId,                               // unnormalisiert, wie geliefert
    string? PhysicalReference,                    // Aufdruck an der Säule, z. B. "A2"
    string? StationExternalId,                    // Säulen-Ebene, falls vorhanden
    decimal? MaxPowerKw, PowerType? PowerType,
    IReadOnlyList<ProviderConnectorSnapshot> Connectors,
    EvseStatus? Status, string? StatusRaw);       // falls die Inventar-Antwort Status mitliefert

public sealed record ProviderConnectorSnapshot(
    ConnectorStandard Standard, ConnectorFormat Format, PowerType PowerType,
    decimal? MaxPowerKw, int? MaxVoltage, int? MaxAmperage, string? RawType);

public sealed record ProviderEvseStatus(
    string ExternalId, string? EvseId, Guid? CanonicalEvseId,
    EvseStatus Status, string StatusRaw,
    DateTimeOffset ObservedAt,            // wann wir es gesehen haben
    DateTimeOffset? SourceReportedAt);    // Zeitstempel der Quelle, falls vorhanden

public sealed record ProviderPrice(
    string ExternalId, string? EvseId, Guid? CanonicalEvseId,
    string? EmpKey,                       // null = Ad-hoc
    string Currency,
    decimal? PricePerKwh, decimal? PricePerMinute, decimal? SessionFee,
    decimal? BlockingFeePerMinute, int? BlockingAfterMinutes,
    JsonElement? TariffElements,          // OCPI TariffElement[] als JSON, wenn vollständig ableitbar
    string? TariffName, string? TariffUrl,
    DateTimeOffset ObservedAt, JsonElement Raw);

public sealed record ProviderMappingIssue(string ProviderKey, string? ExternalId, string Problem);
```

### Status-Mapping

`StatusMapping` ist die Tabelle Quell-String → OCPI-`EvseStatus`. Jeder Adapter liefert sie als JSON-Datei (`{"frei": "AVAILABLE", "besetzt": "CHARGING", …}`), geladen mit `StatusMapping.FromJson`; die Einstellung `status_mapping` des Providers ergänzt oder überschreibt Einträge (`WithOverrides`). Die Suche ignoriert Groß-/Kleinschreibung und umgebende Leerzeichen; unbekannte Strings ergeben `UNKNOWN`. Ziele müssen OCPI-Statusnamen sein (keine Zahlen, keine Kleinschreibung), sonst wirft das Laden.

## Regeln für Adapter

1. **Nie werfen bei Einzelfehlern.** Ein kaputter Datensatz wird als `ProviderMappingIssue` geloggt und übersprungen; der Stream läuft weiter.
2. **Alles, was unbekannt ist, bleibt `null` oder `UNKNOWN`** – nicht raten. Insbesondere Status: Ein nicht mappbarer Status-String wird `UNKNOWN` mit `StatusRaw` gefüllt; der Admin sieht neue Strings in einer Liste und erweitert das Mapping.
3. **Status-Mapping ist eine Tabelle, kein `switch` im Code** (JSON im Adapter: `"frei" → AVAILABLE`, `"besetzt" → CHARGING`, `"reserviert" → RESERVED`, `"Störung" → OUTOFORDER`, …), damit Nicht-Entwickler Pull Requests machen können.
4. **EVSE-IDs werden roh weitergegeben**; Normalisierung passiert zentral im `EvseId` Value Object.
5. **Keine Persistenz, keine Domain-Objekte im Adapter.**
6. **Rate-Limit und Backoff sind Pflicht** und kommen aus dem Descriptor; der Worker setzt sie durch (`System.Threading.RateLimiting` + Polly `429`/`503`-Handling mit `Retry-After`).
7. **Fixtures**: Jeder Adapter bringt mindestens eine aufgezeichnete, sanitisierte Antwort je Endpunkt mit (`fixtures/<key>/…json`) und einen Replay-Test, der daraus das Ingest-Modell erzeugt und gegen ein erwartetes Snapshot-JSON vergleicht (Verify.Xunit). Kein automatisierter Test spricht mit der echten Quelle.
8. **Health-Check**: `IProviderHealth.ProbeAsync()` (ein billiger Request), damit Ausfälle und Format-Änderungen sofort auffallen.

## Generische Adapter

Drei Formate kommen so oft vor, dass sie je einen generischen, konfigurierbaren Adapter bekommen:

| Adapter | Für | Konfiguration |
|---|---|---|
| `Providers.Ocpi` | OCPI 2.2.1 `Locations`/`Tariffs` (CH-Open-Data, Partner-Feeds, manche NAPs) | Base-URL, Token, Versions-Endpoint |
| `Providers.DatexII` | DATEX II EnergyInfrastructure (NAPs DE/AT/IT) | Pull-URL, Credentials, Namespace-Version |
| `Providers.GenericJsonMap` | Web-Karten, die einfach `GET …?bbox=…` → JSON-Liste liefern | JSON-Pfade (`$.stations[*]`, `$.lat`, …), Status-Mapping – für einfache Quellen ohne eigenes C#-Projekt |

## Registrierung & Konfiguration

Adapter registrieren sich im Code via `services.AddProvider<EvnProvider>()`; der `IProviderRegistry` löst nach Key auf. Capabilities bestimmen automatisch, welche Jobtypen der Scheduler für den Provider anlegt.

Alle Laufzeit-Einstellungen eines Providers liegen im **zentralen Config-Service** (siehe [02](02-architektur.md#zentraler-config-service-application--infrastructure--api--web)), nicht in `appsettings.json` oder `.env`. Jeder Adapter liefert dafür seine `SettingDefinition`s mit Defaults aus dem Descriptor:

| Key (Scope `provider:<key>`) | Typ | Default aus Descriptor | Beschreibung |
|---|---|---|---|
| `enabled` | bool | `false` | Provider aktiv |
| `regions` | string[] | alle Scope-Regionen | Regionen, für die gecrawlt wird |
| `interval.inventory` / `interval.status` / `interval.prices` | TimeSpan | `PollDefaults` | Basisintervalle |
| `rate_limit.per_minute` / `rate_limit.burst` / `rate_limit.per_day` | int | `RateLimitPolicy` | clusterweites Budget |
| `secret.api_key` | secret | – | verschlüsselt gespeichert, nur über Admin-UI/API setzbar, nie lesbar zurückgegeben |
| `status_mapping` | map | aus Adapter-JSON | überschreibbares Status-Mapping |

Änderungen gelten ohne Neustart in allen Crawler-/Worker-Instanzen.

## Checkliste „neuen Provider anbinden“

1. Quelle in [07](07-bestandsanalyse-apps-at.md) / [08](08-datenquellen-und-recht.md) dokumentieren (Endpunkte, Auth, Lizenz, Rate-Limit, Capabilities).
2. Projekt `ChargeGlobe.Providers.<Key>` anlegen, Raw-DTOs aus 2–3 echten Antworten ableiten.
3. Descriptor + Mapper + Status-Mapping-Tabelle.
4. Fixtures + Replay-Test, Health-Probe.
5. Lokal gegen Region laufen lassen, `source_conflict`-Queue prüfen (typisch: Koordinaten-Offsets, Stecker-Freitext).
6. Konfiguration + Attribution-Text in `source`-Tabelle; Eintrag in `docs/07` aktualisieren.
