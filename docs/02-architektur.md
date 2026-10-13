# 02 – Architektur (technisches Konzept)

## Leitentscheidungen

| Entscheidung | Wahl | Begründung |
|---|---|---|
| Deployment-Form | **Modularer Monolith** (eine Solution, eine DB; Prozesse `api`, `crawler` ×N, `worker` ×M) | Open-Source-Projekt mit kleinem Team; Microservices erst, wenn ein Modul nachweislich eigene Skalierung braucht ([ADR-0002](adr/0002-modularer-monolith.md)) |
| Backend | **.NET 10 (LTS) / C# 14**, ASP.NET Core, EF Core 10 + Npgsql | vom Team vorgegeben; Npgsql hat erstklassigen PostGIS-/JSONB-Support; eingebaute OpenAPI-3.1-Generierung |
| Datenbank | **PostgreSQL 17 + PostGIS + TimescaleDB** | Geo-Abfragen (bbox, Umkreis) und Zeitreihen (Status-Historie) in einer DB ([ADR-0003](adr/0003-timescaledb-fuer-historie.md)) |
| Frontend | **Angular 22** (Standalone Components, Signals, Zoneless) | vom Team vorgegeben |
| Karte, Standort, Routing | **MapLibre GL JS** + OSM-Vektorkacheln (OpenFreeMap, Style-URL konfigurierbar), Browser-Geolocation, selbst gehostete Routing-Engine **Valhalla** hinter `POST /routes` | keine nutzungsabhängigen Kosten, kein API-Key, keine Anbieterbindung ([ADR-0007](adr/0007-karte-standort-routing.md)) |
| Mail | zentraler Mail-Service: Port `IEmailSender`, Outbox-Tabelle, SMTP-Versand im Worker, SMTP-Zugang im Config-Service | ein Weg für alle Mails, SMTP-Server ohne Neustart änderbar ([ADR-0008](adr/0008-zentraler-mail-service-smtp.md)) |
| Transport | **HTTPS-only** am exponierten Port mit dauerhaft self-signed Zertifikat, ohne HSTS; gültiges Zertifikat (und bei Bedarf HSTS) im externen Loadbalancer | Debugging im HTTPS-only-Modus, sicherer Kontext für Geolocation ([ADR-0009](adr/0009-https-only-self-signed.md)) |
| Identität & Zugriff | **ASP.NET Core Identity** (EF Core, Postgres) + **feingranulare Permissions**, Rollen als Berechtigungsbündel; JWT für die SPA, **API-Keys** für Agenten/Integrationen; anonym nur die Karte, alles Weitere für angemeldete Nutzer (Selbstregistrierung) | eine Autorisierungsschicht für Menschen und Agenten ([ADR-0005](adr/0005-identity-rbac-openapi.md)) |
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
│ crawler ×N (ChargeGlobe.Jobs --role crawler)               │  │ worker ×M              │
│  ┌───────────────┐  ┌──────────────────┐  ┌─────────────┐ │  │ (Jobs --role worker)   │
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

