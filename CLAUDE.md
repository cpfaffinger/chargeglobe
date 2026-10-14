# CLAUDE.md – chargeglobe

Offene Datenbank für öffentliche E-Ladepunkte: Inventar aus vielen Quellen unter gemeinsamer Identität (EVSE-ID), zyklisches Status-Tracking je Ladepunkt mit Historie, Preistracking über Ladekarten-/App-Anbieter (EMPs) und Tarifmodelle hinweg, öffentliche API + Web-Frontend. Start-Scope Wien/NÖ/OÖ/Burgenland, Ziel AT → DACH → Italien.

Stack: **ASP.NET Core / C# / .NET 10 · Angular 22 / TypeScript mit Angular Material 3 ([ADR-0012](docs/adr/0012-design-system-und-theming.md)) · PostgreSQL 17 + PostGIS + TimescaleDB · Docker Compose**. Zugriff: anonym ist nur die Karte nutzbar (Ladepunkte mit Basisdaten und aktuellem Status, eigener Standort); alle erweiterten Funktionen und persönlichen Einstellungen setzen ein Benutzerkonto voraus (kostenlose Selbstregistrierung) ([ADR-0005](docs/adr/0005-identity-rbac-openapi.md)); Karte mit MapLibre/OSM, Standort und Routing ([ADR-0007](docs/adr/0007-karte-standort-routing.md)); zentraler Mail-Service mit SMTP ([ADR-0008](docs/adr/0008-zentraler-mail-service-smtp.md)); Nutzerverwaltung mit RBAC (Rollen = Berechtigungsbündel, feingranulare Permissions) für Pflege und Administration; API-Keys und eine OpenAPI-3.1-Beschreibung, die KI-Agenten ohne weitere Doku nutzen können ([ADR-0005](docs/adr/0005-identity-rbac-openapi.md)).

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
5. **Deploy**: nach dem Merge auf den Live-Server `srv-dev1` deployen (Zugang in `CLAUDE.local.md`): Repo unter `/etc/docker/compose/chargeglobe` auf `origin/master` bringen, `.env` auf dem Server unverändert lassen (nur neue Variablen aus `.env.example` ergänzen), `docker compose up -d --build`, Healthcheck prüfen (lokal und über `https://chargeglobe.net`). Die anderen Sites und Container auf dem Server bleiben unberührt.
6. **Link nennen**: Im Chat zum Abschluss den erreichbaren Link an den Betreiber geben (`https://chargeglobe.net`, ggf. mit konkretem Pfad zur neuen Funktion) und den Zustand des Deployments ehrlich berichten (Healthcheck-Ergebnis, offene Punkte).
7. **n+1-Prompt** als letzter Abschnitt (Regel 3).

## Betrieb & Deployment

- Die gesamte App läuft als **Docker-Compose-Stack**. Services: `db` (timescaledb-ha mit PostGIS), `api` (.NET), `crawler` (.NET, holt Daten von Quellen; skalierbar), `worker` (.NET, Ingest/Merge/Aggregation; skalierbar) – beide aus dem Projekt `ChargeGlobe.Jobs` (Image `chargeglobe-jobs`, `--role crawler|worker`), `web` (nginx: Angular-Build, statisch ausgeliefert, proxied `/api` zur API, terminiert TLS). Vorgesehen: `routing` (Valhalla, nicht exponiert).
- **Konfiguration in zwei Schichten** ([ADR-0006](docs/adr/0006-konfiguration-env-minimal-config-service.md)):
  - **`.env` = nur Infrastruktur und Bootstrap**, so minimal wie möglich: exponierter Port, DB-Zugang, JWT-Secret, optionaler Admin-Seed (leer = Setup-Assistent `/setup`), Anzahl Crawler-/Worker-Instanzen, Log-Level, Basis-URL. **`.env.example` listet immer alle verfügbaren Variablen** mit Default und Kommentar; eine neue Variable ohne Eintrag in `.env.example` ist ein Fehler.
  - **Zentraler Config-Service in der App** für alles Fachliche (Provider an/aus, Intervalle, Rate-Limits, Regionen, Schwellwerte, Präzedenzen, E-Mail-Bestätigung und Admin-Freischaltung neuer Konten): Katalog der Einstellungen mit Typ, Default und Beschreibung im Code, Werte in der DB-Tabelle `app_setting`, änderbar über API (`settings:manage`) und Admin-UI (`/admin/settings`), wirksam ohne Neustart in allen Containern (Zugriff im Code nur über `IAppConfig`; neue Einstellungen werden in `SettingsCatalog` ergänzt). Keine fachlichen Einstellungen in `.env` oder `appsettings.json`.
