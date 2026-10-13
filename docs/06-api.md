# 06 – Öffentliche API (v1)

Basis: `http://<host>:5005/api/v1`, JSON, UTF-8, Zeiten in ISO 8601 UTC, Geld als Dezimalstring + Währung. OpenAPI 3.1 unter `/openapi/v1.json`, interaktive Doku unter `/scalar`. Fehler nach RFC 9457 (Problem Details).

## Authentifizierung & Autorisierung

| Schema | Für wen | Mechanik |
|---|---|---|
| anonym | alle Lese-Endpunkte unter „Ladepunkte“, „Standorte“, „Stammdaten & Meta“ | Rate-Limit je IP |
| `Bearer` (JWT) | Angular-SPA, Menschen | `POST /auth/login` (E-Mail + Passwort) → Access-Token (15 min) + Refresh-Token (HttpOnly-Cookie, 14 Tage); `POST /auth/refresh`, `POST /auth/logout` |
| `ApiKey` | KI-Agenten, Skripte, Integrationen | Header `X-Api-Key: cg_<prefix>_<secret>`; Key trägt eine Teilmenge der Rechte seines Eigentümers, Ablaufdatum, eigenes Rate-Limit-Tier |

Beide Schemata münden in dieselben **Permission-Claims**. Jeder geschützte Endpunkt deklariert genau eine Permission (`x-required-permission` in OpenAPI). Rollen sind nur Bündel von Permissions und tauchen in der API-Autorisierung nicht auf.

| Methode & Pfad | Zweck | Permission |
|---|---|---|
| `GET /me` | eigene Identität, effektive Permissions, Rate-Limit-Tier | authentifiziert |
| `GET /permissions` | Katalog aller Permissions mit Beschreibung | anonym |
| `GET/POST/PUT/DELETE /users`, `PUT /users/{id}/roles`, `PUT /users/{id}/permissions` | Nutzerverwaltung | `user:manage` |
| `GET/POST/PUT/DELETE /roles` | Rollen und ihre Permissions | `role:manage` |
| `GET/POST /api-keys`, `DELETE /api-keys/{id}` | eigene API-Keys (Klartext nur in der POST-Antwort) | `apikey:manage` (für fremde Keys zusätzlich `user:manage`) |
| `GET /audit-log` | schreibende Aktionen, filterbar nach Akteur/Entität/Zeit | `audit:read` |
| `PUT /tariffs/{id}`, `POST /tariffs` | Tarif-Pflege | `tariff:write` |
| `POST /conflicts/{id}/resolve`, `POST /evses/{id}/merge` | Konflikt-/Match-Entscheidungen | `conflict:resolve` |
| `GET /settings`, `GET /settings/{key}`, `PUT /settings/{key}` | zentraler Config-Service: Katalog mit Typ/Default/Beschreibung und aktuellen Werten je Scope; Secrets werden maskiert zurückgegeben | `settings:read` / `settings:manage` |
| `POST /sources/{key}/run` | Job anstoßen | `provider:manage` |
| `GET /jobs/instances`, `GET /jobs/queue` | laufende Crawler-/Worker-Instanzen, Leases, Queue-Tiefe | `jobs:read` |
| `POST /proposals/tariffs`, `POST /proposals/conflicts` | Vorschläge einreichen (werden von Maintainern geprüft) | `proposal:write` (Standard für Rolle `Agent`) |

## Agenten-Tauglichkeit (OpenAPI als einzige Doku)

- **Vollständige OpenAPI 3.1**: jeder Endpunkt mit `operationId` (`listEvses`, `getEvseStatusHistory`, …), `summary`, mehrsätziger `description` (Zweck, Einheiten, typische Parameterkombinationen, Fallstricke), Tags, Beispiel-Request/-Response, dokumentierten Fehlerfällen (`401`, `403` mit fehlender Permission im `detail`, `404`, `409`, `422` mit Feldfehlern, `429` mit `Retry-After`).
- **Security Schemes** `bearerAuth` und `apiKeyAuth` sind im Dokument deklariert; `x-required-permission` je Operation, damit ein Agent vorab prüfen kann, ob sein Key reicht (`GET /me`).
- **Vorhersagbare Konventionen**: Pagination immer `page`/`pageSize` + `meta.total`, Filter als Query-Parameter mit Enum-Werten aus dem Schema, Sortierung `sort=feld:asc|desc`, Zeiten ISO 8601, IDs als Strings; keine HTML-Antworten, keine Redirects auf API-Pfaden.
- **Schreibende Aufrufe sicher wiederholbar**: `PUT` ist idempotent; `POST` akzeptiert `Idempotency-Key` (24 h gespeichert, gleiche Antwort bei Wiederholung).
- **Discovery**: `GET /.well-known/openapi` → Link auf das aktuelle Spec-Dokument; `GET /` liefert JSON mit Versionen, Spec-URL und Attribution.
- **Spätere Option**: ein MCP-Server, der aus derselben OpenAPI-Beschreibung generiert wird (Roadmap Phase 2); er bringt keine zusätzliche Logik, sondern ist nur eine weitere Darreichung derselben Operationen.

## Ressourcen

### Ladepunkte

