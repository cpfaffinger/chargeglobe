# 06 – Öffentliche API (v1)

Basis: `https://<host>:5005/api/v1` (HTTPS-only, am App-Port mit self-signed Zertifikat), JSON, UTF-8, Zeiten in ISO 8601 UTC, Geld als Dezimalstring + Währung. OpenAPI 3.1 unter `/openapi/v1.json`, interaktive Doku unter `/scalar`. Fehler nach RFC 9457 (Problem Details).

## Authentifizierung & Autorisierung

| Schema | Für wen | Mechanik |
|---|---|---|
| anonym | nur die Karte: `GET /locations`, `GET /locations/{id}`, `GET /tiles/…`, `GET /map/config`; dazu `GET /health`, `GET /permissions`, `/auth/*` | `/auth/*`: 20 Requests je Minute und IP, danach `429` mit `Retry-After` |
| `Bearer` (JWT, HS256) | Angular-SPA, Menschen | `POST /auth/login` (E-Mail + Passwort) → Access-Token (15 min) im Body + Refresh-Token als Cookie `cg_refresh` (HttpOnly, Secure, SameSite=Strict, Pfad `/api/v1/auth`, 14 Tage); `POST /auth/refresh` tauscht das Cookie gegen ein neues Access-Token und ein neues Cookie (jedes Refresh-Token gilt einmal; ein bereits benutztes beendet alle Sitzungen des Nutzers); `POST /auth/logout` |
| `ApiKey` | KI-Agenten, Skripte, Integrationen | Header `X-Api-Key: cg_<prefix>_<secret>`; der Key trägt eine Teilmenge der Rechte seines Eigentümers und optional ein Ablaufdatum. Verliert der Eigentümer eine Permission, verliert sie auch der Key. Vorgesehen: eigenes Rate-Limit-Tier |

Beide Schemata münden in dieselben **Permission-Claims**. Jeder Endpunkt deklariert seine Anforderung in `x-required-permission`: eine Permission aus dem Katalog, `authenticated` (beliebiges Konto oder beliebiger API-Key) oder `none` (anonym). Rollen sind nur Bündel von Permissions und tauchen in der API-Autorisierung nicht auf. `401` und `403` kommen als Problem Details; `403` nennt die fehlende Permission im `detail` (`Missing permission: apikey:manage.`), `422` trägt die Feldfehler in `errors`.

Vorhandene Endpunkte (in Klammern die `operationId`):

| Methode & Pfad | Zweck | Permission |
|---|---|---|
| `POST /auth/register` (`register`) | legt ein Konto mit der Rolle `User` an und stellt die Bestätigungsmail in die Outbox. Body `email`, `password` (mindestens 10 Zeichen), optional `language` (`de`/`en`). Antwort immer `202`, egal ob die Adresse schon existiert | `none` |
| `POST /auth/confirm-email` (`confirmEmail`) | bestätigt die Adresse mit `userId` und `token` aus dem Mail-Link (24 h gültig, einmalig). `204`, bei ungültigem Token `422` | `none` |
| `POST /auth/login` (`login`) | `200` mit `accessToken`, `tokenType`, `expiresIn` + Refresh-Cookie; `401` bei falschen Zugangsdaten, `403` bei unbestätigter Adresse (entfällt mit `AUTH_REQUIRE_CONFIRMED_EMAIL=false`) | `none` |
| `POST /auth/refresh` (`refreshToken`) | neues Access-Token gegen das Refresh-Cookie, ohne Body; `401` ohne gültiges Cookie | `none` |
| `POST /auth/logout` (`logout`) | widerruft das Refresh-Token und löscht das Cookie; immer `204` | `none` |
| `POST /auth/password/forgot` (`requestPasswordReset`) | stellt die Reset-Mail in die Outbox, falls die Adresse zu einem Konto gehört; Antwort immer `202` | `none` |
| `POST /auth/password/reset` (`resetPassword`) | setzt mit `userId`, `token`, `newPassword` ein neues Passwort und beendet alle Browser-Sitzungen des Nutzers; `204` bzw. `422` | `none` |
| `GET /me` (`getMe`) | `id`, `email`, `roles`, effektive `permissions`, `authenticatedVia` (`bearer`/`apiKey`); bei einem API-Key beschreiben `id`/`email`/`roles` den Eigentümer und `permissions` den Key | `authenticated` |
| `GET /permissions` (`listPermissions`) | Katalog aller Permissions mit Beschreibung | `none` |
| `GET /api-keys` (`listApiKeys`) | eigene Keys inklusive widerrufener, ohne Secret | `apikey:manage` |
| `POST /api-keys` (`createApiKey`) | Body `name`, `permissions[]` (nicht leer, ⊆ Rechte des Aufrufers), optional `expiresAt`. `201` mit dem Klartext-Key in `key` – nur in dieser Antwort. Mit Header `Idempotency-Key` legt eine Wiederholung nichts an und antwortet `200` mit dem bestehenden Key und `key: null`, weil das Secret nicht wiederherstellbar ist | `apikey:manage` |
| `DELETE /api-keys/{id}` (`revokeApiKey`) | widerruft einen eigenen Key sofort; wiederholbar (`204`), `404` bei fremder oder unbekannter Id | `apikey:manage` |

