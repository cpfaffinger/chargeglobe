# CLAUDE.md – chargeglobe

Offene Datenbank für öffentliche E-Ladepunkte: Inventar aus vielen Quellen unter gemeinsamer Identität (EVSE-ID), zyklisches Status-Tracking je Ladepunkt mit Historie, Preistracking über Ladekarten-/App-Anbieter (EMPs) und Tarifmodelle hinweg, öffentliche API + Web-Frontend. Start-Scope Wien/NÖ/OÖ/Burgenland, Ziel AT → DACH → Italien.

Stack: **ASP.NET Core / C# / .NET 10 · Angular 22 / TypeScript · PostgreSQL 17 + PostGIS + TimescaleDB · Docker Compose**. Zugriff: anonym ist nur die Karte nutzbar (Ladepunkte mit Basisdaten und aktuellem Status, eigener Standort); alle erweiterten Funktionen und persönlichen Einstellungen setzen ein Benutzerkonto voraus (kostenlose Selbstregistrierung) ([ADR-0005](docs/adr/0005-identity-rbac-openapi.md)); Karte mit MapLibre/OSM, Standort und Routing ([ADR-0007](docs/adr/0007-karte-standort-routing.md)); zentraler Mail-Service mit SMTP ([ADR-0008](docs/adr/0008-zentraler-mail-service-smtp.md)); Nutzerverwaltung mit RBAC (Rollen = Berechtigungsbündel, feingranulare Permissions) für Pflege und Administration; API-Keys und eine OpenAPI-3.1-Beschreibung, die KI-Agenten ohne weitere Doku nutzen können ([ADR-0005](docs/adr/0005-identity-rbac-openapi.md)).

## Dokumentation (Einstieg)

| Datei | Inhalt |
|---|---|
| `docs/01-vision-und-scope.md` | Ziele, Begriffe (Location / EVSE = Ladepunkt / Connector), Regionen |
| `docs/02-architektur.md` | Module, Worker/Scheduler, API, Frontend, Betrieb |
| `docs/03-datenmodell.md` | kanonisches Modell (OCPI-Vokabular), Provenienz, Zeitreihen, Identity Resolution |
| `docs/04-provider-interface-acl.md` | Interfaces/Capabilities für Quell-Adapter, Checkliste neuer Provider |
| `docs/05-crawling-konzept.md` | Quellen-Tiers, Polling, Merge, App-Analyse-Methodik, Compliance |
| `docs/06-api.md` | REST v1 |
| `docs/07-bestandsanalyse-apps-at.md` | Anbieter/Apps Österreich, Priorisierung, belegte Endpunkte |
| `docs/08-datenquellen-und-recht.md` | E-Control-API, AFIR/NAP, DE/CH/IT, Rechtslage |
| `docs/09-roadmap.md` | Phasen, offene Entscheidungen, Risiken |
| `docs/adr/` | aktuell gültige Architekturentscheidungen |
| `docs/templates/endpunkt-notiz.md` | Vorlage für die Dokumentation eines Quell-Endpunkts |

## Arbeitsregeln (verbindlich)

1. **Code ist die Wahrheit, Doku folgt dem Code.** Wird Code geändert, werden betroffene Dokumente im selben Schritt angepasst. Niemals Code an veraltete Doku anpassen. Solange zu einem Thema noch kein Code existiert, beschreibt die Doku die Zielvision.
2. **Keine Historie in Dateien.** Dateien zeigen ausschließlich den aktuellen Ist-Stand bzw. die aktuelle Zielvision. Falsche oder überholte Informationen werden korrigiert oder gelöscht, nicht als „veraltet“ markiert. Keine Changelog-Abschnitte, keine „früher war…“-Absätze, keine „superseded“-ADRs: Ändert sich eine Entscheidung, wird der ADR umgeschrieben oder entfernt. Die Git-Historie ist das Archiv.
3. **Am Ende jeder Prompt-Ausführung den nächsten Prompt (n+1) formulieren.** Als letzten Abschnitt der Antwort einen Prompt-Vorschlag für den logisch nächsten Schritt als Codeblock ausgeben. Der Prompt muss **so vollständig sein, dass ein Agent ohne jeden Gesprächskontext die Aufgabe damit erledigen kann**. Pflichtinhalte:
   - Ausgangslage: Repo, Branch-Basis (`master`), relevante Dateien/Verzeichnisse mit Pfad, bereits getroffene Entscheidungen, die zu beachten sind (mit Verweis auf `docs/…`).
   - Ziel: was danach existiert bzw. funktioniert, inkl. Schnittstellen (Endpunkte, Tabellen, Komponenten) mit Namen.
   - Abnahmekriterien: konkrete Befehle, erwartete Ausgaben/URLs, Tests, Coverage.
   - Abschlussschritte: Doku-Anpassung, Branch-Merge, Deploy, Link an den Betreiber (siehe Workflow unten).
   Das gilt auch, wenn die Aufgabe nur teilweise erledigt wurde; dann beschreibt n+1 den Rest inklusive Zustand des liegengebliebenen Branches.
