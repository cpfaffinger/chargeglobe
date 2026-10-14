# 06 – Öffentliche API (v1)

Basis: `https://<host>:5005/api/v1` (HTTPS-only, am App-Port mit self-signed Zertifikat), JSON, UTF-8, Zeiten in ISO 8601 UTC, Geld als Dezimalstring + Währung. OpenAPI 3.1 unter `/openapi/v1.json`, interaktive Doku unter `/scalar`. Fehler nach RFC 9457 (Problem Details).

## Authentifizierung & Autorisierung

| Schema | Für wen | Mechanik |
|---|---|---|
| anonym | nur die Karte: `GET /locations`, `GET /locations/{id}`, `GET /tiles/…`, `GET /map/config`; dazu `GET /health`, `GET /permissions`, `/auth/*`, `/setup` | `/auth/*`: Limit je Minute und IP aus der Einstellung `auth.rate_limit_per_minute` (Default 20), danach `429` mit `Retry-After` |
| `Bearer` (JWT, HS256) | Angular-SPA, Menschen | `POST /auth/login` (E-Mail + Passwort) → Access-Token (15 min) im Body + Refresh-Token als Cookie `cg_refresh` (HttpOnly, Secure, SameSite=Strict, Pfad `/api/v1/auth`, 14 Tage); `POST /auth/refresh` tauscht das Cookie gegen ein neues Access-Token und ein neues Cookie (jedes Refresh-Token gilt einmal; ein bereits benutztes beendet alle Sitzungen des Nutzers); `POST /auth/logout` |
| `ApiKey` | KI-Agenten, Skripte, Integrationen | Header `X-Api-Key: cg_<prefix>_<secret>`; der Key trägt eine Teilmenge der Rechte seines Eigentümers und optional ein Ablaufdatum. Verliert der Eigentümer eine Permission, verliert sie auch der Key. Vorgesehen: eigenes Rate-Limit-Tier |

Beide Schemata münden in dieselben **Permission-Claims**. Jeder Endpunkt deklariert seine Anforderung in `x-required-permission`: eine Permission aus dem Katalog, `authenticated` (beliebiges Konto oder beliebiger API-Key) oder `none` (anonym). Rollen sind nur Bündel von Permissions und tauchen in der API-Autorisierung nicht auf. `401` und `403` kommen als Problem Details; `403` nennt die fehlende Permission im `detail` (`Missing permission: apikey:manage.`), `422` trägt die Feldfehler in `errors`.

Vorhandene Endpunkte (in Klammern die `operationId`):

