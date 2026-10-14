# 09 – Roadmap & offene Entscheidungen

## Phasen

### Phase 0 – Fundament (jetzt)
- [x] Konzept (dieses Verzeichnis)
- [x] Git-Repository angelegt (`origin` = github.com/cpfaffinger/chargeglobe), `CLAUDE.md` mit Arbeitsregeln
- [x] .NET-10-SDK lokal installiert
- [x] Solution-Skelett (.NET 10), Angular-22-Workspace, `docker-compose.yml` + `.env.example` (db/api/crawler/worker/web, HTTPS-only auf Port 5005 mit self-signed Zertifikat, `CRAWLER_REPLICAS`/`WORKER_REPLICAS`), Test-Projekte mit Coverage-Reporting
- [x] Compose-Testpfad `docker-compose.test.yml` + `scripts/test.sh` für Testläufe auf dem Dev-Server ([ADR-0010](adr/0010-tests-auf-dev-server-per-compose.md))
- [x] Zentraler Config-Service: Settings-Katalog, `app_setting`, typisierte Accessoren, LISTEN/NOTIFY-Invalidierung, `GET/PUT /settings`, generisches Admin-UI (`/admin/settings`), `GET /map/config`; Einstellungen `auth.rate_limit_per_minute`, `mail.*`, `map.*`
- [x] Identity-Grundgerüst: ASP.NET Core Identity, Permission-Katalog, Standardrollen, Selbstregistrierung mit E-Mail-Bestätigung, Login (JWT), Passwort-Reset, API-Keys, `GET /me`, OpenAPI mit Security Schemes und `x-required-permission`; Admin-Seed-User aus `.env`; Mail-Port `IEmailSender` mit `email_outbox`; Angular-Seiten für Registrierung, Login und Passwort-Reset mit Guard nach Permission
- [x] Mail-Service: SMTP-Versand der Outbox im Worker (MailKit) mit den Einstellungen `mail.*`, Backoff und Retention, `POST /mail/test`, `GET /mail/outbox`, Admin-Seite `/admin/mail` ([ADR-0008](adr/0008-zentraler-mail-service-smtp.md))
- [x] Live-Betrieb unter `https://chargeglobe.net` (Cloudflare → nginx des Hosts → App-Port), Stack in `/etc/docker/compose/chargeglobe` mit systemd-Unit; Tests weiter auf dem Testserver
- [x] Persistente Daten nur in Bind-Mounts unter `./data/` statt Docker-Volumes ([ADR-0011](adr/0011-bind-mounts-statt-volumes.md))
- [ ] Lizenz festlegen (siehe unten), `CONTRIBUTING.md`, Code of Conduct
- [x] API-Key für das E-Control-Ladestellenverzeichnis (an eine Domain gebunden, derzeit Platzhalter; Key nur im Config-Service)
- [ ] Nutzungsbedingungen der E-Control-API archivieren, Weiterverbreitung schriftlich klären, Go-Live-Meldung
- [ ] OpenChargeMap-API-Key beantragen