4. **Begriffe und Identität:** intern immer Location / ChargingStation / EVSE / Connector; fachlicher Schlüssel ist die EVSE-ID (normalisiert ohne `*`), technischer Schlüssel UUID. Die vom Projekt-Owner als „Netzpunkt-ID“ bezeichnete Kennung ist die EVSE-ID.
5. **Quellen-Vertrag:** Jeder Quell-Adapter implementiert die Interfaces aus `docs/04` und übersetzt im Adapter (Anti-Corruption-Layer); Quellformate dringen nie in Domain oder DB. Jede Quelle hat Descriptor, Status-Mapping-Tabelle, Fixtures und Replay-Test.
6. **Compliance:** Tier-0-Quellen (E-Control, NAP, OSM, OCM) bevorzugen; Rate-Limits je Provider im Adapter erzwingen; inoffizielle Endpunkte nur anonym erreichbar und mit ToS-Prüfung; keine eingebetteten Fremd-Keys ins Repo; Attribution in API-Antworten. Details `docs/05`, `docs/08`.
7. **Sprache:** Dokumentation und Kommunikation Deutsch; Code, Identifier, Kommentare, Commit-Messages Englisch. OpenAPI-Beschreibungen (summary/description) Englisch, da sie von Agenten gelesen werden.
8. **API-Regeln:** Jeder Endpunkt hat `operationId`, `summary`, `description`, Tags, Beispiel-Responses und deklariert seine Permission (`x-required-permission`); schreibende Endpunkte sind idempotent (Idempotency-Key) oder per PUT; Fehler immer als RFC-9457-Problem-Details; Autorisierung ausschließlich über Permission-Policies (`[RequirePermission("…")]`), nie über Rollennamen im Code. Details `docs/06`.
8. **Secrets** (API-Keys, `.env`, SSH-Zugänge) werden nie committet. Lokale Betriebsdaten stehen in `CLAUDE.local.md` (gitignored).

## Workflow je Prompt

