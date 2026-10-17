# chargeglobe

**Offene Datenbank für öffentliche E-Ladepunkte – mit Belegungshistorie und Preisvergleich über Apps/Tarife hinweg.**

chargeglobe sammelt Ladestationen aus allen erreichbaren Quellen (amtliche Verzeichnisse, Open Data, Web-Karten und
App-Backends von Betreibern und Ladekarten-Anbietern), führt sie unter einer gemeinsamen Identität (EVSE-ID) zusammen,
fragt zyklisch den Status jedes einzelnen Ladepunkts ab und protokolliert Preise je Ladepunkt über verschiedene
Anbieter und Tarifmodelle hinweg. Alles wird über eine öffentliche API und ein Web-Frontend bereitgestellt.

| | |
|---|---|
| **Status** | Lauffähiges Grundgerüst: Compose-Stack, Benutzerkonten (Selbstregistrierung, Login, Passwort-Reset, API-Keys, Permissions), zentraler Config-Service mit Admin-Seite, Mail-Versand über konfigurierbaren SMTP-Server, OpenAPI 3.1, Angular-Seiten für Konto und Anmeldung, Karte als anonyme Startseite (MapLibre, OpenFreeMap, eigener Standort, Ladepunkte mit Clustering und Verfügbarkeitsfarben), Ingest-Pipeline für das E-Control-Ladestellenverzeichnis (Register seitenweise für Inventar mit Zahlungsarten, Roaming, Ökostrom und Parken; Live-Status über Abfragepunkte mit Stundenbudget und Pausen nach Fehlern; Live-Ansicht der Crawler für Administratoren; Scheduler, Request-Budget, Queue, Merge, Statushistorie), Ladepunkt-API mit Statushistorie und Belegungsprofil (angemeldet), Standort-Detailseite mit Status-Timeline und Heatmap, Oberfläche mit Angular Material 3 in Light und Dark Mode, Bundesländer aus OpenStreetMap-Grenzen für die Regionszuordnung, Jobs-Heartbeat; Preise, Filter und weitere Quellen folgen laut [Roadmap](docs/09-roadmap.md) |
| **Live** | [chargeglobe.net](https://chargeglobe.net) |
| **Start-Scope** | Wien, Niederösterreich, Oberösterreich, Burgenland |
| **Ziel-Scope** | Österreich → DACH → Italien |
| **Stack** | ASP.NET Core / C# / .NET 10 · Angular 22 / TypeScript · PostgreSQL (PostGIS + TimescaleDB) |
| **Zugriff** | anonym ist nur die Karte nutzbar (Ladepunkte mit Basisdaten und aktuellem Status, eigener Standort); alle erweiterten Funktionen und persönlichen Einstellungen setzen ein Benutzerkonto voraus (kostenlose Selbstregistrierung); rollenbasierte Berechtigungen (RBAC) für Pflege/Admin; API-Keys und OpenAPI 3.1 für KI-Agenten und Integrationen |
| **Karte** | MapLibre GL JS mit OpenStreetMap-Vektorkacheln (OpenFreeMap, eigener Stil für den Dark Mode), eigener Standort; Routing über selbst gehostete OSM-Engine (geplant) |
| **Design** | Angular Material 3 + CDK, Palette „Volt“, Schrift Roboto Flex (OFL-1.1, selbst gehostet), Icons aus Material Symbols (Apache-2.0, eingebettet); hell, dunkel oder nach System ([ADR-0012](docs/adr/0012-design-system-und-theming.md)) |
| **Betrieb** | Docker Compose, Konfiguration über `.env`, Daten in Bind-Mounts unter `./data/` (keine Docker-Volumes), Web-App + API auf einem HTTPS-Port (Default 5005, self-signed Zertifikat); gültiges Zertifikat über externen Loadbalancer |
| **Lizenz** | noch offen – Vorschlag siehe [docs/09-roadmap.md](docs/09-roadmap.md); die Region-Polygone (`src/ChargeGlobe.Infrastructure/Persistence/Seed/regions-at.geojson`) stammen aus OpenStreetMap, © OpenStreetMap contributors, ODbL 1.0 |

## Dokumentation

| Dokument | Inhalt |
|---|---|
| [01 – Vision & Scope](docs/01-vision-und-scope.md) | Ziele, Nicht-Ziele, Begriffe (Ladesäule vs. Ladepunkt vs. Stecker), Regionen |
| [02 – Architektur](docs/02-architektur.md) | Technisches Konzept: Modularer Monolith, Crawler-Worker, API, Frontend, Infrastruktur |
| [03 – Datenmodell](docs/03-datenmodell.md) | Kanonisches Modell (an OCPI 2.2.1 angelehnt), Identität über EVSE-ID, Status- und Preishistorie, SQL-Skizze |
| [04 – Provider-Interface & Anti-Corruption-Layer](docs/04-provider-interface-acl.md) | Wie neue Anbieter angebunden werden: Interfaces, Capabilities, Mapping, Tests |
| [05 – Crawling-Konzept](docs/05-crawling-konzept.md) | Quellen-Tiers, Polling-Strategie, Rate-Limits, Merge-Regeln, App-Analyse-Methodik, Compliance |
| [06 – API](docs/06-api.md) | Öffentliche REST-API (v1): Ladepunkte, Historie, Belegung, Preise, Provenienz |
| [07 – Bestandsanalyse Apps/Anbieter Österreich](docs/07-bestandsanalyse-apps-at.md) | EVN, ÖAMTC ePower, Verbund/SMATRICS, Electroverse, Tesla, Wien Energie, Linz AG, Energie AG, Energie Burgenland, … |
| [08 – Datenquellen & Rechtsrahmen](docs/08-datenquellen-und-recht.md) | E-Control Ladestellenverzeichnis, AFIR/NAP, Open Data, DE/CH/IT, rechtliche Leitplanken |
| [09 – Roadmap](docs/09-roadmap.md) | Phasen, Meilensteine, offene Entscheidungen |
| [ADRs](docs/adr/) | Architekturentscheidungen |

## Schnellstart

```bash
cp .env.example .env        # POSTGRES_PASSWORD, JWT_SIGNING_KEY (openssl rand -hex 32) setzen
docker compose up -d --build
curl -k https://localhost:5005/api/v1/health   # {"status":"ok","db":"ok"}
docker compose logs api | grep "Setup required"   # Setup-Token für den Assistenten unter https://localhost:5005/setup
```

Web-App: `https://localhost:5005` (self-signed Zertifikat, Ausnahme im Browser bestätigen) mit der Karte als Startseite; Kacheln, Schriften und Sprites lädt der Browser von `tiles.openfreemap.org` (Einstellung `map.style_url`). Beim ersten Start führt jede Seite zum Setup-Assistenten `/setup`: erstes Administrator-Konto mit dem Setup-Token aus dem API-Log anlegen, danach optional E-Control-Zugang und SMTP-Server eintragen (alternativ legt `ADMIN_EMAIL`/`ADMIN_INITIAL_PASSWORD` in `.env` den Admin automatisch an) · OpenAPI 3.1: `/openapi/v1.json` · Scalar UI: `/scalar`. Mails werden erst versendet, wenn ein Admin den SMTP-Zugang unter `/admin/settings` eingetragen (`mail.*`), unter `/admin/mail` mit einer Testmail geprüft und `mail.enabled` eingeschaltet hat; bis dahin bleiben sie in der Outbox, und mit der Einstellung `auth.require_confirmed_email = false` sind neue Konten sofort nutzbar. Die Administration liegt unter `/admin` (Nutzer, Rollen, Crawler, E-Mail, Einstellungen); der Setup-Assistent läuft nur einmal, bei leerer Instanz. Build- und Testbefehle stehen in [CLAUDE.md](CLAUDE.md#befehle).

## Repository-Struktur

Einträge mit „(geplant)“ existieren noch nicht.

```
chargeglobe/
├─ docs/                         # Konzept und Architektur
├─ ChargeGlobe.sln
├─ src/
│  ├─ ChargeGlobe.Domain/        # Ladepunkt-Modell (Location, EVSE, Connector, Operator, Region), Provenienz, Status-Zeitreihen, Value Objects (EvseId, GeoPoint), Preise und EMP-Tarife, Permission-Katalog; vorgesehen: Domain-Events
│  ├─ ChargeGlobe.Application/   # Ports (IEmailSender, IAppConfig, IIngestQueue, IPollPlanStore, …), Settings-Katalog, Mail-Vorlagen, Ingest-Batch, Regionen
│  ├─ ChargeGlobe.Infrastructure/# EF Core + Npgsql (AppDbContext, Migrationen, PostGIS/TimescaleDB), ASP.NET Core Identity, Identity-Services, Mail-Outbox + SMTP (MailKit), Config-Service
│  ├─ ChargeGlobe.Providers.Abstractions/  # Provider-Vertrag (ACL): Descriptor, Quell-Interfaces, Ingest-Modell, Status-Mapping
│  ├─ ChargeGlobe.Providers.EControl/  # Adapter E-Control-Ladestellenverzeichnis (offizielle API mit Key)
│  ├─ ChargeGlobe.Providers.OpenChargeMap/  # Adapter Open Charge Map (Inventar, CC BY 4.0, API-Key)
│  ├─ ChargeGlobe.Providers.TariffCatalog/  # kuratierter Tarifkatalog (catalog/<land>/*.json: Netze, Anbieter, Tarife mit Zonen; ADR-0013)
│  ├─ ChargeGlobe.Providers.*/   # (geplant) weitere Adapter (Osm, Evn, …)
│  ├─ ChargeGlobe.Api/           # ASP.NET Core Minimal API (Karte, EVSE-Detail/Historie/Belegung, Konto, Einstellungen, Mail), OpenAPI 3.1, Auth (JWT + API-Keys), Permission-Prüfung, Rate Limiting
│  ├─ ChargeGlobe.Jobs/          # ein Host für beide Rollen (--role crawler|worker), Image chargeglobe-jobs, skalierbar
│  │                             #   crawler: Source-Abgleich, Health-Probe, Lease-Scheduler + Abrufe → raw_snapshot, ingest_queue
│  │                             #   worker: Mail-Zustellung, Ingest-Queue → Merge ins Ladepunkt-Modell, Retention; vorgesehen: Aggregate, Reconcile
│  └─ web/                       # Angular-App: Karte (Startseite), Standort-Detail mit Timeline und Heatmap, Registrierung, Login, Passwort-Reset, Konto, Suche, Administration (Nutzer, Rollen, Crawler, E-Mail, Einstellungen)
├─ tests/
│  ├─ ChargeGlobe.Api.Tests/         # Tests ohne Datenbank (WebApplicationFactory mit Stub, Einstellungen, Mail-Vorlagen)
│  ├─ ChargeGlobe.Domain.Tests/
│  ├─ ChargeGlobe.Jobs.Tests/
│  ├─ ChargeGlobe.Providers.Tests/   # Tests des Provider-Vertrags; künftig Replay-Tests der Adapter mit sanitisierten Antworten
│  ├─ ChargeGlobe.IntegrationTests/  # API in-process gegen Postgres (Compose-Service test-db): Migration, Seed, Identity, Ingest, Karten- und EVSE-Endpunkte, OpenAPI
│  ├─ Dockerfile.dotnet              # Test-Image für test-dotnet
│  └─ run-dotnet-tests.sh            # Tests + Coverage-Gate im Container
├─ fixtures/                     # sanitisierte Beispiel-Responses je Provider + erwartetes Ingest-Modell (Replay-Tests)
├─ docker-compose.yml            # data-init · db · api · crawler · worker · web, gesteuert über .env
├─ data/                         # (entsteht beim Start, gitignored) Bind-Mounts: postgres/ (Datenbank), tls/ (Zertifikat)
├─ docker-compose.test.yml       # Testlauf statt App: test-db · test-smtp · test-dotnet · test-web (Projekt chargeglobe-test)
├─ scripts/test.sh               # ein Aufruf: bauen, beide Suiten, Coverage-Zusammenfassung, aufräumen
├─ scripts/regions/              # build-regions-at.sh: Bundesländer-Polygone aus OpenStreetMap (Overpass + PostGIS) → Persistence/Seed/regions-at.geojson
└─ .env.example                  # vollständige Liste aller Variablen
```