Die Solution `ChargeGlobe.sln` liegt im Repo-Root, Projekte unter `src/`, Testprojekte unter `tests/`: `ChargeGlobe.Domain.Tests`, `ChargeGlobe.Jobs.Tests`, `ChargeGlobe.Api.Tests` (Api, Application und Infrastructure ohne Datenbank) und `ChargeGlobe.IntegrationTests` (API in-process gegen Postgres: Migration, Seed, alle Identity-Abläufe, Vollständigkeit der OpenAPI-Beschreibung).

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
- **Domain** (`Identity/`): `Permissions` ist der statische Katalog (Key nach dem Muster `<ressource>:<aktion>` plus englische Beschreibung): `evse:read`, `price:read`, `route:read`, `tariff:write`, `conflict:resolve`, `provider:manage`, `user:manage`, `role:manage`, `apikey:manage`, `export:read`, `settings:read`, `settings:manage`, `jobs:read`, `audit:read`, `proposal:write`. `DefaultRoles` enthält die Standardrollen mit ihrem Default-Bündel. Eine hier ergänzte Permission ist automatisch in `GET /permissions`, OpenAPI und Datenbank bekannt.
- **Application**: `AuthSettings` (Bootstrap-Werte aus `.env`: `JWT_SIGNING_KEY`, `PUBLIC_BASE_URL`, `AUTH_REQUIRE_CONFIRMED_EMAIL`, `ADMIN_EMAIL`, `ADMIN_INITIAL_PASSWORD`; Lebensdauern: Access-Token 15 min, Refresh-Token 14 Tage, Mail-Token 24 h).
- **Infrastructure**: ASP.NET Core Identity mit Guid-Schlüsseln (`AspNetUsers`, `AspNetRoles`, …) und eigene Tabellen `role_permission`, `user_permission` (direkte Zusatzrechte), `api_key` (nur SHA-256-Hash, Prefix zur Anzeige, `owner_user_id`, Permission-Teilmenge, `expires_at`, `last_used_at`, `revoked_at`, `idempotency_key`), `refresh_token` (nur Hash, rotierend) und `audit_log` (`actor_user_id`, `actor_api_key_id`, `action`, `target`, `occurred_at`). Services unter `Identity/`: `AccountService` (Registrierung, Bestätigung, Login, Passwort-Reset), `SessionService` (Refresh-Token-Rotation; ein bereits benutztes Token beendet alle Sitzungen des Nutzers), `ApiKeyService`, `PermissionService` (effektive Permissions = Rollen ∪ Direktrechte), `AuditLog`. Tokens für E-Mail-Bestätigung und Passwort-Reset sind zufällig, einmalig verwendbar und nur als Hash gespeichert (`HashedTokenProvider`); Passwörter brauchen mindestens 10 Zeichen.
- **Api** (`Auth/`): zwei Authentifizierungsschemata – `Bearer` (JWT HS256, Login der SPA) und `ApiKey` (Header `X-Api-Key: cg_<prefix>_<secret>`) – die beide in dieselben `permission`-Claims münden. Ein API-Key wirkt mit der Schnittmenge aus seinen gespeicherten Permissions und den aktuellen Permissions seines Eigentümers. `RequirePermission("…")` (Attribut bzw. Erweiterungsmethode für Endpunkte und Gruppen) ist der einzige Weg, einen Endpunkt zu schützen, und setzt zugleich `x-required-permission` sowie die 401/403-Antworten in OpenAPI; `authenticated` steht für „beliebiges Konto“, Endpunkte ohne Attribut tragen `none`. Fehlgeschlagene Autorisierung wird als Problem Details beantwortet (403 nennt die fehlende Permission).
- **Start-Abgleich**: fehlende Standardrollen werden mit ihrem Default-Bündel angelegt; bestehende Rollen behalten ihre (ggf. geänderten) Permissions, nur `Admin` erhält immer den vollständigen Katalog. Der Admin-User aus `ADMIN_EMAIL`/`ADMIN_INITIAL_PASSWORD` wird nur angelegt, solange kein Nutzer existiert, mit bereits bestätigter E-Mail.
- **Standardrollen** (Bündel in der Datenbank, im Code nie namentlich geprüft): `Admin` (alles), `Maintainer` (Lesen, Tarife, Konflikte, Provider-Konfiguration, Einstellungen lesen, Jobs, eigene API-Keys), `Analyst` (Lesen, Exports, eigene API-Keys), `Agent` (Lesen + Vorschläge einreichen), `User` (Standardrolle nach Selbstregistrierung: `evse:read`, `price:read`, `route:read`, `apikey:manage`).
- **Vorgesehen**: Nutzer-/Rollen-CRUD und Admin-UI, `/me/preferences` und Favoriten, Rate-Limit-Tier je API-Key.

### `ChargeGlobe.Infrastructure`
EF Core 10 + Npgsql: `AppDbContext` (`Persistence/`), Migrationen unter `Persistence/Migrations` (anlegen mit `dotnet ef migrations add`, siehe `CLAUDE.md`), Identity-Stores, die Identity-Services und die Mail-Outbox. Die API wendet ausstehende Migrationen beim Start an und gleicht danach Rollen, Permissions und den Admin-Seed ab (`DatabaseInitializer`); ein `pg_advisory_lock` serialisiert parallele Starts. Vorgesehen: PostGIS-Mapping (NetTopologySuite), Timescale-Hypertables via Raw-SQL-Migrationen, Repositories, Outbox für Domain-Events.

### `ChargeGlobe.Jobs`
Ein Generic-Host-Projekt und ein Image (`chargeglobe-jobs`) für beide Hintergrund-Rollen. Die Rolle wird beim Start über `--role crawler|worker` gewählt (fehlende oder unbekannte Rolle bricht den Start ab); die Anzahl der Instanzen je Rolle steht in `.env` (`CRAWLER_REPLICAS`, `WORKER_REPLICAS`) und wird von Compose per `deploy.replicas` umgesetzt. Jede Instanz loggt beim Start und danach alle 60 s einen Heartbeat mit Rolle und Instanz-ID (Hostname des Containers). Eine Instanz je Rolle reicht für Phase 1.