### Phase 1 – MVP Ost-Österreich (Wien, NÖ, OÖ, Bgld)
- [x] Domain + Datenmodell + Migration `ChargingDomain` (PostGIS-Geometrie mit GiST, Hypertables mit Kompression/Retention, Region-Trigger)
- [x] `Providers.Abstractions` (Descriptor, Capabilities, Quell-Interfaces, Ingest-Modell, Status-Mapping)
- [x] Ingest-Pipeline: Lease-Scheduler (`poll_plan`), clusterweites Request-Budget (`provider_rate_budget`), `raw_snapshot`, `ingest_queue`, Merge mit Matching-Schritten 1/2/5, Status-Intervalle, Retention unter `job_lease`
- [x] Continuous Aggregate `evse_occupancy_hourly` (stündlich je EVSE, Real-Time-Aggregation, Refresh-Policy)
- [x] Region-Seed: die neun Bundesländer aus OSM-Grenzen (`regions-at.geojson`, ODbL, Skript `scripts/regions/build-regions-at.sh`), Import beim API-Start, `location.region_code` wird neu berechnet; `RegionBounds` aus den Polygonen abgeleitet
- [ ] Matching-Heuristiken (Geo + Operator), Quellen-Präzedenz je Attribut, adaptive Intervalle
- [x] Adapter **E-Control** (Inventar und Ad-hoc-Preis seitenweise aus dem Register `GET /search/stations` – ganz Österreich in 17 Requests, ein Inventar-Plan für alle Regionen –, Live-Status über `GET /search` je Abfragepunkt, Health-Probe im Crawler, Provider-Einstellungen je Quelle im Config-Service) – Tier 0. Die DATEX-II-Publikationen der Quelle sind live geprüft, der Status-Snapshot ist aber Stunden alt und wird nicht genutzt ([Endpunkt-Notiz](endpunkte/econtrol.md))
- [ ] Adapter **OpenStreetMap** (Overpass) und **OpenChargeMap** als Ergänzung/Plausibilisierung
- [x] Passive Statusabfrage: Stundenbudget je Quelle clusterweit, Abfragepunkte aus den bekannten Standorten mit Priorität nach Aktivität (5/15/60 min, nachts ×3), Abruf bei Bedarf beim Öffnen eines Standorts, linear wachsende Pause nach Fehlern und Sperre bei abgelehntem Key, Lauf-Protokoll `crawler_run`, strukturierte Crawler-Logs, Live-Ansicht `/admin/crawler` ([05](05-crawling-konzept.md))
- [ ] Metriken (OpenTelemetry → Prometheus/Grafana): Requests, Fehlerquote, Datenfrische je Quelle
- [x] API v1: `GET /evses/{evseId}` (EVSE-ID in jeder Schreibweise oder UUID), Statushistorie, Belegung (`evse:read`)
- [ ] API v1: EVSE-Suche (`GET /evses`), Ad-hoc-Preis, Provenienz
- [x] Angular: Karte mit MapLibre und OSM-Vektorkacheln (OpenFreeMap, `GET /map/config`) als anonyme Startseite, eigener Standort per Geolocation ([ADR-0007](adr/0007-karte-standort-routing.md))
- [x] `GET /locations` (bbox, aggregiert, Attribution) und `GET /locations/{id}`; Ladepunkt-Marker mit Clustering, Status-Farben, Legende, Infokarte und Quellenangabe
- [x] Design auf Angular-Basis ([ADR-0012](adr/0012-design-system-und-theming.md)): Angular Material 3 + CDK, Palette „Volt“ und Roboto Flex (selbst gehostet), Light/Dark/System über `ThemeService` mit Umschalter in Toolbar bzw. Seitenmenü, Karte mit dunklem Stil (`map.style_url_dark`) und Markern in Statusfarbe mit Symbol, Stepper im Setup, Akkordeon mit Suche in den Einstellungen, Tabelle mit Paginator im Postausgang, Seiten außer der Karte als Lazy-Chunks
- [x] Zusatzattribute aus E-Control: Zahlungs- und Authentifizierungsarten (`AuthMethod`), Roaming, Preis-URL, Ökostrom, Parken (kostenlos, überdacht, barrierefreie Plätze) im Ingest-Modell, in `location`/`evse` und in `GET /locations`, `GET /locations/{id}`, `GET /evses/{evseId}`; Stecker- und Zahlungsarten-Tabellen gegen die Wertelisten der Quelle geprüft. `providers` sind Dienstleister, keine Ladekarten, und werden nicht übernommen
- [ ] Filter in der Karte (Stecker, Leistung, Betreiber, nur frei, Ad-hoc-Zahlung, Roaming, Ökostrom, barrierefrei)
- [ ] Ad-hoc-Preise aus E-Control speichern (`PRICE_POLL` aus dem Register, `price_observation`, `GET /evses/{evseId}/prices`)
- [ ] Lebenszyklus: Standorte und EVSEs, die eine Quelle nicht mehr meldet oder als inaktiv führt, auf `REMOVED` setzen
- [x] Angular: Standort-Detailseite `/locations/:id` (angemeldet, `evse:read`) mit Status-Timeline je EVSE und Heatmap Wochentag × Stunde, Link „Details“ in der Infokarte
- [x] Ersteinrichtung: Setup-Assistent `/setup` bei leerer Instanz (erster Admin mit Setup-Token aus dem API-Log, danach Datenquelle E-Control und SMTP), `GET /setup`, `POST /setup/admin`; Registrierung bis dahin geschlossen
- [x] Administration `/admin` mit Unterseiten nach Kategorie (Nutzer, Rollen, Crawler, E-Mail, Einstellungen) und Dropdown in der Toolbar; Nutzer- und Rollenverwaltung in der API (`/users`, `/roles`, Permission `user:read`), Admin-Freischaltung neuer Konten (`auth.require_admin_approval`) und E-Mail-Bestätigung als Einstellung (`auth.require_confirmed_email`), Login-Codes mit deutschen Texten und „Bestätigungsmail erneut senden“; Setup-Assistent nur bei leerer Instanz
- [x] Suche nach EVSE-ID, Name, Straße, PLZ und Ort (`GET /search`, `pg_trgm`) mit Suchfeld in der Toolbar, das zum Standort fliegt und die Infokarte öffnet
- [x] Standortdetails direkt in der Infokarte der Karte (für alle Ladepunkte mit Status und Steckern, mit `evse:read` Verlauf und Auslastung), gemerkter Kartenausschnitt und eigene Startposition im Browser mit Knopf „Startposition“, ein Standort-Request zugleich ohne Abfrage beim Hineinzoomen, MapLibre lädt parallel zur Konfiguration
- [ ] Startposition und Kartenausschnitt angemeldeter Nutzer serverseitig in `/me/preferences` (geräteübergreifend)
- [ ] Angular: API-Keys fremder Konten, Audit-Log (`GET /audit-log`)
- [ ] Customizings: `GET/PUT /me/preferences`, Favoriten, Einstellungsseite
- [ ] Routing: Compose-Service `routing` (Valhalla, OSM-Extrakt Österreich), `IRoutingEngine`, `POST /routes`, Routen-Layer in der Karte
- [ ] Erste EMP-Tarife: 2–3 Ladekarten-Anbieter mit Tarif-Editor (manuell gepflegt), Szenario-Preisvergleich; welche Ladekarte an welchem Ladepunkt geht, liefert die E-Control nicht (nur `roaming` ja/nein) – Quelle dafür sind die Roaming-Listen der EMPs ([07](07-bestandsanalyse-apps-at.md))

