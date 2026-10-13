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

```csharp
public interface IProvider
{
    ProviderDescriptor Descriptor { get; }
}

public sealed record ProviderDescriptor(
    string Key,                       // "econtrol", "ocm", "osm", "evn", "oeamtc", "tesla", ...
    string DisplayName,
    SourceTier Tier,                  // Official, OpenData, PublicWeb, AppBackend, Partner
    ProviderCapabilities Capabilities,
    string License,                   // z. B. "CC BY 4.0", "ODbL", "ToS – nur Anzeige"
    string AttributionText,
    IReadOnlyList<string> Countries,  // ISO 3166-1 alpha-2
    PollDefaults PollDefaults,        // Inventar täglich, Status 5 min, Preise 24 h ...
    RateLimitPolicy RateLimit);       // z. B. 60 req/min, Burst 10

[Flags]
public enum ProviderCapabilities
{
    None               = 0,
    StaticInventory    = 1 << 0,  // liefert Standorte/EVSEs/Stecker
    EvseIds            = 1 << 1,  // liefert EVSE-IDs (entscheidend für Matching)
    LiveStatusPerEvse  = 1 << 2,  // Status je Ladepunkt
    LiveStatusPerLocation = 1 << 3, // nur "x von y frei"
    AdHocPrice         = 1 << 4,  // CPO-Ad-hoc-Preis
    EmpTariff          = 1 << 5,  // Preis der eigenen Ladekarte/App
    BulkByBoundingBox  = 1 << 6,  // Abfrage per bbox/Tile möglich (billig)
    BulkAll            = 1 << 7,  // ein Request = alles (Export)
    ByEvseId           = 1 << 8,  // Einzelabfrage je Ladepunkt (teuer)
    DeltaSince         = 1 << 9,  // "geändert seit" wird unterstützt
    Ocpi               = 1 << 10, // echtes OCPI → generischer Adapter
}

/// Inventar (Stammdaten).
public interface IInventorySource : IProvider
{
    IAsyncEnumerable<ProviderLocationSnapshot> FetchInventoryAsync(
        InventoryQuery query, CancellationToken ct);
}

/// Belegt-Status. Der Adapter wählt selbst den billigsten Weg (bbox > bulk > ids).
public interface IStatusSource : IProvider
{
    IAsyncEnumerable<EvseStatusObservation> FetchStatusAsync(
        StatusQuery query, CancellationToken ct);
}

/// Preise – Ad-hoc und/oder EMP-Tarife.
public interface IPriceSource : IProvider
{
    IAsyncEnumerable<PriceObservation> FetchPricesAsync(
        PriceQuery query, CancellationToken ct);
}

public sealed record InventoryQuery(BoundingBox? Bbox, string? Country, DateTimeOffset? Since);
public sealed record StatusQuery(BoundingBox? Bbox, IReadOnlyCollection<ProviderEvseRef>? Evses);
public sealed record PriceQuery(BoundingBox? Bbox, IReadOnlyCollection<ProviderEvseRef>? Evses);

/// Verweis auf einen Ladepunkt aus Sicht des Providers (Quell-ID + ggf. EVSE-ID).
public sealed record ProviderEvseRef(string ExternalId, string? EvseId, Guid CanonicalEvseId);
```

### Ingest-Modell (bewusst flach, kein Domain-Objekt)

```csharp
public sealed record ProviderLocationSnapshot(
    string ExternalId,
    string? Name,
    Address? Address,
    GeoPoint Coordinates,
    string? OperatorName, string? OperatorId,     // z. B. "EVN" (aus EVSE-ID-Präfix oder Quelle)
    AccessType? Access,
    OpeningTimes? OpeningTimes,
    IReadOnlyList<ProviderEvseSnapshot> Evses,
    DateTimeOffset ObservedAt,
    JsonElement Raw);                              // Rohdatensatz für raw_snapshot

public sealed record ProviderEvseSnapshot(
    string ExternalId,
    string? EvseId,                                // unnormalisiert, wie geliefert
    string? PhysicalReference,                     // Aufdruck an der Säule, z. B. "A2"
    string? StationExternalId,                     // Säulen-Ebene, falls vorhanden
    decimal? MaxPowerKw,
    CurrentType? CurrentType,
    IReadOnlyList<ProviderConnectorSnapshot> Connectors,
    EvseStatus? Status,                            // falls die Inventar-Antwort Status mitliefert
    string? StatusRaw);

public sealed record ProviderConnectorSnapshot(
    ConnectorStandard Standard, ConnectorFormat Format, PowerType PowerType,
    decimal? MaxPowerKw, int? MaxVoltage, int? MaxAmperage, string? RawType);

public sealed record EvseStatusObservation(
    string ExternalId, string? EvseId, Guid? CanonicalEvseId,
    EvseStatus Status, string StatusRaw,
    DateTimeOffset ObservedAt,            // wann wir es gesehen haben
    DateTimeOffset? SourceReportedAt);    // Zeitstempel der Quelle, falls vorhanden

public sealed record PriceObservation(
    string ExternalId, string? EvseId, Guid? CanonicalEvseId,
    string? EmpKey,                        // null = Ad-hoc
    string Currency,
    decimal? PricePerKwh, decimal? PricePerMinute, decimal? SessionFee,
    decimal? BlockingFeePerMinute, int? BlockingAfterMinutes,
    IReadOnlyList<TariffElement>? TariffElements,   // OCPI, wenn vollständig ableitbar
    string? TariffName, string? TariffUrl,
    DateTimeOffset ObservedAt, JsonElement Raw);
```

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
