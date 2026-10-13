# 02 – Architektur (technisches Konzept)

## Leitentscheidungen

| Entscheidung | Wahl | Begründung |
|---|---|---|
| Deployment-Form | **Modularer Monolith** (eine Solution, eine DB; Prozesse `api`, `crawler` ×N, `worker` ×M) | Open-Source-Projekt mit kleinem Team; Microservices erst, wenn ein Modul nachweislich eigene Skalierung braucht ([ADR-0002](adr/0002-modularer-monolith.md)) |
| Backend | **.NET 10 (LTS) / C# 14**, ASP.NET Core, EF Core 10 + Npgsql | vom Team vorgegeben; Npgsql hat erstklassigen PostGIS-/JSONB-Support; eingebaute OpenAPI-3.1-Generierung |
| Datenbank | **PostgreSQL 17 + PostGIS + TimescaleDB** | Geo-Abfragen (bbox, Umkreis) und Zeitreihen (Status-Historie) in einer DB ([ADR-0003](adr/0003-timescaledb-fuer-historie.md)) |
| Frontend | **Angular 22** (Standalone Components, Signals, Zoneless), MapLibre GL JS | vom Team vorgegeben; MapLibre ist OSS und vendor-frei |
| Identität & Zugriff | **ASP.NET Core Identity** (EF Core, Postgres) + **feingranulare Permissions**, Rollen als Berechtigungsbündel; JWT für die SPA, **API-Keys** für Agenten/Integrationen; Lesezugriff anonym | eine Autorisierungsschicht für Menschen und Agenten ([ADR-0005](adr/0005-identity-rbac-openapi.md)) |
| API-Vertrag | **OpenAPI 3.1** als maschinenlesbarer Vertrag, auf Agenten-Nutzung ausgelegt | Agenten sollen die API ohne weitere Doku bedienen können ([ADR-0005](adr/0005-identity-rbac-openapi.md)) |
| Kanonisches Datenmodell | an **OCPI 2.2.1** angelehnt (Location/EVSE/Connector/Tariff) | Branchenstandard, deckt Status-Enums, Steckertypen und Tarifelemente ab; viele Quellen liefern OCPI-nahe Daten ([ADR-0004](adr/0004-ocpi-als-kanonisches-modell.md)) |
| Identität | **EVSE-ID** als fachlicher Schlüssel, interne UUIDs als technischer Schlüssel | quellenübergreifend auffindbar ([ADR-0001](adr/0001-evse-id-als-identitaet.md)) |
| Provider-Anbindung | **Ports & Adapters** mit Anti-Corruption-Layer je Quelle | Quellformate dürfen niemals ins Domänenmodell durchschlagen |
| Scheduling | eigener leichtgewichtiger Scheduler (`PeriodicTimer` + `Channel<T>`), Poll-Pläne und Leases in Postgres | Hangfire/Quartz sind für 10⁴–10⁵ Jobs/Minute zu schwergewichtig; wir brauchen Bulk-Polling pro Provider, nicht Job-pro-EVSE |
| Skalierung | getrennte, zustandslose `crawler`- und `worker`-Container, Anzahl per `.env` (`CRAWLER_REPLICAS`, `WORKER_REPLICAS`), Verteilung über Postgres-Leases/Queues | horizontale Skalierung ohne Broker; Compose genügt ([ADR-0006](adr/0006-konfiguration-env-minimal-config-service.md)) |
| Konfiguration | `.env` nur Infrastruktur/Bootstrap (vollständig in `.env.example`); alles Fachliche im **zentralen Config-Service** (Katalog im Code, Werte in `app_setting`, Admin-UI/API) | minimale Betriebs-Konfiguration, Änderungen ohne Neustart ([ADR-0006](adr/0006-konfiguration-env-minimal-config-service.md)) |

## Systemübersicht