### Phase 2 – Preise & Tier-1/2-Quellen
- [ ] Analyse und Adapter je Anbieter nach Priorität aus [07](07-bestandsanalyse-apps-at.md) (Web-Karten zuerst)
- [ ] Automatisches Tarif-Tracking je EMP, Preisänderungs-Feed
- [ ] Status-Zweitquellen (CPO-direkt) für Latenz-/Qualitätsvergleich mit E-Control
- [ ] Ladepunkte entlang einer Route (Korridorabfrage), OSM-Extrakt-Aktualisierung als Worker-Job
- [ ] Vector-Tiles, Admin-UI (Konflikte, Match-Review, Provider-Health), Vorschlags-Workflow für Agenten (`proposal:write`)
- [ ] MCP-Server aus der OpenAPI-Beschreibung generieren (optional)
- [ ] Ganz Österreich

### Phase 3 – DACH
- [ ] Generischer **OCPI-Adapter** (zuerst für die Schweizer BFE-API, die ab November 2026 öffentlich OCPI liefert)
- [ ] Generischer **DATEX-II-Adapter** (Mobilithek-AFIR-Angebote, Bundesnetzagentur-Register als Inventar-Fallback), getestet an den DATEX-v3.5-Publikationen der E-Control
- [ ] Mehrsprachigkeit (de/en/it/fr) in API-Texten und Frontend

### Phase 4 – Italien
- [ ] PUN/NAP-Zugang klären (vermutlich Anfrage an GSE/MIT nötig), Südtirol als Pilot über OCM/OSM + CPO-Web-Karten

## Offene Entscheidungen