| Methode & Pfad | Zweck | Permission |
|---|---|---|
| `GET /setup` (`getSetupStatus`) | `required: true`, solange kein Nutzerkonto existiert; die Web-App zeigt dann den Setup-Assistenten `/setup` | `none` |
| `POST /setup/admin` (`createInitialAdmin`) | Ersteinrichtung: Body `setupToken` (einmaliger Token aus dem API-Log, Zeile „Setup required: …“, nach einem Neustart ein neuer), `email`, `password` (mindestens 10 Zeichen). Legt das erste Konto mit der Rolle `Admin` und bestätigter Adresse an und antwortet wie `login` (`200` mit Access-Token + Refresh-Cookie). Gelingt genau einmal, danach `409`; `422` mit Feldfehlern `setupToken`, `email`, `password`; Rate-Limit wie `/auth/*` | `none` |
| `POST /auth/register` (`register`) | `409`, solange die Instanz keinen Nutzer hat (erst `createInitialAdmin`); sonst legt es ein Konto mit der Rolle `User` an und stellt die Bestätigungsmail in die Outbox. Body `email`, `password` (mindestens 10 Zeichen), optional `language` (`de`/`en`). Antwort immer `202`, egal ob die Adresse schon existiert | `none` |
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
| `GET /settings` (`listSettings`) | zentraler Config-Service: vollständiger Katalog mit `key`, `type` (`string`/`int`/`number`/`bool`/`enum`/`secret`), `description`, `default`, `allowedValues`, `min`, `max`, `scopeKinds`, den konkret editierbaren `scopes` (z. B. `global` oder `provider:econtrol`), dem wirksamen globalen `value` und den gespeicherten Werten je Scope in `stored`. Secrets erscheinen als `********`, wenn gesetzt, sonst `null` | `settings:read` |
| `GET /settings/{key}` (`getSetting`) | eine Einstellung in derselben Form; `404` bei unbekanntem Key | `settings:read` |
| `PUT /settings/{key}` (`updateSetting`) | Body `value` und optional `scope` (Default `global`). Speichert den Wert; `value: null` (oder weggelassen) entfernt ihn, sodass der Default wieder gilt. Idempotent, wirkt ohne Neustart in allen Containern. `422` mit Feldfehlern (`value`, `scope`) bei falschem Typ, Wert außerhalb der Grenzen oder nicht unterstütztem Scope; `404` bei unbekanntem Key | `settings:manage` |
| `POST /mail/test` (`sendTestMail`) | Body `to`. Sendet eine Testmail synchron mit den aktuellen `mail.*`-Einstellungen, auch bei `mail.enabled = false`. `200` mit Erfolgsmeldung; `422` mit Feldfehlern bei ungültiger Adresse (`to`) oder fehlenden Einstellungen (`mail.smtp.host`, `mail.from.address`); `502` als Problem Details mit der SMTP-Meldung im `detail` | `settings:manage` |
| `GET /mail/outbox` (`listMailOutbox`) | Postausgang, neueste zuerst: `data[]` mit `id`, `to`, `subject`, `templateKey`, `status`, `attempts`, `lastError`, `createdAt`, `sentAt`, `nextAttemptAt` und `meta` mit `total`, `page`, `pageSize`. Filter `status` (`QUEUED`, `SENT`, `FAILED`), Pagination `page` (ab 1) und `pageSize` (1–200, Default 50). Mail-Körper werden nie ausgegeben. `422` bei unbekanntem Status oder ungültiger Seite | `settings:manage` |
| `GET /locations` (`listLocations`) | aktive Standorte in `bbox=minLon,minLat,maxLon,maxLat` (WGS 84), nächstgelegene zur Box-Mitte zuerst, höchstens `limit` (1–2000, Default 500). Je Standort `id`, `name`, Adresse, `latitude`/`longitude`, `operator`, `evsesTotal`, `evsesAvailable` (Status `AVAILABLE`, nicht älter als `map.status_max_age_minutes`, Default 60), `maxPowerKw`, `statusObservedAt` (neuester Statuswert) und `availability` (`AVAILABLE`, `OCCUPIED`, `OUT_OF_ORDER`, `UNKNOWN`); `meta` mit `returned`, `limit`, `truncated` und `attribution` (je beteiligter Quelle `source`, `text`, `license` aus der Tabelle `source`). `422` mit Feldfehlern `bbox` bzw. `limit` | `none` |
| `GET /locations/{id}` (`getLocation`) | ein aktiver Standort (`data.location` wie oben) mit allen aktiven EVSEs (`data.evses`: `evseId`, `hasEvseId` – `false` bei einer Surrogat-ID von chargeglobe –, `status` mit `statusSince` und `statusObservedAt`, `UNKNOWN` wenn zu alt, `maxPowerKw`, `powerType`, `connectors` mit `standard`, `format`, `powerType`, `maxPowerKw`) und `meta.attribution`; `404` bei unbekannter Id | `none` |
| `GET /evses/{evseId}` (`getEvse`) | ein EVSE; `evseId` ist die EVSE-ID in beliebiger Schreibweise (`AT*VIE*E1234*1`, `AT-VIE-E1234-1`, `ATVIEE12341`, Groß-/Kleinschreibung egal, auch Surrogat-IDs `XX*CG*E…`) oder die interne UUID. `data`: `id`, `evseId`, `hasEvseId`, `lifecycle` (auch nicht mehr aktive EVSEs), `location` (`id`, Name, Adresse, `latitude`/`longitude`, `region` als ISO-3166-2-Code), `operator` (`id`, `name`, `operatorId`; ohne eigenen der des Standorts), `physicalReference`, `maxPowerKw`, `powerType`, `connectors`, `status` (`status`, `since`, `observedAt`, `source`, `stale` – älter als `map.status_max_age_minutes` oder fehlend; der Status wird hier nicht durch `UNKNOWN` ersetzt), `firstSeen`, `lastSeen`, `lastConfirmedAt`; `meta.attribution` aus den Quell-Datensätzen des EVSE. `404` bei unbekannter ID | `evse:read` |
| `GET /evses/{evseId}/status/history` (`getEvseStatusHistory`) | Statusintervalle, die [`from`, `to`) überlappen (`from` inklusive, `to` exklusiv, ISO 8601, ohne Offset UTC; Default `to` = jetzt, `from` = `to` − 7 Tage), älteste zuerst, nicht beschnitten (`to: null` = andauernd): `data` mit `id`, `evseId`, `from`, `to`, `intervals[]` (`status`, `from`, `to`, `source`, `confidence`); optional `source` (Quellen-Key). `422` mit Feldfehler `from` bzw. `to` bei unlesbaren Zeiten, `from` nicht vor `to` oder mehr als 90 Tagen; `404` bei unbekannter ID | `evse:read` |
| `GET /evses/{evseId}/occupancy` (`getEvseOccupancy`) | Belegungsprofil der letzten `weeks` Wochen (1–12, Default 4) bis jetzt aus `evse_occupancy_hourly`: `data.days[]` mit sieben Zeilen ab Montag (`weekday` 1–7, `day` z. B. `MONDAY`), je 24 Werte `chargingShare` (Anteil der Statusmeldungen mit `CHARGING`, 0–1, `null` ohne Meldung) und `observations`; dazu `utilization` (Anteil über den ganzen Zeitraum), `observations`, `from`, `to`, `weeks`, `timeZone` (IANA-Name für Wochentag und Stunde, Parameter `timeZone`, Default `Europe/Vienna`). Die Anteile sind Stichproben im Abfrageintervall der Quellen, keine gemessenen Dauern. `422` bei `weeks` außerhalb 1–12 oder unbekannter Zeitzone, `404` bei unbekannter ID | `evse:read` |
| `GET /map/config` (`getMapConfig`) | `styleUrl`, `attribution`, `center` (`latitude`, `longitude`) und `zoom` der Basiskarte aus den Einstellungen `map.*`; die Kartenseite der Web-App lädt sie bei jedem Aufruf, Änderungen wirken also nach einem Neuladen der Seite. Kacheln, Glyphen und Sprites holt der Browser direkt von den Hosts der Style-URL (Default: `tiles.openfreemap.org`), nicht über die API | `none` |