```
                 ┌──────────────────────────────────────────────────────────────────────┐
                 │                            Quellen                                    │
                 │  Tier 0: E-Control Ladestellenverzeichnis · NAP (AFIR/DATEX II)        │
                 │          OpenChargeMap · OpenStreetMap                                │
                 │  Tier 1: Web-Karten der CPOs/EMPs (XHR-Endpunkte)                     │
                 │  Tier 2: App-Backends (nach Analyse)        Tier 3: OCPI-Partnerfeeds  │
                 └───────────────┬──────────────────────────────────────────────────────┘
                                 │ HTTP (Polly: Retry, Circuit Breaker, Rate Limiter)
┌────────────────────────────────▼──────────────────────────┐  ┌────────────────────────┐
│ crawler ×N (ChargeGlobe.Crawler)                           │  │ worker ×M              │
│  ┌───────────────┐  ┌──────────────────┐  ┌─────────────┐ │  │ (ChargeGlobe.Worker)   │
│  │ Lease-Scheduler│→│ Provider-Adapter │→ │ ACL / Mapper│ │  │  Ingest-Queue →        │
│  │ (poll_plan,    │  │ (IInventorySource│  │ (Raw→Ingest-│─┼─▶│  Identity Resolution   │
│  │  SKIP LOCKED)  │  │  IStatusSource   │  │  Modell)    │ │  │  Merge (Precedence)    │
│  │               │  │  IPriceSource)   │  │             │ │  │  Change Detection      │
│  └───────────────┘  └──────────────────┘  └─────────────┘ │  │  Aggregate/Reconcile   │
└───────────────────────────┬───────────────────────────────┘  └──────────┬─────────────┘
                            │ raw_snapshot + ingest_queue                  │
                           ┌───────────────────────────────────────────────▼──────────────┐
                           │ PostgreSQL 17 · PostGIS · TimescaleDB                        │
                           │  canonical (location, evse, connector, operator, tariff …)   │
                           │  provenance (source, source_record, attribute_origin)        │
                           │  timeseries (evse_status_observation, evse_status_interval,  │
                           │              price_observation) · raw_snapshot (JSONB)       │
                           └───────────────────────────────▲──────────────────────────────┘
                                                           │
┌──────────────────────────────────────────────────────────┴─────────────────────────────┐
│ ChargeGlobe.Api (ASP.NET Core)  ·  REST /api/v1  ·  OpenAPI  ·  Output-Cache  ·  ETag   │
│  optional: Vector-Tiles (pg_tileserv/Martin) für die Karte                              │
└──────────────────────────────────────────────────────────▲─────────────────────────────┘
                                                           │
                           ┌───────────────────────────────┴──────────────────────────────┐
                           │ web (Angular 22 + MapLibre): Karte · Ladepunkt-Detail         │
                           │ Belegungshistorie · Preisvergleich · Provenienz · Admin       │
                           └──────────────────────────────────────────────────────────────┘
```

## Module (Projekte in der Solution)

### `ChargeGlobe.Domain`
Reine C#-Klassen ohne Infrastruktur-Abhängigkeiten.

- Entities/Aggregates: `Location`, `ChargingStation`, `Evse`, `Connector`, `Operator` (CPO), `Emp` (Ladekarten-/App-Anbieter), `Tariff`, `Region`.
- Value Objects: `EvseId` (Parsing, Normalisierung, Ländercode, Operator-ID, Validierung), `GeoPoint`, `PowerKw`, `Money`, `ConnectorStandard` (Enum nach OCPI), `EvseStatus` (Enum nach OCPI: `AVAILABLE`, `BLOCKED`, `CHARGING`, `INOPERATIVE`, `OUTOFORDER`, `PLANNED`, `REMOVED`, `RESERVED`, `UNKNOWN`).
- Domain-Events: `EvseStatusChanged`, `EvseDiscovered`, `PriceChanged`, `SourceConflictDetected`.

### `ChargeGlobe.Providers.Abstractions`
Der Vertrag, den jeder Quell-Adapter erfüllt (ausführlich in [04](04-provider-interface-acl.md)). Enthält **nur** Interfaces, Capability-Flags und das *Ingest-Modell* (`ProviderLocationSnapshot`, `EvseStatusObservation`, `PriceObservation`) – ein bewusst von den Domain-Entities getrenntes DTO-Modell, damit Adapter keine Persistenz-Details kennen.

### `ChargeGlobe.Providers.<Quelle>`
Ein Projekt je Quelle (z. B. `EControl`, `OpenChargeMap`, `Osm`, `Evn`, `OeamtcEpower`, `Tesla`, …). Enthält: HTTP-Client (typed `HttpClient` + Polly), Roh-DTOs der Quelle, Mapper auf das Ingest-Modell, `ProviderDescriptor` mit Capabilities und Standard-Pollintervallen, aufgezeichnete Fixtures für Replay-Tests. Adapter werden via `IProviderRegistry` per Konfiguration aktiviert.