| Thema | Optionen | Empfehlung |
|---|---|---|
| **Code-Lizenz** | MIT / Apache-2.0 / AGPL-3.0 | **AGPL-3.0**: verhindert, dass Dritte die Crawler-Infrastruktur als geschlossenen Dienst betreiben, ohne Verbesserungen zurückzugeben. MIT, wenn maximale Verbreitung wichtiger ist. |
| **Daten-Lizenz der API** | CC BY 4.0 / ODbL / „nur mit Quellenlizenzen“ | Jede Antwort trägt die Lizenzen der beteiligten Quellen (`meta.attribution`). Eine eigene Sammel-Lizenz (z. B. ODbL für die Kompilation) erst, wenn die Nutzungsbedingungen der E-Control-API das erlauben – die Weiterverbreitung ihrer Daten ist laut Nutzungsbedingungen an Auflagen gebunden. |
| **Hosting** | entschieden: Docker Compose auf einem gemeinsam genutzten Debian-Host (`/etc/docker/compose/chargeglobe`, App-Port nur lokal, davor nginx des Hosts mit Cloudflare-Origin-Zertifikat und Cloudflare-Proxy), Domain `chargeglobe.net` | Der Host hat 8 GB RAM für viele Dienste; bei Wachstum (Statushistorie, weitere Quellen) eigener Host. |
| **Realtime-API** | SSE / WebSocket / Polling | SSE ab Phase 2, Polling im MVP. |
| **Benachrichtigungen** (Alerts) | keine / später | keine im MVP; Favoriten und persönliche Einstellungen sind entschieden ([ADR-0005](adr/0005-identity-rbac-openapi.md)), Alerts bauen später auf dem Mail-Service auf. |
| **UI-Design** | entschieden: Angular Material (Material 3) + Angular CDK mit Light und Dark Mode, Palette „Volt“, Roboto Flex | umgesetzt, siehe [ADR-0012](adr/0012-design-system-und-theming.md); Wahl des Modus später als Customizing im Benutzerprofil |
| **Karten-Kacheln selbst hosten** | öffentliche OpenFreeMap-Instanz / eigener Kachel-Service im Compose-Stack | öffentliche Instanz, solange sie zuverlässig ist; Wechsel über `map.style_url` ohne Codeänderung ([ADR-0007](adr/0007-karte-standort-routing.md)). |
| **Identity-Provider** | nur lokale Konten / zusätzlich OIDC (GitHub, Google, Entra) | lokal im MVP; OIDC-Login später additiv über ASP.NET Core Identity External Logins. |
| **Tesla-Quelle** | Tesla-Standortkarte (öffentlich) vs. Owner-API (Login) | nur öffentliche Standortdaten + Roaming-EVSE-IDs aus E-Control; keine Owner-API. |

## Risiken

| Risiko | Gegenmaßnahme |
|---|---|
| E-Control-Nutzungsbedingungen erlauben keine Weiterverbreitung in gewünschter Form | frühe Kontaktaufnahme; Daten nur als „Zugriff über unsere API mit Attribution“, kein Bulk-Dump der E-Control-Rohdaten; alternative Open-Data-Freigabe (OSM hat eine bekommen) anfragen |
| Format-Änderungen bei Tier-1/2-Quellen | Replay-Fixtures, Health-Probes, Alarm bei Mapping-Quote < 95 % |
| Rate-Limits (E-Control: 2.500 Requests/h, 30 parallel) | Abfrageraster auf Basis bekannter Standorte planen (Deckung mit ≤ 200 Requests je 5-min-Zyklus für Ost-Österreich), adaptive Intervalle |
| Öffentliche Kachel-Instanz (OpenFreeMap) fällt aus oder führt Limits ein | Style-URL ist Konfiguration; Self-Hosting der Kacheln als vorbereiteter Ausweg |
| Routing-Graph braucht mit wachsendem Scope (DACH, Italien) deutlich mehr Speicher und Bauzeit | Extrakte je Region, Graphaufbau außerhalb der Spitzenzeiten, Ressourcen vor Phase 3 neu bewerten |
| Offene Registrierung zieht Missbrauch an (Fake-Konten, Scraping über Konten) | E-Mail-Bestätigung, Rate-Limits je Konto und IP, Sperren durch Admins |
| Rechtliche Beschwerden einzelner Anbieter | Kill-Switch je Provider, Kontaktadresse, dokumentierter Takedown-Prozess |
| Datenqualität des amtlichen Verzeichnisses (Verzögerungen bei Meldungen) | Zweitquellen und Konflikt-Queue; Frische je Quelle in der API sichtbar |