1. **Branch anlegen**: jede Prompt-Ausführung arbeitet in einem eigenen Branch von `master` (`feat/<kurzname>`, `fix/<kurzname>`, `docs/<kurzname>`). Nie direkt auf `master` committen.
2. **Umsetzen + Tests**: Code, Tests und Doku im selben Branch. **Ziel ist 100 % Statement Coverage** für den .NET-Code (Coverlet, `dotnet test --collect:"XPlat Code Coverage"`) und den Angular-Code (`ng test --code-coverage`); noch keine Branch-Coverage-Gates. Nicht testbarer Glue-Code (z. B. `Program.cs`) wird explizit per `[ExcludeFromCodeCoverage]` ausgenommen und das Warum im Code kommentiert. Vor dem Merge laufen alle Tests grün, und zwar **auf dem Dev-Server über den Compose-Testpfad** (`docker-compose.test.yml`, Projekt `chargeglobe-test`, Aufruf `scripts/test.sh`), nicht in der lokalen WSL ([ADR-0010](docs/adr/0010-tests-auf-dev-server-per-compose.md)). Dazu den Branch nach `origin` pushen, auf dem Server nach `/opt/chargeglobe-test` auschecken, Testlauf starten, Ergebnis und Coverage berichten, danach die Test-Reste entfernen (nur Projekt `chargeglobe-test` und `/opt/chargeglobe-test`; nie `docker system prune` oder andere globale Aufräumbefehle).
3. **Keine GitHub-CI**: derzeit nicht im Scope. Keine Workflows unter `.github/` anlegen. Qualitätssicherung passiert vor dem Merge über den Testlauf auf dem Dev-Server.
4. **Merge**: nach grünen Tests und angepasster Doku den Branch in `master` mergen (`git merge --no-ff`), Branch lokal und auf `origin` löschen, `master` nach `origin` pushen.
5. **Deploy**: nach dem Merge auf den Dev-Server deployen (Zugang in `CLAUDE.local.md`): Repo unter `/opt/chargeglobe` auf `origin/master` bringen, `.env` auf dem Server unverändert lassen (nur neue Variablen aus `.env.example` ergänzen), `docker compose up -d --build`, Healthcheck prüfen. Die fremde App auf dem Server bleibt unberührt.
6. **Link nennen**: Im Chat zum Abschluss den erreichbaren Link an den Betreiber geben (`https://srv-dev2.pulseone.cloud:5005`, ggf. mit konkretem Pfad zur neuen Funktion) und den Zustand des Deployments ehrlich berichten (Healthcheck-Ergebnis, offene Punkte).
7. **n+1-Prompt** als letzter Abschnitt (Regel 3).

## Betrieb & Deployment

- Die gesamte App läuft als **Docker-Compose-Stack**. Services: `db` (timescaledb-ha mit PostGIS), `api` (.NET), `crawler` (.NET, holt Daten von Quellen; skalierbar), `worker` (.NET, Ingest/Merge/Aggregation; skalierbar) – beide aus dem Projekt `ChargeGlobe.Jobs` (Image `chargeglobe-jobs`, `--role crawler|worker`), `web` (nginx: Angular-Build, statisch ausgeliefert, proxied `/api` zur API, terminiert TLS). Vorgesehen: `routing` (Valhalla, nicht exponiert).
- **Konfiguration in zwei Schichten** ([ADR-0006](docs/adr/0006-konfiguration-env-minimal-config-service.md)):
  - **`.env` = nur Infrastruktur und Bootstrap**, so minimal wie möglich: exponierter Port, DB-Zugang, JWT-Secret, Admin-Seed, E-Mail-Bestätigung an/aus (`AUTH_REQUIRE_CONFIRMED_EMAIL`), Anzahl Crawler-/Worker-Instanzen, Log-Level, Basis-URL. **`.env.example` listet immer alle verfügbaren Variablen** mit Default und Kommentar; eine neue Variable ohne Eintrag in `.env.example` ist ein Fehler.
  - **Zentraler Config-Service in der App** für alles Fachliche (Provider an/aus, Intervalle, Rate-Limits, Regionen, Schwellwerte, Präzedenzen): Katalog der Einstellungen mit Typ, Default und Beschreibung im Code, Werte in der DB-Tabelle `app_setting`, änderbar über API (`settings:manage`) und Admin-UI (`/admin/settings`), wirksam ohne Neustart in allen Containern (Zugriff im Code nur über `IAppConfig`; neue Einstellungen werden in `SettingsCatalog` ergänzt). Keine fachlichen Einstellungen in `.env` oder `appsettings.json`.
