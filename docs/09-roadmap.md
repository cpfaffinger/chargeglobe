# 09 – Roadmap & offene Entscheidungen

## Phasen

### Phase 0 – Fundament (jetzt)
- [x] Konzept (dieses Verzeichnis)
- [x] Git-Repository angelegt (`origin` = github.com/cpfaffinger/chargeglobe), `CLAUDE.md` mit Arbeitsregeln
- [x] .NET-10-SDK lokal installiert
- [x] Solution-Skelett (.NET 10), Angular-22-Workspace, `docker-compose.yml` + `.env.example` (db/api/crawler/worker/web, HTTPS-only auf Port 5005 mit self-signed Zertifikat, `CRAWLER_REPLICAS`/`WORKER_REPLICAS`), Test-Projekte mit Coverage-Reporting
- [ ] Zentraler Config-Service: Settings-Katalog, `app_setting`, typisierte Accessoren, LISTEN/NOTIFY-Invalidierung, `GET/PUT /settings`, generisches Admin-UI
- [ ] Identity-Grundgerüst: ASP.NET Core Identity, Permission-Katalog, Standardrollen, Selbstregistrierung mit E-Mail-Bestätigung, Login (JWT), Passwort-Reset, API-Keys, `GET /me`, OpenAPI mit Security Schemes und `x-required-permission`; Admin-Seed-User aus `.env`; Mail-Port `IEmailSender` mit `email_outbox`
- [ ] Mail-Service: SMTP-Versand der Outbox im Worker (MailKit), Einstellungen `mail.*` im Config-Service, `POST /mail/test`, `GET /mail/outbox`, Admin-UI ([ADR-0008](adr/0008-zentraler-mail-service-smtp.md)); setzt den Config-Service voraus
- [x] Erstes Deployment auf den Dev-Server (`/opt/chargeglobe`)
- [ ] Lizenz festlegen (siehe unten), `CONTRIBUTING.md`, Code of Conduct
- [ ] API-Key für das E-Control-Ladestellenverzeichnis beantragen (admin.ladestellen.at → API → registrieren), Nutzungsbedingungen prüfen und dokumentieren
- [ ] OpenChargeMap-API-Key beantragen

### Phase 1 – MVP Ost-Österreich (Wien, NÖ, OÖ, Bgld)
- [ ] Domain + Datenmodell + Migrationen (inkl. Hypertables)
- [ ] `Providers.Abstractions`, Ingest-Pipeline, Matching-Kaskade (`EvseId` Value Object existiert bereits)
- [ ] Adapter **E-Control** (Inventar + Status + Ad-hoc-Preis) – Tier 0, deckt bereits Status und Ad-hoc-Preis für ganz Österreich ab
- [ ] Adapter **OpenStreetMap** (Overpass) und **OpenChargeMap** als Ergänzung/Plausibilisierung
- [ ] Region-Polygone, Lease-Scheduler (`poll_plan`), `ingest_queue`, clusterweites Rate-Budget, adaptive Statusabfrage, Metriken
- [ ] API v1 (EVSE-Suche, Detail, Statushistorie, Belegung, Ad-hoc-Preis, Provenienz)
- [ ] Angular: Karte mit MapLibre und OSM-Vektorkacheln (OpenFreeMap, `GET /map/config`), eigener Standort per Geolocation, anonym nur Karte ([ADR-0007](adr/0007-karte-standort-routing.md))
- [ ] Angular: Detailseite mit Historie (angemeldet), Registrierung/Login/Passwort-Reset, Admin (Nutzer, Rollen, API-Keys, Einstellungen)
- [ ] Customizings: `GET/PUT /me/preferences`, Favoriten, Einstellungsseite
- [ ] Routing: Compose-Service `routing` (Valhalla, OSM-Extrakt Österreich), `IRoutingEngine`, `POST /routes`, Routen-Layer in der Karte
- [ ] Erste EMP-Tarife: 2–3 Ladekarten-Anbieter mit Tarif-Editor (manuell gepflegt), Szenario-Preisvergleich

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
- [ ] Generischer **DATEX-II-Adapter** (Mobilithek-AFIR-Angebote, Bundesnetzagentur-Register als Inventar-Fallback)
- [ ] Mehrsprachigkeit (de/en/it/fr) in API-Texten und Frontend

### Phase 4 – Italien
- [ ] PUN/NAP-Zugang klären (vermutlich Anfrage an GSE/MIT nötig), Südtirol als Pilot über OCM/OSM + CPO-Web-Karten

## Offene Entscheidungen

| Thema | Optionen | Empfehlung |
|---|---|---|
| **Code-Lizenz** | MIT / Apache-2.0 / AGPL-3.0 | **AGPL-3.0**: verhindert, dass Dritte die Crawler-Infrastruktur als geschlossenen Dienst betreiben, ohne Verbesserungen zurückzugeben. MIT, wenn maximale Verbreitung wichtiger ist. |
| **Daten-Lizenz der API** | CC BY 4.0 / ODbL / „nur mit Quellenlizenzen“ | Jede Antwort trägt die Lizenzen der beteiligten Quellen (`meta.attribution`). Eine eigene Sammel-Lizenz (z. B. ODbL für die Kompilation) erst, wenn die Nutzungsbedingungen der E-Control-API das erlauben – die Weiterverbreitung ihrer Daten ist laut Nutzungsbedingungen an Auflagen gebunden. |
| **Hosting** | entschieden: Docker Compose auf dem Dev-Server (`/opt/chargeglobe`, HTTPS-Port 5005 mit self-signed Zertifikat, gültiges Zertifikat im externen Loadbalancer) | Produktiv-Hosting später separat entscheiden; Ressourcen reichen für Phase 1–2 (DB < 50 GB). |
| **Realtime-API** | SSE / WebSocket / Polling | SSE ab Phase 2, Polling im MVP. |
| **Benachrichtigungen** (Alerts) | keine / später | keine im MVP; Favoriten und persönliche Einstellungen sind entschieden ([ADR-0005](adr/0005-identity-rbac-openapi.md)), Alerts bauen später auf dem Mail-Service auf. |
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