#### Rolle `crawler` (Container `crawler`, N Instanzen)
Zustandslos, holt Daten von den Quellen:

- **Lease-Scheduler**: `poll_plan` (Provider × Region/Kachel × Jobtyp × Intervall × `next_run_at`) liegt in Postgres. Jede Instanz holt fällige Pläne mit `SELECT … FOR UPDATE SKIP LOCKED`, setzt `leased_by`/`lease_until` und führt sie aus; abgelaufene Leases werden von jeder Instanz übernommen. Damit verteilen sich N Instanzen ohne Koordinator.
- **Rate-Limits je Provider sind clusterweit**: Token-Bucket-Zustand in Postgres (`provider_rate_budget`), nicht pro Prozess, damit N Crawler zusammen das Limit einhalten.
- **Jobtypen**: `InventoryFull`, `InventoryDelta`, `StatusPoll`, `PricePoll` (siehe [05](05-crawling-konzept.md)).
- Ergebnis jedes Jobs: `raw_snapshot` + bereits gemapptes Ingest-Modell als Nachricht in `ingest_queue` (Postgres-Tabelle, JSONB). Crawler schreiben nie in kanonische Tabellen.
- Health/Metriken je Provider und Instanz (OpenTelemetry → Prometheus/Grafana): Requests, Fehlerquote, 429s, Datenfrische, Lease-Alter.

#### Rolle `worker` (Container `worker`, M Instanzen)
Zustandslos, verarbeitet `ingest_queue` (ebenfalls `SKIP LOCKED`, Batches je Provider):

- Identity Resolution, Merge, Change Detection, Status-Intervalle, Konflikt-Queue (siehe `Application`).
- Periodische Aufgaben mit Single-Runner-Lease (`job_lease`-Tabelle): `Reconcile`, Continuous-Aggregate-Refresh, Retention, Statistiken, Permission-/Settings-Katalog-Abgleich.
- **Adaptive Intervalle**: schreibt nach jedem Statuswechsel das neue Intervall in `poll_plan` zurück; der Crawler liest es beim nächsten Lease.

### Zentraler Config-Service (Application + Infrastructure + Api + web)
- **Katalog im Code**: `SettingDefinition` je Einstellung (Key wie `providers.econtrol.status_interval`, Typ, Default, Beschreibung, Validierung, Scope `global | provider:<key> | region:<code>`), analog zum Permission-Katalog. Der Katalog ist die vollständige Liste fachlicher Einstellungen.
- **Werte in `app_setting`** (Key, Scope, `value jsonb`, `updated_by`, `updated_at`); fehlender Wert = Default aus dem Katalog.
- **Zugriff** über typisierte Accessoren (`IAppConfig.Get<T>(key, scope)`), In-Memory-Cache je Prozess, Invalidierung über Postgres `LISTEN/NOTIFY` (Fallback: Polling alle 30 s) → Änderungen wirken in allen Containern ohne Neustart.
- **Pflege** über `GET/PUT /settings` (`settings:manage`) und Admin-UI (generisch aus dem Katalog gerendert); Änderungen landen im Audit-Log.
- **Nicht im Config-Service** (gehört in `.env`): alles, was vor dem DB-Zugriff bekannt sein muss oder die Infrastruktur betrifft.

### `ChargeGlobe.Api`
ASP.NET Core Minimal APIs, Versionierung `/api/v1`, OpenAPI 3.1 (eingebaute Generierung, Scalar UI), Problem Details (RFC 9457) für alle Fehler, `Microsoft.AspNetCore.RateLimiting` (derzeit auf `/auth/*`: 20 Requests je Minute und IP). Vorgesehen: Output-Caching mit kurzen TTLs für Karten-Queries, ETag/`If-None-Match`, CORS. Anonym erreichbar sind nur die Kartendaten, Health, Permission-Katalog und Auth-Endpunkte; alles andere läuft über JWT oder API-Key mit Permission-Policies. Die OpenAPI-Beschreibung ist so vollständig (operationId, Beschreibungen, Beispiele, Security Schemes, Permission-Hinweise), dass ein KI-Agent sie als einzige Dokumentation braucht. Details in [06](06-api.md).