Schreibende Aktionen (Registrierung, Bestätigung, Login, Passwort-Reset anfordern/ausführen, Key anlegen/widerrufen) landen mit Akteur (Nutzer bzw. API-Key) im `audit_log`.

Vorgesehene Endpunkte:

| Methode & Pfad | Zweck | Permission |
|---|---|---|
| `GET /me/preferences`, `PUT /me/preferences` | Customizings des Nutzers (Standard-Kartenausschnitt, gespeicherte Filter, eigene Ladekarten) | `authenticated` |
| `GET /me/favorites`, `PUT /me/favorites/{locationId}`, `DELETE /me/favorites/{locationId}` | Favoriten | `authenticated` |
| `GET/POST/PUT/DELETE /users`, `PUT /users/{id}/roles`, `PUT /users/{id}/permissions` | Nutzerverwaltung; fremde API-Keys verwalten | `user:manage` |
| `GET/POST/PUT/DELETE /roles` | Rollen und ihre Permissions | `role:manage` |
| `GET /audit-log` | schreibende Aktionen, filterbar nach Akteur/Entität/Zeit | `audit:read` |
| `PUT /tariffs/{id}`, `POST /tariffs` | Tarif-Pflege | `tariff:write` |
| `POST /conflicts/{id}/resolve`, `POST /evses/{id}/merge` | Konflikt-/Match-Entscheidungen | `conflict:resolve` |
| `GET /settings`, `GET /settings/{key}`, `PUT /settings/{key}` | zentraler Config-Service: Katalog mit Typ/Default/Beschreibung und aktuellen Werten je Scope; Secrets werden maskiert zurückgegeben | `settings:read` / `settings:manage` |
| `POST /mail/test`, `GET /mail/outbox` | Testmail über den konfigurierten SMTP-Server senden; Zustellstatus der Outbox | `settings:manage` |
| `POST /sources/{key}/run` | Job anstoßen | `provider:manage` |
| `GET /jobs/instances`, `GET /jobs/queue` | laufende Crawler-/Worker-Instanzen, Leases, Queue-Tiefe | `jobs:read` |
| `POST /proposals/tariffs`, `POST /proposals/conflicts` | Vorschläge einreichen (werden von Maintainern geprüft) | `proposal:write` (Standard für Rolle `Agent`) |

## Agenten-Tauglichkeit (OpenAPI als einzige Doku)

- **Vollständige OpenAPI 3.1**: jeder Endpunkt mit `operationId` (`listEvses`, `getEvseStatusHistory`, …), `summary`, mehrsätziger `description` (Zweck, Einheiten, typische Parameterkombinationen, Fallstricke), Tags, Beispiel-Request/-Response, dokumentierten Fehlerfällen (`401`, `403` mit fehlender Permission im `detail`, `404`, `409`, `422` mit Feldfehlern, `429` mit `Retry-After`).
- **Security Schemes** `bearerAuth` und `apiKeyAuth` sind im Dokument deklariert; `x-required-permission` je Operation, damit ein Agent vorab prüfen kann, ob sein Key reicht (`GET /me`). Ein Integrationstest lädt `/openapi/v1.json` und verlangt für jede Operation `operationId`, `summary`, `description`, mindestens einen Tag, `x-required-permission`, bei geschützten Operationen beide Security Schemes samt `401`/`403` und für jede erfolgreiche JSON-Antwort ein Beispiel.
- **Vorhersagbare Konventionen**: Pagination immer `page`/`pageSize` + `meta.total`, Filter als Query-Parameter mit Enum-Werten aus dem Schema, Sortierung `sort=feld:asc|desc`, Zeiten ISO 8601, IDs als Strings; keine HTML-Antworten, keine Redirects auf API-Pfaden.
- **Schreibende Aufrufe sicher wiederholbar**: `PUT` ist idempotent; `POST`, das etwas anlegt, akzeptiert `Idempotency-Key` (eine Wiederholung legt nichts Zweites an und liefert das bereits Angelegte; bei `createApiKey` ohne das Secret).
- **Discovery**: `GET /.well-known/openapi` → Link auf das aktuelle Spec-Dokument; `GET /` liefert JSON mit Versionen, Spec-URL und Attribution.
- **Spätere Option**: ein MCP-Server, der aus derselben OpenAPI-Beschreibung generiert wird (Roadmap Phase 2); er bringt keine zusätzliche Logik, sondern ist nur eine weitere Darreichung derselben Operationen.