- **Skalierung**: `crawler` und `worker` sind zustandslos und werden über `CRAWLER_REPLICAS` / `WORKER_REPLICAS` in `.env` (Compose `deploy.replicas`) vervielfacht; Arbeitsverteilung über Lease-/Queue-Tabellen in Postgres (`FOR UPDATE SKIP LOCKED`), kein Message-Broker.
- **Oberkante der App ist ein exponierter HTTPS-Port** (`CHARGEGLOBE_PORT`, Default `5005`, in `.env` änderbar), unter dem die Web-App inkl. `/api` erreichbar ist. Der Port spricht **ausschließlich HTTPS** mit einem **dauerhaft self-signed Zertifikat**, das der `web`-Container beim ersten Start erzeugt (Volume `tls`); HTTP-Requests werden umgeleitet. Die App sendet kein HSTS (`Strict-Transport-Security`); das wäre Sache des externen Loadbalancers. Intern (nginx → `api:8080`) bleibt es bei HTTP ([ADR-0009](docs/adr/0009-https-only-self-signed.md)).
- **Nicht im Scope:** öffentlich gültige Zertifikate, Let's Encrypt, Caddy. Das übernimmt der externe Loadbalancer (nginx) außerhalb dieses Repos.
- **Deployment-Ziel** ist ein Dev-Server, dessen Zugangsdaten in `CLAUDE.local.md` stehen. Auf dem Server läuft eine **andere Anwendung unter `/opt/companyhero-dev` mit eigenen Deploy-Skripten in `/opt`; diese wird nicht angefasst.** chargeglobe wird unter `/opt/chargeglobe` betrieben (`docker compose up -d`), eigene Compose-Projektnamen, keine Port-Kollisionen mit fremden Diensten.
- Deployment-Ablauf: Repo auf dem Server klonen/pullen → `.env` pflegen → `docker compose up -d --build` → Healthcheck `curl -k https://srv-dev2.pulseone.cloud:5005/api/v1/health`.

## Befehle

Alle Befehle vom Repo-Root, sofern nicht anders angegeben. Lokale Läufe (Voraussetzungen: .NET-10-SDK, Node 24, Docker mit Compose; rechnerspezifische Pfade in `CLAUDE.local.md`) sind optional; maßgeblich ist der Testlauf auf dem Dev-Server.