### Mail-Service (Application + Infrastructure + Jobs)
- **Port** `IEmailSender.QueueAsync(EmailMessage)` in Application (`Mail/`); einziger Weg, eine Mail zu versenden. `EmailMessage` trägt Empfänger, Betreff, Text- und HTML-Körper und den `TemplateKey`.
- **Vorlagen** im Code (`EmailTemplates`, Text + HTML, de/en, Deutsch als Rückfall): `confirm-email` und `reset-password`; Links entstehen aus `PUBLIC_BASE_URL` (`/confirm-email?userId=…&token=…`, `/reset-password?userId=…&token=…`).
- **Outbox** `email_outbox` (Infrastructure, `OutboxEmailSender`): `to`, `subject`, `body_text`, `body_html`, `template_key`, `status` (`QUEUED | SENT | FAILED`), `attempts`, `last_error`, `created_at`, `sent_at`. Einträge entstehen in der Transaktion des auslösenden Use Case; Mail-Inhalte (Tokens) werden nie geloggt.
- **Vorgesehen** ([ADR-0008](adr/0008-zentraler-mail-service-smtp.md)): Versand in der Rolle `worker` (`SKIP LOCKED`, Backoff, begrenzte Versuche) per MailKit; SMTP-Host, Port, Sicherheit (`plain`/`starttls`/`ssl`), Benutzer, Passwort (Secret) und Absender aus dem Config-Service (`mail.*`); `POST /mail/test` und `GET /mail/outbox` (`settings:manage`). Bis dahin bleiben alle Mails mit Status `QUEUED` in der Outbox; ein Betrieb ohne Mailversand setzt `AUTH_REQUIRE_CONFIRMED_EMAIL=false`.

### Routing (Api + Infrastructure + Compose-Service `routing`)
- Port `IRoutingEngine` in Application, Adapter für Valhalla in Infrastructure (HTTP, Anti-Corruption-Layer wie bei den Quell-Adaptern).
- `POST /routes` (`route:read`): Start, Ziel, optionale Wegpunkte, Profil; Antwort mit Geometrie (GeoJSON), Distanz, Dauer, Abbiegehinweisen. Koordinaten werden weder gespeichert noch geloggt.
- Der Container `routing` baut seinen Graphen aus OSM-Extrakten in ein eigenes Volume und ist nur im Compose-Netz erreichbar.

### `web` (Angular)
- Karte (MapLibre mit OSM-Vektorkacheln, Clustering, Status-Farbcodierung, Filter: Stecker, Leistung, Betreiber, nur frei); für anonyme Besucher die einzige Funktion, inklusive eigenem Standort (Geolocation-Button).
- Routenfindung in der Karte (angemeldet): Start/Ziel per Klick, Suche oder eigenem Standort, Route als Layer mit Distanz und Dauer.
- Konto: Seiten `/register`, `/confirm-email`, `/login`, `/forgot-password`, `/reset-password` und die nur angemeldet erreichbare Seite `/account`. `AuthService` hält das Access-Token ausschließlich im Speicher (Signal) und stellt die Sitzung nach einem Reload über das Refresh-Cookie wieder her; ein HTTP-Interceptor setzt den Bearer-Header und erneuert ein abgelaufenes Token einmalig; der Guard `requirePermission(…)` leitet anonyme Besucher auf `/login?returnUrl=…` um. Die Startseite ist anonym erreichbar (Platzhalter für die Karte, API-/DB-Status) und zeigt nach dem Login E-Mail, Permissions und einen Logout-Button. Vorgesehen: Einstellungsseite für Customizings (Standard-Kartenausschnitt, gespeicherte Filter, eigene Ladekarten, Favoriten).
- Ladepunkt-Detail (angemeldet): aktueller Status + Alter der Beobachtung, Quellen, Belegungs-Timeline (letzte 7 Tage), Heatmap Wochentag × Stunde, Preistabelle über EMPs (Ad-hoc, ÖAMTC, EVN, Electroverse, …) mit Beispielrechnung (z. B. 30 kWh / 40 min).
- Provenienz-Ansicht (angemeldet): welches Attribut kommt woher, wann zuletzt bestätigt.
- Admin-Bereich, sichtbar nach Permissions: Config-Service inkl. SMTP-Einstellungen und Testmail, Provider-Health, Konflikt-Queue, Match-Review, Tarif-Editor (für Tarife, die nur in PDFs/Webseiten stehen), Nutzer- und Rollenverwaltung, API-Key-Verwaltung (Key wird genau einmal angezeigt), Audit-Log.

## Infrastruktur & Betrieb