Schreibende Aktionen (Ersteinrichtung, Registrierung, Bestätigung, Login, Passwort-Reset anfordern/ausführen, Key anlegen/widerrufen, Einstellung ändern, Testmail senden) landen mit Akteur (Nutzer bzw. API-Key) im `audit_log`; bei Einstellungen ohne den Wert.

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

Vorhanden sind `GET /evses/{evseId}` (Detail inklusive aktuellem Status), `GET /evses/{evseId}/status/history` und `GET /evses/{evseId}/occupancy` (siehe Tabelle der vorhandenen Endpunkte oben). Vorgesehen:

| Methode & Pfad | Zweck | Wichtige Parameter |
|---|---|---|
| `GET /evses` | Suche/Liste | `bbox=minLon,minLat,maxLon,maxLat` **oder** `near=lat,lon&radius=m`, `status=AVAILABLE,…`, `minPowerKw`, `connector=IEC_62196_T2_COMBO,…`, `operator`, `region=AT-9`, `emp=oeamtc` (nur EVSEs mit bekanntem Preis für diesen EMP), `page`, `pageSize` (max 500) |
| `GET /evses/{evseId}/status/observations` | Rohbeobachtungen (alle Quellen) | `from`, `to` (max 7 Tage) |
| `GET /evses/{evseId}/prices` | Preise je EMP + Ad-hoc, inkl. Szenario-Kosten | `scenario=DC_50KW_30KWH_40MIN`, `at=<timestamp>` (historischer Stand) |
| `GET /evses/{evseId}/prices/history` | Preisbeobachtungen | `emp`, `from`, `to` |
| `GET /evses/{evseId}/sources` | Provenienz: Quell-Datensätze, Match-Methode, Attribut-Herkunft | |

### Standorte (anonym, außer Historie: `evse:read`)