- **Skalierung**: `crawler` und `worker` sind zustandslos und werden über `CRAWLER_REPLICAS` / `WORKER_REPLICAS` in `.env` (Compose `deploy.replicas`) vervielfacht; Arbeitsverteilung über Lease-/Queue-Tabellen in Postgres (`FOR UPDATE SKIP LOCKED`), kein Message-Broker.
- **Oberkante der App ist ein exponierter HTTPS-Port** (`CHARGEGLOBE_PORT`, Default `5005`, Bind-Adresse `CHARGEGLOBE_BIND_ADDRESS`, Default `0.0.0.0`, beides in `.env` änderbar), unter dem die Web-App inkl. `/api` erreichbar ist. Der Port spricht **ausschließlich HTTPS** mit einem **dauerhaft self-signed Zertifikat**, das der `web`-Container beim ersten Start erzeugt (`./data/tls`); HTTP-Requests werden umgeleitet. Die App sendet kein HSTS (`Strict-Transport-Security`); das wäre Sache des externen Loadbalancers. Intern (nginx → `api:8080`) bleibt es bei HTTP ([ADR-0009](docs/adr/0009-https-only-self-signed.md)).
- **Keine Docker-Volumes, nur Bind-Mounts relativ zur Compose-Datei** ([ADR-0011](docs/adr/0011-bind-mounts-statt-volumes.md)): persistente Daten liegen unter `./data/` (`./data/postgres` für den Postgres-Cluster, `./data/tls` für das Zertifikat); der One-Shot-Service `data-init` legt die Verzeichnisse mit den richtigen Rechten an. `./data/` ist gitignored und aus den Build-Kontexten ausgeschlossen. Zurücksetzen einer Umgebung = Stack stoppen, `./data/` löschen, starten (danach erscheint der Setup-Assistent). Ein neuer Service mit persistenten Daten bekommt ein Unterverzeichnis von `./data/`.
- **Nicht im Scope:** öffentlich gültige Zertifikate, Let's Encrypt, Caddy. Das übernimmt der vorgelagerte nginx des Hosts außerhalb dieses Repos (live: Cloudflare-Origin-Zertifikat, siehe unten).
- **Live-Betrieb** unter **`https://chargeglobe.net`** (ohne `www`) auf `srv-dev1.pulseone.cloud` (Zugang in `CLAUDE.local.md`). Kette: Cloudflare (Proxy, SSL-Modus Full) → nginx des Hosts (`/etc/nginx/sites-enabled/chargeglobe.net.conf`, Cloudflare-Origin-Zertifikat aus `/etc/ssl/cloudflare/chargeglobe.net/`, echte Client-IP per `realip` aus `CF-Connecting-IP`, nur von Cloudflare-Adressen) → `https://127.0.0.1:28060` (`CHARGEGLOBE_BIND_ADDRESS=127.0.0.1`, `CHARGEGLOBE_PORT=28060`, `PUBLIC_BASE_URL=https://chargeglobe.net`) → `web`-Container. Der Stack liegt als Git-Checkout in `/etc/docker/compose/chargeglobe` und startet beim Booten über die systemd-Unit `docker-compose@chargeglobe` (`docker compose up -d --remove-orphans` in diesem Verzeichnis). **Auf dem Host laufen viele andere Sites und Container (`/etc/docker/compose/*`, `/etc/nginx/sites-enabled/*`); diese werden nicht angefasst.** Der Host hat wenig freien Arbeitsspeicher (8 GB gesamt), deshalb laufen Testläufe nicht dort.
- **Testserver** bleibt `srv-dev2.pulseone.cloud` (nur `scripts/test.sh` in `/opt/chargeglobe-test`, [ADR-0010](docs/adr/0010-tests-auf-dev-server-per-compose.md)). Dort liegt unter `/opt/chargeglobe` der gestoppte frühere Dev-Stack als Sicherung; er wird nicht wieder gestartet (sonst ruft ein zweiter Crawler die E-Control ab). **Auf srv-dev2 läuft außerdem eine andere Anwendung unter `/opt/companyhero-dev` mit eigenen Deploy-Skripten in `/opt`; diese wird nicht angefasst.**
- Deployment-Ablauf: `cd /etc/docker/compose/chargeglobe && git pull --ff-only origin master` → `.env` um neue Variablen ergänzen → `docker compose up -d --build` → Healthcheck `curl -k https://127.0.0.1:28060/api/v1/health` und `curl https://chargeglobe.net/api/v1/health`.

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
npx ng generate @angular/material:theme-color --primary-color="#006A60" --tertiary-color="#7C5800" --directory=src/ --is-scss   # Palette neu erzeugen (ADR-0012), nur bei Farbwechsel