### `ChargeGlobe.Application`
Use Cases und die Ingest-Pipeline:

1. **Snapshot-Persistenz**: Jede Rohantwort wird (komprimiert, JSONB, mit Hash) in `raw_snapshot` abgelegt → Replay, Debugging, spätere Re-Normalisierung bei Mapper-Fixes.
2. **Identity Resolution** (`IEvseMatcher`): Zuordnung eines Quell-Datensatzes zu einem kanonischen EVSE (Details in [03](03-datenmodell.md#identity-resolution)).
3. **Merge** (`IAttributeMerger`): Attribut-weise Zusammenführung nach Quellen-Präzedenz, mit Aufzeichnung der Herkunft je Attribut (`attribute_origin`).
4. **Change Detection**: Nur Änderungen erzeugen Status-Intervalle/Events; jede Beobachtung landet trotzdem in der Observation-Hypertable (für Frische-Metriken).
5. **Conflict Queue**: Unlösbare Zuordnungen (zwei Quellen behaupten verschiedene Koordinaten > 150 m, unterschiedliche Steckerzahl, …) landen in `source_conflict` zur manuellen Prüfung im Admin-UI.

### Identity & Access (in Domain, Application, Infrastructure, Api)
- **Domain**: `Permission`-Katalog als statische Liste von Strings nach dem Muster `<ressource>:<aktion>` (z. B. `evse:read`, `tariff:write`, `conflict:resolve`, `provider:manage`, `user:manage`, `role:manage`, `apikey:manage`, `export:read`). Neue Permissions werden hier ergänzt und sind damit automatisch in OpenAPI, UI und Datenbank bekannt.
- **Application**: `IAuthorizationService`-Policies, die ausschließlich Permissions prüfen; Use Cases für Nutzer, Rollen, API-Keys; Audit-Log für alle schreibenden Aktionen (wer, wann, was, über welchen Key).
- **Infrastructure**: ASP.NET Core Identity (`AspNetUsers`, `AspNetRoles`) + eigene Tabellen `role_permission`, `user_permission` (direkte Zusatzrechte), `api_key` (nur Hash gespeichert, Permissions-Teilmenge des Erstellers, Ablaufdatum, letzter Zugriff).
- **Api**: zwei Authentifizierungsschemata – `Bearer` (JWT, Login der SPA) und `ApiKey` (Header `X-Api-Key`) – die beide in dieselben Permission-Claims münden; Endpunkt-Attribut `[RequirePermission("…")]`, das zugleich das OpenAPI-Feld `x-required-permission` setzt.
- **Standardrollen** (editierbar): `Admin` (alles), `Maintainer` (Tarife, Konflikte, Provider-Konfiguration), `Analyst` (Exports, Rohbeobachtungen), `Agent` (lesen + Tarif-/Konfliktvorschläge einreichen), `Viewer` (nur das, was ohnehin anonym lesbar ist, plus höhere Rate-Limits).

### `ChargeGlobe.Infrastructure`
EF Core DbContext, Migrationen, PostGIS-Mapping (NetTopologySuite), Timescale-Hypertables via Raw-SQL-Migrationen, Repositories, Outbox für Domain-Events, Identity-Stores.

### `ChargeGlobe.Crawler` (Container `crawler`, N Instanzen)
Generic Host, zustandslos, holt Daten von den Quellen:

- **Lease-Scheduler**: `poll_plan` (Provider × Region/Kachel × Jobtyp × Intervall × `next_run_at`) liegt in Postgres. Jede Instanz holt fällige Pläne mit `SELECT … FOR UPDATE SKIP LOCKED`, setzt `leased_by`/`lease_until` und führt sie aus; abgelaufene Leases werden von jeder Instanz übernommen. Damit verteilen sich N Instanzen ohne Koordinator.
- **Rate-Limits je Provider sind clusterweit**: Token-Bucket-Zustand in Postgres (`provider_rate_budget`), nicht pro Prozess, damit N Crawler zusammen das Limit einhalten.
- **Jobtypen**: `InventoryFull`, `InventoryDelta`, `StatusPoll`, `PricePoll` (siehe [05](05-crawling-konzept.md)).
- Ergebnis jedes Jobs: `raw_snapshot` + bereits gemapptes Ingest-Modell als Nachricht in `ingest_queue` (Postgres-Tabelle, JSONB). Crawler schreiben nie in kanonische Tabellen.
- Health/Metriken je Provider und Instanz (OpenTelemetry → Prometheus/Grafana): Requests, Fehlerquote, 429s, Datenfrische, Lease-Alter.

### `ChargeGlobe.Worker` (Container `worker`, M Instanzen)
Generic Host, zustandslos, verarbeitet `ingest_queue` (ebenfalls `SKIP LOCKED`, Batches je Provider):

- Identity Resolution, Merge, Change Detection, Status-Intervalle, Konflikt-Queue (siehe `Application`).
- Periodische Aufgaben mit Single-Runner-Lease (`job_lease`-Tabelle): `Reconcile`, Continuous-Aggregate-Refresh, Retention, Statistiken, Permission-/Settings-Katalog-Abgleich.
- **Adaptive Intervalle**: schreibt nach jedem Statuswechsel das neue Intervall in `poll_plan` zurück; der Crawler liest es beim nächsten Lease.

Beide Hosts teilen sich das Image `chargeglobe-jobs` und werden über `--role crawler|worker` gestartet; die Anzahl steht in `.env` (`CRAWLER_REPLICAS`, `WORKER_REPLICAS`) und wird von Compose per `deploy.replicas` umgesetzt. Eine Instanz je Rolle reicht für Phase 1.

### Zentraler Config-Service (Application + Infrastructure + Api + web)
- **Katalog im Code**: `SettingDefinition` je Einstellung (Key wie `providers.econtrol.status_interval`, Typ, Default, Beschreibung, Validierung, Scope `global | provider:<key> | region:<code>`), analog zum Permission-Katalog. Der Katalog ist die vollständige Liste fachlicher Einstellungen.
- **Werte in `app_setting`** (Key, Scope, `value jsonb`, `updated_by`, `updated_at`); fehlender Wert = Default aus dem Katalog.
- **Zugriff** über typisierte Accessoren (`IAppConfig.Get<T>(key, scope)`), In-Memory-Cache je Prozess, Invalidierung über Postgres `LISTEN/NOTIFY` (Fallback: Polling alle 30 s) → Änderungen wirken in allen Containern ohne Neustart.
- **Pflege** über `GET/PUT /settings` (`settings:manage`) und Admin-UI (generisch aus dem Katalog gerendert); Änderungen landen im Audit-Log.
- **Nicht im Config-Service** (gehört in `.env`): alles, was vor dem DB-Zugriff bekannt sein muss oder die Infrastruktur betrifft.

### `ChargeGlobe.Api`
ASP.NET Core Minimal APIs, Versionierung `/api/v1`, OpenAPI 3.1 (eingebaute Generierung, Scalar UI), `Microsoft.AspNetCore.RateLimiting`, Output-Caching mit kurzen TTLs für Karten-Queries, ETag/`If-None-Match`, CORS, Problem Details (RFC 9457). Lesezugriffe anonym; schreibende und administrative Endpunkte über JWT oder API-Key mit Permission-Policies. Die OpenAPI-Beschreibung ist so vollständig (operationId, Beschreibungen, Beispiele, Security Schemes, Permission-Hinweise), dass ein KI-Agent sie als einzige Dokumentation braucht. Details in [06](06-api.md).

### `web` (Angular)
- Karte (MapLibre, Clustering, Status-Farbcodierung, Filter: Stecker, Leistung, Betreiber, nur frei).
- Ladepunkt-Detail: aktueller Status + Alter der Beobachtung, Quellen, Belegungs-Timeline (letzte 7 Tage), Heatmap Wochentag × Stunde, Preistabelle über EMPs (Ad-hoc, ÖAMTC, EVN, Electroverse, …) mit Beispielrechnung (z. B. 30 kWh / 40 min).
- Provenienz-Ansicht: welches Attribut kommt woher, wann zuletzt bestätigt.
- Login (JWT) und Admin-Bereich, sichtbar nach Permissions: Provider-Health, Konflikt-Queue, Match-Review, Tarif-Editor (für Tarife, die nur in PDFs/Webseiten stehen), Nutzer- und Rollenverwaltung, API-Key-Verwaltung (Key wird genau einmal angezeigt), Audit-Log.

## Infrastruktur & Betrieb

- **Ein Compose-Stack für alles**: `docker compose up -d` startet `db` (`timescale/timescaledb-ha:pg17`, enthält PostGIS), `api`, `crawler` (×N), `worker` (×M) und `web` (Angular-Build, statisch ausgeliefert; `/api` wird containerintern an `api` weitergereicht).
- **`.env` minimal, aber vollständig dokumentiert**: `.env.example` enthält jede verfügbare Variable mit Default und Kommentar. Vorgesehener Umfang:

  | Variable | Default | Zweck |
  |---|---|---|
  | `CHARGEGLOBE_PORT` | `5005` | exponierter HTTP-Port (einzige Oberkante) |
  | `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` | `chargeglobe`, `chargeglobe`, – | DB-Zugang (Container-intern) |
  | `JWT_SIGNING_KEY` | – | Secret für Access-Tokens |
  | `ADMIN_EMAIL`, `ADMIN_INITIAL_PASSWORD` | – | Seed des ersten Admin-Users (nur beim ersten Start) |
  | `CRAWLER_REPLICAS`, `WORKER_REPLICAS` | `1`, `1` | Anzahl Instanzen |
  | `LOG_LEVEL` | `Information` | Serilog-Minimum |
  | `PUBLIC_BASE_URL` | `http://localhost:5005` | für Links in API-Antworten/E-Mails |

  Provider-Keys (z. B. E-Control-API-Key) werden **nicht** in `.env` gepflegt, sondern verschlüsselt im Config-Service (Data-Protection-API, Schlüssel im `db`-Volume), damit `.env` klein bleibt und Keys über das Admin-UI rotiert werden können.
- **Oberkante der App ist ein HTTP-Port** (`CHARGEGLOBE_PORT`, Default `5005`), unter dem Web-App und `/api` erreichbar sind. TLS, Reverse Proxy, Caddy oder Let's Encrypt sind **nicht** Teil der App; das übernimmt eine getrennte Komponente vor dem Port. Ein Snakeoil-Zertifikat ist zulässig, falls ein Service eines verlangt, aber nicht vorgesehen.
- **Server**: Dev-Deployment auf einem Debian-Host mit Docker unter `/opt/chargeglobe` (Zugangsdaten in `CLAUDE.local.md`, nicht im Repo). Andere Dienste auf dem Host werden nicht berührt; eigener Compose-Projektname, nur der eine Port wird exponiert.
- Seed-Skript lädt Region-Polygone und den ersten E-Control-Import.
- **Qualitätssicherung**: keine GitHub-CI (nicht im Scope). Vor jedem Merge laufen lokal Build, Unit-Tests, Provider-Replay-Tests, Integrationstests (Testcontainers) und Angular-Tests; Ziel 100 % Statement Coverage (siehe `CLAUDE.md`, Workflow). Docker-Images werden beim Deploy auf dem Server gebaut.
- **Konfiguration**: Infrastruktur über `.env` (siehe oben), alles Fachliche über den Config-Service; `appsettings.json` enthält nur statische Framework-Defaults (Logging-Format, Kestrel) und keine Umgebungswerte.
- **Observability**: Serilog (strukturiert), OpenTelemetry Traces/Metrics, Dashboards als Code (`deploy/grafana`).
- **Backups**: `pg_dump` der kanonischen Tabellen + Timescale-Compression/Retention-Policies (Observations 180 Tage roh, Intervalle unbegrenzt, Raw-Snapshots 30 Tage).

## Skalierungsabschätzung (Phase 1)

| Größe | Annahme | Folge |
|---|---|---|
| Ladepunkte Ost-Österreich | ~12.000–18.000 | Inventar trivial |
| Statusbeobachtungen | 15.000 EVSE × 288/Tag (5 min) ≈ 4,3 Mio Zeilen/Tag | Hypertable, Compression nach 7 Tagen; ~10 GB/Monat roh, komprimiert ~1 GB |
| Status-Intervalle | ~5–10 Wechsel/Tag/EVSE ≈ 150.000 Zeilen/Tag | unproblematisch |
| HTTP-Requests | bbox/bulk-Endpunkte: wenige hundert Requests/Minute gesamt; per-ID-Endpunkte: bis 3.000/min für einen Provider → nur mit adaptivem Polling | Rate-Limiter je Provider zwingend |

Bei DACH-Ausweitung (≈ 200.000 Ladepunkte) bleibt die Architektur gleich; `CRAWLER_REPLICAS`/`WORKER_REPLICAS` werden erhöht, die Lease-Verteilung übernimmt den Rest.