## Ressourcen

Ohne Anmeldung sind nur die Endpunkte unter „Standorte“ (`GET /locations`, `GET /locations/{id}`) und „Karte“ erreichbar. Die übrigen Lese-Endpunkte verlangen die angegebene Permission; die Rolle `User` (Selbstregistrierung) enthält `evse:read`, `price:read` und `route:read`.

### Ladepunkte (`evse:read`, Preis-Endpunkte `price:read`)

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

### Standorte (anonym, außer Historie: `evse:read`)

| Methode & Pfad | Zweck |
|---|---|
| `GET /locations` | wie `/evses`, aber aggregiert je Standort (`evsesTotal`, `evsesAvailable`, `maxPowerKw`, Betreiber) – für Karten-Marker |
| `GET /locations/{id}` | Standort mit allen Ladepunkten und Steckern |
| `GET /locations/{id}/status/history` | aggregierte Verfügbarkeit (frei/gesamt) über Zeit |

### Stammdaten & Meta (`evse:read`)

| Methode & Pfad | Zweck |
|---|---|
| `GET /operators`, `GET /emps`, `GET /regions` | Listen |
| `GET /tariffs?emp=&operator=` | Tarife (OCPI-Elemente), Versionen |
| `GET /sources` | Quellen mit Lizenz, Attribution, Capabilities, Health (`lastSuccess`, `freshnessP50`) |
| `GET /stats` | Zähler (EVSEs je Region/Betreiber/Leistungsklasse, Statusfrische) |
| `GET /changes?since=` | Feed von Änderungen (neue/entfernte EVSEs, Preisänderungen) für Downstream-Consumer |

### Karte (anonym)
- `GET /map/config` – Style-URL der Basiskarte, Attributionstext, Start-Ausschnitt (aus dem Config-Service, `map.*`).
- `GET /tiles/{z}/{x}/{y}.mvt` (ab Phase 2) – Vector-Tiles mit Standort-Markern inkl. Status-Zusammenfassung; alternativ via pg_tileserv/Martin direkt aus Postgres.

### Routing (`route:read`)
`POST /routes` – berechnet eine Route auf OSM-Straßendaten. Body: `origin` und `destination` (`lat`, `lon`), optional `waypoints[]`, `profile` (`car`, Standard), `avoid` (`tolls`, `motorways`). Antwort: `geometry` (GeoJSON LineString), `distanceMeters`, `durationSeconds`, `legs[].maneuvers[]`, `meta.attribution`. Der Aufruf verändert nichts und ist beliebig wiederholbar; POST statt GET, damit Koordinaten nicht in URLs und Logs stehen. Start und Ziel werden nicht gespeichert.

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
- **Rate-Limits**: `/auth/*` 20 req/min je IP, `429` als Problem Details mit `Retry-After`. Vorgesehen für die übrigen Endpunkte: anonym 60 req/min & 5.000/Tag je IP; authentifiziert nach `rate_limit_tier` (Standard 600 req/min).
- **Attribution**: jede Antwort enthält `meta.attribution[]` mit Lizenztexten der beteiligten Quellen (Pflicht z. B. bei ODbL/CC BY).
- **Versionierung**: Breaking Changes nur über `/api/v2`; additive Felder jederzeit.
- **Bulk-Export**: täglicher Dump (`/exports/evses.csv.gz`, `/exports/evses.geojson.gz`, Tarife als JSON) für Forschung/Analysen – mit Lizenzhinweis.
- **Realtime (später)**: Server-Sent Events `GET /stream/status?bbox=` für Statuswechsel.