# Replay-Erwartung eines Adapters nach gewollter Mapping-Änderung neu schreiben (danach Diff von fixtures/<key>/*.expected.json prüfen)
UPDATE_FIXTURES=1 dotnet test tests/ChargeGlobe.Providers.Tests

# Region-Polygone aus OpenStreetMap neu erzeugen (Overpass + Wegwerf-Container mit PostGIS; braucht curl und Docker, z. B. auf srv-dev2)
./scripts/regions/build-regions-at.sh       # schreibt src/ChargeGlobe.Infrastructure/Persistence/Seed/regions-at.geojson; die API übernimmt Änderungen beim nächsten Start

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
cp .env.example .env                        # POSTGRES_PASSWORD, JWT_SIGNING_KEY: openssl rand -hex 32; ADMIN_EMAIL/ADMIN_INITIAL_PASSWORD leer lassen → Setup-Assistent
docker compose up -d --build
curl -k https://localhost:5005/api/v1/health   # {"status":"ok","db":"ok"}; -k wegen self-signed Zertifikat
curl -k https://localhost:5005/api/v1/setup        # {"required":true} solange kein Nutzer existiert
docker compose logs api | grep "Setup required"     # Setup-Token für https://<host>:5005/setup (nach jedem API-Neustart neu)
curl -k https://localhost:5005/api/v1/permissions   # Permission-Katalog (anonym)
curl -k https://localhost:5005/api/v1/map/config    # Kartenkonfiguration aus dem Config-Service (anonym)
curl -k "https://localhost:5005/api/v1/locations?bbox=16.2,48.1,16.6,48.3&limit=50"   # Standorte in Wien mit meta.attribution (anonym)
curl -k "https://localhost:5005/api/v1/locations?bbox=16.2,48.1,16.6,48.3&minPowerKw=150&connector=IEC_62196_T2_COMBO&availableOnly=true"   # mit Filtern; ungültiger connector → 422
curl -k "https://localhost:5005/api/v1/operators?q=wien&limit=5"   # Betreiber nach Zahl der Standorte (anonym)
curl -kI https://localhost:5005/maplibre/maplibre-gl-worker.mjs   # 200, Content-Type text/javascript (Worker der Karte)
curl -k "https://localhost:5005/api/v1/evses/AT*TST*E1*1"   # ohne Token: 401 Problem Details (evse:read nötig)
TOKEN=$(curl -sk -H 'Content-Type: application/json' -d "{\"email\":\"$EMAIL\",\"password\":\"$PASSWORD\"}" https://localhost:5005/api/v1/auth/login | sed 's/.*"accessToken":"\([^"]*\)".*/\1/')
curl -k -H "Authorization: Bearer $TOKEN" "https://localhost:5005/api/v1/evses/<EVSE-ID oder UUID>"                  # 200: EVSE mit Standort, Steckern, aktuellem Status
curl -k -H "Authorization: Bearer $TOKEN" "https://localhost:5005/api/v1/evses/<EVSE-ID>/status/history"           # Statusintervalle der letzten 7 Tage (max. 90 Tage je Abfrage)
curl -k -H "Authorization: Bearer $TOKEN" "https://localhost:5005/api/v1/evses/<EVSE-ID>/occupancy?weeks=4"        # Matrix Wochentag × Stunde (Europe/Vienna)
curl -k -H "Authorization: Bearer $TOKEN" "https://localhost:5005/api/v1/evses/<EVSE-ID>/prices"                  # Ad-hoc-Preis, Verlauf, Kosten 30 kWh/40 min, emps[] = Ladekarten nach Kosten (price:read)
curl -k -H "Authorization: Bearer $TOKEN" -X PUT -H 'Content-Type: application/json' -d '{"name":"SMATRICS","website":"https://smatrics.com"}' https://localhost:5005/api/v1/emps/smatrics   # Anbieter anlegen (tariff:write); UI: /admin/tariffs
curl -k -H "Authorization: Bearer $TOKEN" -X PUT -H 'Content-Type: application/json' -d '{"emp":"smatrics","name":"Basic","currency":"EUR","validFrom":"2026-01-01T00:00:00Z","pricePerKwhAc":0.45,"pricePerKwhDc":0.65,"roamingOnly":true}' https://localhost:5005/api/v1/tariffs/$(cat /proc/sys/kernel/random/uuid)   # Tarif anlegen, 201
curl -k -H "Authorization: Bearer $TOKEN" https://localhost:5005/api/v1/tariffs   # Tarife (price:read)
docker compose logs crawler worker          # Heartbeats beider Rollen (alle 30 s); crawler (nur mit provider.enabled): "Job STATUS_POLL econtrol point 17/212 (…) started (HOT, last poll 6 min ago)", "… done: … requests …; 412/600 requests in the last hour; …", "E-Control register page 3/17 …", Pausen "… provider paused until …"; Health-Probe stündlich
curl -k "https://localhost:5005/api/v1/search?q=stephansplatz"   # Suche nach EVSE-ID, Name, Straße, PLZ, Ort (anonym)
curl -k -H "Authorization: Bearer $TOKEN" "https://localhost:5005/api/v1/users?q=example.org"   # Konten mit Status und Rollen (user:read); UI: /admin/users
curl -k -H "Authorization: Bearer $TOKEN" https://localhost:5005/api/v1/roles        # Rollen mit Permissions (user:read); UI: /admin/roles
curl -k -X PUT -H "Authorization: Bearer $TOKEN" https://localhost:5005/api/v1/me/favorites/<Standort-UUID>   # Favorit merken (204, idempotent); DELETE entfernt
curl -k -H "Authorization: Bearer $TOKEN" https://localhost:5005/api/v1/me/favorites   # Favoriten mit aktuellem Status; UI: /favorites
curl -k -H "Authorization: Bearer $TOKEN" https://localhost:5005/api/v1/me/preferences   # Startposition, Filter, Kartenoptionen des Kontos (updatedAt null = noch keine)
curl -k -H "Authorization: Bearer $TOKEN" https://localhost:5005/api/v1/admin/crawler/status   # Budget, Pause, Abfragepunkte, Pläne, Queue (jobs:read); UI: /admin/crawler
docker compose exec db psql -U chargeglobe -d chargeglobe -c "select template_key, status, attempts, last_error from email_outbox"
docker compose exec db psql -U chargeglobe -d chargeglobe -c "select hypertable_name from timescaledb_information.hypertables where hypertable_schema = 'public'"   # evse_status_observation, location_status_observation
docker compose exec db psql -U chargeglobe -d chargeglobe -c "select view_name, materialized_only from timescaledb_information.continuous_aggregates"   # evse_occupancy_hourly, f (Real-Time-Aggregation)
docker compose exec db psql -U chargeglobe -d chargeglobe -c "select code, name, round(ST_Area(geom)/1e6) km2 from region order by 1"   # AT-1 … AT-9 aus dem OSM-Seed
docker compose exec db psql -U chargeglobe -d chargeglobe -c "select region_code, count(*) from location group by 1 order by 1"   # Standorte je Bundesland
docker compose exec db psql -U chargeglobe -d chargeglobe -c "select provider_key, job_type, region_code, next_run_at, leased_by, last_result from poll_plan order by 1,2,3"
docker compose exec db psql -U chargeglobe -d chargeglobe -c "select provider_key, sum(requests) requests, count(*) runs from crawler_run where started_at > now() - interval '1 hour' group by 1"   # Abrufvolumen der letzten Stunde (≤ provider.request_budget_per_hour)
docker compose exec db psql -U chargeglobe -d chargeglobe -c "select priority, count(*), min(last_polled_at) from poll_tile group by 1"   # Abfragepunkte je Priorität
docker compose exec db psql -U chargeglobe -d chargeglobe -c "select * from provider_backoff"   # Pause/Sperre nach Fehlern der Quelle
docker compose exec db psql -U chargeglobe -d chargeglobe -c "select (select count(*) from evse_price_current) aktuell, (select count(*) from price_observation) aenderungen"   # Ad-hoc-Preise nach einem Register-Durchlauf
docker compose exec db psql -U chargeglobe -d chargeglobe -c "select count(*) filter (where processed_at is null) offen, count(*) filter (where error is not null) fehler from ingest_queue"   # Rückstand des Workers; nach einem Register-Lauf in wenigen Minuten wieder 0 (docs/05, Durchsatz)
docker compose exec db psql -U chargeglobe -d chargeglobe -c "select (select count(*) from location) standorte, (select count(*) from evse) evses, (select count(*) from evse_status_current) status"
docker compose down                         # ./data (Postgres-Cluster, Zertifikat) bleibt erhalten, auch mit -v
docker compose down && sudo rm -rf data && docker compose up -d   # Umgebung zurücksetzen: leere Instanz mit Setup-Assistent

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

# Deploy auf den Live-Server srv-dev1 (per SSH laut CLAUDE.local.md)
cd /etc/docker/compose/chargeglobe && git pull --ff-only origin master
docker compose up -d --build
curl -k https://127.0.0.1:28060/api/v1/health   # {"status":"ok","db":"ok"}
curl https://chargeglobe.net/api/v1/health       # über Cloudflare und den nginx des Hosts
systemctl status docker-compose@chargeglobe      # Start beim Booten
nginx -t && systemctl reload nginx               # nach Änderungen an /etc/nginx/sites-enabled/chargeglobe.net.conf
```

Vor einem neuen lokalen Coverage-Lauf alte Ergebnisse löschen (`tests/*/TestResults`, `artifacts/`), sonst mischt ReportGenerator alte und neue Berichte; im Compose-Testpfad erledigt das der Container. Bei einer Coverage-Lücke steht die Liste der nicht abgedeckten Zeilen in der Ausgabe von `scripts/test.sh` (`Datei:Zeile`). Niemals `docker system prune`, `docker volume prune` oder andere globale Aufräumbefehle auf dem Dev-Server ausführen.

## Nächste Schritte (Stand jetzt)

Siehe `docs/09-roadmap.md`: reale Ladekarten-Tarife pflegen, eigene Karten in den Einstellungen, Routing mit Valhalla, zweite Datenquelle (NAP/AFIR oder OpenChargeMap), Mail-Absender auf `chargeglobe.net`; außerdem Lizenz festlegen und die E-Control-Nutzungsbedingungen archivieren. API-Keys von Quellen werden nur im Admin-UI eingetragen (`provider.api_key`, Scope `provider:<key>`), nie in Dateien.