- **Ein Compose-Stack für alles**: `docker compose up -d` startet `db` (`timescale/timescaledb-ha:pg17`, enthält PostGIS), `api`, `crawler` (×N), `worker` (×M) und `web` (nginx, liefert den Angular-Build statisch aus; `/api`, `/openapi` und `/scalar` werden containerintern per HTTP an `api:8080` weitergereicht). Nur `web` veröffentlicht einen Port, und zwar ausschließlich HTTPS mit einem self-signed Zertifikat, das der Container beim ersten Start erzeugt und im Volume `tls` ablegt ([ADR-0009](adr/0009-https-only-self-signed.md)); Compose-Projektname ist `chargeglobe`. Vorgesehen ist zusätzlich der Service `routing` (Valhalla, nicht exponiert).
- **`.env` minimal, aber vollständig dokumentiert**: `.env.example` enthält jede verfügbare Variable mit Default und Kommentar. Vorgesehener Umfang:

  | Variable | Default | Zweck |
  |---|---|---|
  | `CHARGEGLOBE_PORT` | `5005` | exponierter HTTPS-Port (einzige Oberkante) |
  | `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` | `chargeglobe`, `chargeglobe`, – | DB-Zugang (Container-intern) |
  | `JWT_SIGNING_KEY` | – | Secret für Access-Tokens (HS256), Pflicht, mindestens 32 Zeichen; ohne startet der Stack nicht |
  | `ADMIN_EMAIL`, `ADMIN_INITIAL_PASSWORD` | – | Seed des ersten Admin-Users; wird nur ausgewertet, solange noch kein Nutzer existiert (Passwort mindestens 10 Zeichen) |
  | `AUTH_REQUIRE_CONFIRMED_EMAIL` | `true` | Login erst nach bestätigter E-Mail-Adresse; bei `false` sind neue Konten sofort bestätigt (für Betrieb ohne SMTP-Versand) |
  | `CRAWLER_REPLICAS`, `WORKER_REPLICAS` | `1`, `1` | Anzahl Instanzen |
  | `LOG_LEVEL` | `Information` | Minimum-Log-Level aller .NET-Container |
  | `PUBLIC_BASE_URL` | `https://localhost:5005` | für Links in API-Antworten/E-Mails; der Hostname wird in das self-signed Zertifikat aufgenommen |

  Provider-Keys (z. B. E-Control-API-Key) und der SMTP-Zugang werden **nicht** in `.env` gepflegt, sondern verschlüsselt im Config-Service (Data-Protection-API, Schlüssel im `db`-Volume), damit `.env` klein bleibt und Keys über das Admin-UI rotiert werden können.
- **Oberkante der App ist ein HTTPS-Port** (`CHARGEGLOBE_PORT`, Default `5005`), unter dem Web-App und `/api` erreichbar sind; HTTP-Requests auf diesen Port werden auf HTTPS umgeleitet. Das Zertifikat ist dauerhaft self-signed. Die App sendet keinen `Strict-Transport-Security`-Header. Öffentlich gültige Zertifikate, Let's Encrypt oder Caddy sind **nicht** Teil der App; das übernimmt der externe Loadbalancer vor dem Port ([ADR-0009](adr/0009-https-only-self-signed.md)).
- **Server**: Dev-Deployment auf einem Debian-Host mit Docker unter `/opt/chargeglobe` (Zugangsdaten in `CLAUDE.local.md`, nicht im Repo). Andere Dienste auf dem Host werden nicht berührt; eigener Compose-Projektname, nur der eine Port wird exponiert.
- Seed-Skript lädt Region-Polygone und den ersten E-Control-Import.
- **Qualitätssicherung**: keine GitHub-CI (nicht im Scope). Tests laufen auf dem Dev-Server über einen eigenen Compose-Startpfad (`docker-compose.test.yml`, Projekt `chargeglobe-test`, getrennt vom laufenden Stack; Aufruf `scripts/test.sh`, das danach alle Reste des Projekts entfernt) – [ADR-0010](adr/0010-tests-auf-dev-server-per-compose.md). Vor jedem Merge laufen dort Build, Unit-Tests, Integrationstests (gegen den Compose-Service `test-db`, je Test-Fixture eine eigene Datenbank) und Angular-Tests (Karma/Jasmine); beide Suiten brechen unter 100 % Line- bzw. Statement-Coverage ab (siehe `CLAUDE.md`, Workflow und Befehle). Provider-Replay-Tests kommen mit dem ersten Adapter dazu. Coverlet ist über `coverlet.runsettings` konfiguriert (nur `ChargeGlobe.*`; ausgenommen sind Testprojekte, Source-Generator-Code, generierte EF-Migrationen und Auto-Properties); ReportGenerator fasst die Berichte der Testprojekte zusammen. Docker-Images werden beim Deploy auf dem Server gebaut.
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