| Methode & Pfad | Zweck | Wichtige Parameter |
|---|---|---|
| `GET /evses` | Suche/Liste | `bbox=minLon,minLat,maxLon,maxLat` **oder** `near=lat,lon&radius=m`, `status=AVAILABLE,…`, `minPowerKw`, `connector=IEC_62196_T2_COMBO,…`, `operator`, `region=AT-9`, `emp=oeamtc` (nur EVSEs mit bekanntem Preis für diesen EMP), `page`, `pageSize` (max 500) |
| `GET /evses/{evseId}` | Detail | `evseId` = EVSE-ID in beliebiger Schreibweise oder interne UUID |
| `GET /evses/{evseId}/status` | aktueller Status + Alter + Quelle | |
| `GET /evses/{evseId}/status/history` | Statusintervalle | `from`, `to` (max 90 Tage/Request), `source` |
| `GET /evses/{evseId}/status/observations` | Rohbeobachtungen (alle Quellen) | `from`, `to` (max 7 Tage) |
| `GET /evses/{evseId}/occupancy` | Auslastungsprofil | `weeks=4` → Matrix Wochentag × Stunde mit Anteil `CHARGING`, plus Gesamt-Utilization |
| `GET /evses/{evseId}/prices` | Preise je EMP + Ad-hoc, inkl. Szenario-Kosten | `scenario=DC_50KW_30KWH_40MIN`, `at=<timestamp>` (historischer Stand) |
| `GET /evses/{evseId}/prices/history` | Preisbeobachtungen | `emp`, `from`, `to` |
| `GET /evses/{evseId}/sources` | Provenienz: Quell-Datensätze, Match-Methode, Attribut-Herkunft | |

### Standorte

| Methode & Pfad | Zweck |
|---|---|
| `GET /locations` | wie `/evses`, aber aggregiert je Standort (`evsesTotal`, `evsesAvailable`, `maxPowerKw`, Betreiber) – für Karten-Marker |
| `GET /locations/{id}` | Standort mit allen Ladepunkten und Steckern |
| `GET /locations/{id}/status/history` | aggregierte Verfügbarkeit (frei/gesamt) über Zeit |

### Stammdaten & Meta

| Methode & Pfad | Zweck |
|---|---|
| `GET /operators`, `GET /emps`, `GET /regions` | Listen |
| `GET /tariffs?emp=&operator=` | Tarife (OCPI-Elemente), Versionen |
| `GET /sources` | Quellen mit Lizenz, Attribution, Capabilities, Health (`lastSuccess`, `freshnessP50`) |
| `GET /stats` | Zähler (EVSEs je Region/Betreiber/Leistungsklasse, Statusfrische) |
| `GET /changes?since=` | Feed von Änderungen (neue/entfernte EVSEs, Preisänderungen) für Downstream-Consumer |

### Karte (ab Phase 2)
`GET /tiles/{z}/{x}/{y}.mvt` – Vector-Tiles mit Standort-Markern inkl. Status-Zusammenfassung; alternativ via pg_tileserv/Martin direkt aus Postgres.

## Beispiel: `GET /evses/AT*EVN*E1234*5`

```json
{
  "id": "4c5f…",
  "evseId": "AT*EVN*E1234*5",
  "identityKind": "EVSE_ID",
  "location": { "id": "…", "name": "P+R Korneuburg", "address": "…", "coordinates": { "lat": 48.34, "lon": 16.33 }, "region": "AT-3" },
  "operator": { "id": "…", "name": "EVN", "operatorId": "EVN" },
  "physicalReference": "2",
  "maxPowerKw": 22,
  "currentType": "AC_3_PHASE",
  "connectors": [ { "standard": "IEC_62196_T2", "format": "SOCKET", "powerType": "AC_3_PHASE", "maxPowerKw": 22 } ],
  "status": { "status": "CHARGING", "since": "2026-10-13T18:02:11Z", "observedAt": "2026-10-13T19:40:03Z", "source": "evn", "stale": false },
  "prices": {
    "scenario": "AC_11KW_20KWH_2H",
    "adHoc":  { "pricePerKwh": "0.59", "currency": "EUR", "total": "11.80", "source": "econtrol", "observedAt": "2026-10-13T06:00:00Z" },
    "emps": [
      { "emp": "oeamtc", "product": "ePower Mitglied", "pricePerKwh": "0.49", "blockingFeePerMinute": "0.10", "blockingAfterMinutes": 240, "total": "9.80", "complete": true, "tariffId": "…" },
      { "emp": "electroverse", "pricePerKwh": "0.55", "total": "11.00", "complete": false }
    ]
  },
  "sources": [
    { "source": "econtrol", "externalId": "…", "matchMethod": "EVSE_ID_EXACT", "lastSeen": "…" },
    { "source": "evn",      "externalId": "…", "matchMethod": "EVSE_ID_EXACT", "lastSeen": "…" },
    { "source": "osm",      "externalId": "node/…", "matchMethod": "GEO_OPERATOR_HEURISTIC", "confidence": 0.8 }
  ],
  "lifecycle": "ACTIVE",
  "firstSeen": "2025-03-01T…", "lastConfirmedAt": "2026-10-13T…"
}
```

## Querschnitt

- **Caching**: Listen/Status 30 s Output-Cache, Stammdaten 10 min, Historie 5 min; `ETag` überall.
- **Rate-Limits**: anonym 60 req/min & 5.000/Tag je IP; authentifiziert nach `rate_limit_tier` (Standard 600 req/min). `429` mit `Retry-After`.
- **Attribution**: jede Antwort enthält `meta.attribution[]` mit Lizenztexten der beteiligten Quellen (Pflicht z. B. bei ODbL/CC BY).
- **Versionierung**: Breaking Changes nur über `/api/v2`; additive Felder jederzeit.
- **Bulk-Export**: täglicher Dump (`/exports/evses.csv.gz`, `/exports/evses.geojson.gz`, Tarife als JSON) für Forschung/Analysen – mit Lizenzhinweis.
- **Realtime (später)**: Server-Sent Events `GET /stream/status?bbox=` für Statuswechsel.