| Methode & Pfad | Zweck |
|---|---|
| `GET /locations`, `GET /locations/{id}` | vorhanden, siehe Tabelle der vorhandenen Endpunkte oben; vorgesehen sind zusätzlich die Filter von `/evses` (Stecker, Leistung, Betreiber, nur frei) |
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
- `GET /map/config` (vorhanden) – Style-URL der Basiskarte, Attributionstext, Start-Ausschnitt (aus dem Config-Service, `map.*`).
- `GET /tiles/{z}/{x}/{y}.mvt` (ab Phase 2) – Vector-Tiles mit Standort-Markern inkl. Status-Zusammenfassung; alternativ via pg_tileserv/Martin direkt aus Postgres.

### Routing (`route:read`)
`POST /routes` – berechnet eine Route auf OSM-Straßendaten. Body: `origin` und `destination` (`lat`, `lon`), optional `waypoints[]`, `profile` (`car`, Standard), `avoid` (`tolls`, `motorways`). Antwort: `geometry` (GeoJSON LineString), `distanceMeters`, `durationSeconds`, `legs[].maneuvers[]`, `meta.attribution`. Der Aufruf verändert nichts und ist beliebig wiederholbar; POST statt GET, damit Koordinaten nicht in URLs und Logs stehen. Start und Ziel werden nicht gespeichert.

## Beispiel: `GET /evses/AT-VIE-E1234-1`

```json
{
  "data": {
    "id": "01990a3e-5b1d-7d42-8c3a-6e9f1a2b3c4f",
    "evseId": "AT*VIE*E1234*1",
    "hasEvseId": true,
    "lifecycle": "ACTIVE",
    "location": { "id": "01990a3e-…", "name": "Parkgarage Stephansplatz", "address": "Stephansplatz 1", "postalCode": "1010", "city": "Wien", "country": "AT", "latitude": 48.2085, "longitude": 16.3731, "region": "AT-9" },
    "operator": { "id": "01990a3e-…", "name": "Wien Energie GmbH", "operatorId": "VIE" },
    "physicalReference": "1",
    "maxPowerKw": 22,
    "powerType": "AC_3_PHASE",
    "connectors": [ { "standard": "IEC_62196_T2", "format": "SOCKET", "powerType": "AC_3_PHASE", "maxPowerKw": 22 } ],
    "status": { "status": "CHARGING", "since": "2026-10-14T07:40:00+00:00", "observedAt": "2026-10-14T08:15:00+00:00", "source": "econtrol", "stale": false },
    "firstSeen": "2026-03-01T06:00:00+00:00",
    "lastSeen": "2026-10-14T08:15:00+00:00",
    "lastConfirmedAt": "2026-10-14T06:00:00+00:00"
  },
  "meta": { "attribution": [ { "source": "econtrol", "text": "Datenquelle: Ladestellenverzeichnis der E-Control (ladestellen.at)", "license": "…" } ] }
}
```

Vorgesehen sind zusätzlich `data.prices` (Szenario, Ad-hoc-Preis, Preise je EMP mit Szenario-Kosten) und `data.sources` (Quell-Datensätze mit Match-Methode und Confidence), sobald Preise und weitere Quellen angebunden sind.

## Querschnitt

- **Caching**: Listen/Status 30 s Output-Cache, Stammdaten 10 min, Historie 5 min; `ETag` überall.
- **Rate-Limits**: `/auth/*` je IP nach `auth.rate_limit_per_minute` (Default 20 req/min), `429` als Problem Details mit `Retry-After`. Vorgesehen für die übrigen Endpunkte: anonym 60 req/min & 5.000/Tag je IP; authentifiziert nach `rate_limit_tier` (Standard 600 req/min).
- **Attribution**: jede Antwort enthält `meta.attribution[]` mit Lizenztexten der beteiligten Quellen (Pflicht z. B. bei ODbL/CC BY).
- **Versionierung**: Breaking Changes nur über `/api/v2`; additive Felder jederzeit.
- **Bulk-Export**: täglicher Dump (`/exports/evses.csv.gz`, `/exports/evses.geojson.gz`, Tarife als JSON) für Forschung/Analysen – mit Lizenzhinweis.
- **Realtime (später)**: Server-Sent Events `GET /stream/status?bbox=` für Statuswechsel.