```bash
# .NET: Build und Tests mit Coverage (Coverlet-Konfiguration: coverlet.runsettings).
# ChargeGlobe.IntegrationTests braucht ein Postgres und die Variable ConnectionStrings__TestDb
# (Server-Verbindung, z. B. "Host=localhost;Username=postgres;Password=…;Database=postgres");
# im Compose-Testpfad ist beides vorhanden.
dotnet build
dotnet test --collect:"XPlat Code Coverage"
dotnet tool restore
dotnet tool run reportgenerator "-reports:tests/*/TestResults/*/coverage.cobertura.xml" -targetdir:artifacts/coverage -reporttypes:TextSummary
cat artifacts/coverage/Summary.txt          # erwartet: Line coverage: 100%

# Angular (in src/web): Tests mit Coverage, Build, Dev-Server (proxied /api an localhost:8080)
npm ci
npx ng test --code-coverage --watch=false   # erwartet: Statements 100% (karma.conf.js bricht darunter ab); braucht CHROME_BIN (Chrome oder Edge)
npx ng build                                # maplibre-gl landet als eigener Lazy-Chunk ("Lazy chunk files … maplibre-gl")
npx ng serve

# Replay-Erwartung eines Adapters nach gewollter Mapping-Änderung neu schreiben (danach Diff von fixtures/<key>/*.expected.json prüfen)
UPDATE_FIXTURES=1 dotnet test tests/ChargeGlobe.Providers.Tests

# EF-Core-Migrationen (Tool dotnet-ef aus dotnet-tools.json; braucht keine Datenbank)
dotnet tool restore
dotnet tool run dotnet-ef migrations add <Name> --project src/ChargeGlobe.Infrastructure --output-dir Persistence/Migrations
dotnet tool run dotnet-ef migrations script --idempotent --project src/ChargeGlobe.Infrastructure   # SQL zur Kontrolle
# Angewendet werden Migrationen von der API beim Start (DatabaseInitializer, mit pg_advisory_lock),
# also durch "docker compose up -d --build" bzw. "dotnet run --project src/ChargeGlobe.Api".

# Einzelne Prozesse ohne Docker
dotnet run --project src/ChargeGlobe.Api                    # http://localhost:8080, DB und JWT_SIGNING_KEY laut launchSettings.json
dotnet run --project src/ChargeGlobe.Jobs -- --role crawler # oder --role worker

# Compose-Stack
cp .env.example .env                        # POSTGRES_PASSWORD, JWT_SIGNING_KEY: openssl rand -hex 32; ADMIN_EMAIL, ADMIN_INITIAL_PASSWORD: openssl rand -base64 18
docker compose up -d --build
curl -k https://localhost:5005/api/v1/health   # {"status":"ok","db":"ok"}; -k wegen self-signed Zertifikat
curl -k https://localhost:5005/api/v1/permissions   # Permission-Katalog (anonym)
curl -k https://localhost:5005/api/v1/map/config    # Kartenkonfiguration aus dem Config-Service (anonym)
curl -k "https://localhost:5005/api/v1/locations?bbox=16.2,48.1,16.6,48.3&limit=50"   # Standorte in Wien mit meta.attribution (anonym)
curl -kI https://localhost:5005/maplibre/maplibre-gl-worker.mjs   # 200, Content-Type text/javascript (Worker der Karte)
docker compose logs crawler worker          # Heartbeats beider Rollen (alle 30 s, mit aktuellem Wert von mail.enabled); worker: "Mail outbox: …"; crawler: "Provider econtrol healthy: …" (alle 15 min, nur mit provider.enabled)
docker compose exec db psql -U chargeglobe -d chargeglobe -c "select template_key, status, attempts, last_error from email_outbox"
docker compose exec db psql -U chargeglobe -d chargeglobe -c "select hypertable_name from timescaledb_information.hypertables"   # evse_status_observation, location_status_observation
docker compose exec db psql -U chargeglobe -d chargeglobe -c "select provider_key, job_type, region_code, next_run_at, leased_by, last_result from poll_plan order by 1,2,3"
docker compose exec db psql -U chargeglobe -d chargeglobe -c "select count(*) filter (where processed_at is null) offen, count(*) filter (where error is not null) fehler from ingest_queue"
docker compose exec db psql -U chargeglobe -d chargeglobe -c "select (select count(*) from location) standorte, (select count(*) from evse) evses, (select count(*) from evse_status_current) status"
docker compose down                         # Volumes pgdata und tls (Zertifikat) bleiben erhalten

# Testlauf auf dem Dev-Server (maßgeblicher Abnahmeweg, ADR-0010; per SSH laut CLAUDE.local.md).
# scripts/test.sh baut die Test-Images, startet test-dotnet und test-web (Tests + Produktions-Build) nacheinander, gibt beide
# Coverage-Zusammenfassungen aus und entfernt danach immer Container, Netz und lokal gebaute Images
# des Projekts chargeglobe-test (docker compose -f docker-compose.test.yml down -v --rmi local --remove-orphans).
git push -u origin <branch>                                   # lokal
git clone --depth 1 --branch <branch> https://github.com/cpfaffinger/chargeglobe.git /opt/chargeglobe-test
cd /opt/chargeglobe-test && ./scripts/test.sh                 # Exitcode 0 = alle Tests grün; Ausgabe "Line coverage: 100%" und "Statements : 100%"
cd /opt && rm -rf /opt/chargeglobe-test
docker compose ls                                             # chargeglobe-test darf nicht mehr auftauchen
docker ps -a --filter label=com.docker.compose.project=chargeglobe-test   # leer

# Deploy auf den Dev-Server (per SSH laut CLAUDE.local.md)
cd /opt/chargeglobe && git pull --ff-only origin master
docker compose up -d --build
curl -k https://localhost:5005/api/v1/health
```

Vor einem neuen lokalen Coverage-Lauf alte Ergebnisse löschen (`tests/*/TestResults`, `artifacts/`), sonst mischt ReportGenerator alte und neue Berichte; im Compose-Testpfad erledigt das der Container. Bei einer Coverage-Lücke steht die Liste der nicht abgedeckten Zeilen in der Ausgabe von `scripts/test.sh` (`Datei:Zeile`). Niemals `docker system prune`, `docker volume prune` oder andere globale Aufräumbefehle auf dem Dev-Server ausführen.

## Nächste Schritte (Stand jetzt)

Siehe `docs/09-roadmap.md`: Ladepunkt-Detailseite mit Statushistorie für angemeldete Nutzer und Continuous Aggregate `evse_occupancy_hourly`, danach Region-Seed aus OSM-Grenzen; außerdem Lizenz festlegen und die E-Control-Nutzungsbedingungen archivieren. API-Keys von Quellen werden nur im Admin-UI eingetragen (`provider.api_key`, Scope `provider:<key>`), nie in Dateien.
