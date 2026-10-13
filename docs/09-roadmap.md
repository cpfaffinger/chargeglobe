# 09 – Roadmap & offene Entscheidungen

## Phasen

### Phase 0 – Fundament (jetzt)
- [x] Konzept (dieses Verzeichnis)
- [x] Git-Repository angelegt (`origin` = github.com/cpfaffinger/chargeglobe), `CLAUDE.md` mit Arbeitsregeln
- [x] .NET-10-SDK lokal installiert
- [x] Solution-Skelett (.NET 10), Angular-22-Workspace, `docker-compose.yml` + `.env.example` (db/api/crawler/worker/web, Port 5005, `CRAWLER_REPLICAS`/`WORKER_REPLICAS`), Test-Projekte mit Coverage-Reporting
- [ ] Zentraler Config-Service: Settings-Katalog, `app_setting`, typisierte Accessoren, LISTEN/NOTIFY-Invalidierung, `GET/PUT /settings`, generisches Admin-UI
- [ ] Identity-Grundgerüst: ASP.NET Core Identity, Permission-Katalog, Standardrollen, Login (JWT), API-Keys, `GET /me`, OpenAPI mit Security Schemes und `x-required-permission`; Admin-Seed-User aus `.env`
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
- [ ] Angular: Karte, Detailseite mit Historie, Login, Admin (Nutzer, Rollen, API-Keys)
- [ ] Erste EMP-Tarife: 2–3 Ladekarten-Anbieter mit Tarif-Editor (manuell gepflegt), Szenario-Preisvergleich

### Phase 2 – Preise & Tier-1/2-Quellen
- [ ] Analyse und Adapter je Anbieter nach Priorität aus [07](07-bestandsanalyse-apps-at.md) (Web-Karten zuerst)
- [ ] Automatisches Tarif-Tracking je EMP, Preisänderungs-Feed
- [ ] Status-Zweitquellen (CPO-direkt) für Latenz-/Qualitätsvergleich mit E-Control
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
| **Hosting** | entschieden: Docker Compose auf dem Dev-Server (`/opt/chargeglobe`, HTTP-Port 5005, Reverse Proxy/TLS außerhalb der App) | Produktiv-Hosting später separat entscheiden; Ressourcen reichen für Phase 1–2 (DB < 50 GB). |
| **Realtime-API** | SSE / WebSocket / Polling | SSE ab Phase 2, Polling im MVP. |
| **Endnutzer-Features** (Favoriten, Alerts) | keine / später | keine im MVP; Nutzerkonten gibt es nur für Pflege, Admin und API-Zugang. |
| **Identity-Provider** | nur lokale Konten / zusätzlich OIDC (GitHub, Google, Entra) | lokal im MVP; OIDC-Login später additiv über ASP.NET Core Identity External Logins. |
| **Tesla-Quelle** | Tesla-Standortkarte (öffentlich) vs. Owner-API (Login) | nur öffentliche Standortdaten + Roaming-EVSE-IDs aus E-Control; keine Owner-API. |

## Risiken

| Risiko | Gegenmaßnahme |
|---|---|
| E-Control-Nutzungsbedingungen erlauben keine Weiterverbreitung in gewünschter Form | frühe Kontaktaufnahme; Daten nur als „Zugriff über unsere API mit Attribution“, kein Bulk-Dump der E-Control-Rohdaten; alternative Open-Data-Freigabe (OSM hat eine bekommen) anfragen |
| Format-Änderungen bei Tier-1/2-Quellen | Replay-Fixtures, Health-Probes, Alarm bei Mapping-Quote < 95 % |
| Rate-Limits (E-Control: 2.500 Requests/h, 30 parallel) | Abfrageraster auf Basis bekannter Standorte planen (Deckung mit ≤ 200 Requests je 5-min-Zyklus für Ost-Österreich), adaptive Intervalle |
| Rechtliche Beschwerden einzelner Anbieter | Kill-Switch je Provider, Kontaktadresse, dokumentierter Takedown-Prozess |
| Datenqualität des amtlichen Verzeichnisses (Verzögerungen bei Meldungen) | Zweitquellen und Konflikt-Queue; Frische je Quelle in der API sichtbar |
