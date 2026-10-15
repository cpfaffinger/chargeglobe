# 06 – Öffentliche API (v1)

Basis: `https://<host>:5005/api/v1` (HTTPS-only, am App-Port mit self-signed Zertifikat), JSON, UTF-8, Zeiten in ISO 8601 UTC, Geld als Dezimalstring + Währung. OpenAPI 3.1 unter `/openapi/v1.json`, interaktive Doku unter `/scalar`. Fehler nach RFC 9457 (Problem Details).

## Authentifizierung & Autorisierung

| Schema | Für wen | Mechanik |
|---|---|---|
| anonym | nur die Karte: `GET /locations`, `GET /locations/{id}`, `GET /search`, `GET /operators`, `GET /tiles/…`, `GET /map/config`; dazu `GET /health`, `GET /permissions`, `/auth/*`, `/setup` | `/auth/*`: Limit je Minute und IP aus der Einstellung `auth.rate_limit_per_minute` (Default 20), danach `429` mit `Retry-After` |
| `Bearer` (JWT, HS256) | Angular-SPA, Menschen | `POST /auth/login` (E-Mail + Passwort) → Access-Token (15 min) im Body + Refresh-Token als Cookie `cg_refresh` (HttpOnly, Secure, SameSite=Strict, Pfad `/api/v1/auth`, 14 Tage); `POST /auth/refresh` tauscht das Cookie gegen ein neues Access-Token und ein neues Cookie (jedes Refresh-Token gilt einmal; ein bereits benutztes beendet alle Sitzungen des Nutzers); `POST /auth/logout` |
| `ApiKey` | KI-Agenten, Skripte, Integrationen | Header `X-Api-Key: cg_<prefix>_<secret>`; der Key trägt eine Teilmenge der Rechte seines Eigentümers und optional ein Ablaufdatum. Verliert der Eigentümer eine Permission, verliert sie auch der Key. Vorgesehen: eigenes Rate-Limit-Tier |

Beide Schemata münden in dieselben **Permission-Claims**. Jeder Endpunkt deklariert seine Anforderung in `x-required-permission`: eine Permission aus dem Katalog, `authenticated` (beliebiges Konto oder beliebiger API-Key) oder `none` (anonym). Rollen sind nur Bündel von Permissions und tauchen in der API-Autorisierung nicht auf. `401` und `403` kommen als Problem Details; `403` nennt die fehlende Permission im `detail` (`Missing permission: apikey:manage.`), `422` trägt die Feldfehler in `errors`.

Vorhandene Endpunkte (in Klammern die `operationId`):

| Methode & Pfad | Zweck | Permission |
|---|---|---|
| `GET /setup` (`getSetupStatus`) | `required: true`, solange kein Nutzerkonto existiert; die Web-App zeigt dann den Setup-Assistenten `/setup` | `none` |
| `POST /setup/admin` (`createInitialAdmin`) | Ersteinrichtung: Body `setupToken` (einmaliger Token aus dem API-Log, Zeile „Setup required: …“, nach einem Neustart ein neuer), `email`, `password` (mindestens 10 Zeichen). Legt das erste Konto mit der Rolle `Admin` und bestätigter Adresse an und antwortet wie `login` (`200` mit Access-Token + Refresh-Cookie). Gelingt genau einmal, danach `409`; `422` mit Feldfehlern `setupToken`, `email`, `password`; Rate-Limit wie `/auth/*` | `none` |
| `POST /auth/register` (`register`) | `409`, solange die Instanz keinen Nutzer hat (erst `createInitialAdmin`); sonst legt es ein Konto mit der Rolle `User` an und stellt die Bestätigungsmail in die Outbox (nur mit `auth.require_confirmed_email`, sonst gilt die Adresse sofort als bestätigt); mit `auth.require_admin_approval` wartet das Konto auf die Freischaltung, und die Admins (aktive, freigeschaltete Konten mit `user:manage`) bekommen je eine Mail. Body `email`, `password` (mindestens 10 Zeichen), optional `language` (`de`/`en`). Antwort immer `202`, egal ob die Adresse schon existiert | `none` |
| `POST /auth/confirm-email` (`confirmEmail`) | bestätigt die Adresse mit `userId` und `token` aus dem Mail-Link (24 h gültig, einmalig). `204`, bei ungültigem Token `422` | `none` |
| `POST /auth/login` (`login`) | `200` mit `accessToken`, `tokenType`, `expiresIn` + Refresh-Cookie. Abweisungen als Problem Details mit `code`: `401` `invalid_credentials` (falsche Zugangsdaten), `403` `account_disabled` (gesperrt), `account_not_confirmed` (Adresse unbestätigt, nur mit `auth.require_confirmed_email`), `account_pending_approval` (wartet auf Freischaltung) – in dieser Reihenfolge geprüft | `none` |
| `POST /auth/confirm-email/resend` (`resendConfirmation`) | Body `email`, optional `language`; stellt eine neue Bestätigungsmail in die Outbox, falls die Adresse zu einem unbestätigten Konto gehört. Antwort immer `202`; Rate-Limit wie `/auth/*` | `none` |
| `POST /auth/refresh` (`refreshToken`) | neues Access-Token gegen das Refresh-Cookie, ohne Body; `401` ohne gültiges Cookie | `none` |
| `POST /auth/logout` (`logout`) | widerruft das Refresh-Token und löscht das Cookie; immer `204` | `none` |
| `POST /auth/password/forgot` (`requestPasswordReset`) | stellt die Reset-Mail in die Outbox, falls die Adresse zu einem Konto gehört; Antwort immer `202` | `none` |
| `POST /auth/password/change` (`changePassword`) | Body `currentPassword`, `newPassword` (mindestens 10 Zeichen, anders als das aktuelle). Ändert das Passwort des angemeldeten Kontos und beendet alle anderen Browser-Sitzungen (Refresh-Tokens außer dem des mitgesendeten Cookies widerrufen, Security-Stamp neu); ausgestellte Access-Tokens laufen binnen 15 min ab, API-Keys bleiben gültig. `204`; `422` mit Feldfehler `currentPassword` (falsch oder fehlend) bzw. `newPassword`; Rate-Limit wie `/auth/*` | `authenticated` |
| `POST /auth/password/reset` (`resetPassword`) | setzt mit `userId`, `token`, `newPassword` ein neues Passwort und beendet alle Browser-Sitzungen des Nutzers; `204` bzw. `422` | `none` |
| `GET /me` (`getMe`) | `id`, `email`, `roles`, effektive `permissions`, `authenticatedVia` (`bearer`/`apiKey`); bei einem API-Key beschreiben `id`/`email`/`roles` den Eigentümer und `permissions` den Key | `authenticated` |
| `GET /me/preferences` (`getPreferences`) | Einstellungen des Kontos: `data` mit `home` (`latitude`, `longitude`, `zoom`; `null` = Vorgabe), `filters` (Felder wie die Query-Parameter von `listLocations`; `null` = keine) und `mapOptions` (`clusterRadius` in Pixeln, 0 = nie zusammenfassen; `clusterMaxZoom`; `null` = Vorgabe) und `cards` (Schlüssel der EMPs aus `listEmps`, deren Ladekarten oder Apps der Nutzer hat; `null` = keine); `meta.updatedAt` ist `null`, solange das Konto keine gespeichert hat | `authenticated` |
| `PUT /me/preferences` (`putPreferences`) | ersetzt das ganze Dokument, idempotent; `home` mit allen drei Werten (Breite −90…90, Länge −180…180, Zoom 0…24), `filters.connector` OCPI-Standards (gespeichert in Großbuchstaben), `minPowerKw`/`maxPowerKw` > 0, ≤ 1000, Minimum nicht über Maximum, höchstens 100 `operatorId`, `mapOptions` mit beiden Werten (`clusterRadius` 0…120, `clusterMaxZoom` 5…18), höchstens 20 `cards`, jede der Schlüssel eines vorhandenen EMP (Duplikate entfallen). `200` mit dem gespeicherten Dokument, `422` mit Feldfehlern wie `home.zoom`, `filters.connector` oder `cards`; Audit-Eintrag nur bei einer Änderung | `authenticated` |
| `GET /me/favorites` (`listFavorites`) | aktive Favoriten-Standorte des Aufrufers, neueste zuerst: `data[]` mit `location` (wie in `listLocations`) und `addedAt`; `meta` mit `returned`, `max` (200) und `attribution`. Für eine Liste gedacht, die sich jede Minute aktualisiert | `authenticated` |
| `PUT /me/favorites/{locationId}` (`addFavorite`) | merkt einen Standort; idempotent `204`. `404` bei unbekanntem oder nicht aktivem Standort, `409` bei 200 Favoriten | `authenticated` |
| `DELETE /me/favorites/{locationId}` (`removeFavorite`) | entfernt den Favoriten; idempotent `204` | `authenticated` |
| `GET /permissions` (`listPermissions`) | Katalog aller Permissions mit Beschreibung | `none` |
| `GET /api-keys` (`listApiKeys`) | eigene Keys inklusive widerrufener, ohne Secret | `apikey:manage` |
| `POST /api-keys` (`createApiKey`) | Body `name`, `permissions[]` (nicht leer, ⊆ Rechte des Aufrufers), optional `expiresAt`. `201` mit dem Klartext-Key in `key` – nur in dieser Antwort. Mit Header `Idempotency-Key` legt eine Wiederholung nichts an und antwortet `200` mit dem bestehenden Key und `key: null`, weil das Secret nicht wiederherstellbar ist | `apikey:manage` |
| `DELETE /api-keys/{id}` (`revokeApiKey`) | widerruft einen eigenen Key sofort; wiederholbar (`204`), `404` bei fremder oder unbekannter Id | `apikey:manage` |
| `GET /settings` (`listSettings`) | zentraler Config-Service: vollständiger Katalog mit `key`, `type` (`string`/`int`/`number`/`bool`/`enum`/`secret`), `description`, `default`, `allowedValues`, `min`, `max`, `scopeKinds`, den konkret editierbaren `scopes` (z. B. `global` oder `provider:econtrol`), dem wirksamen globalen `value` und den gespeicherten Werten je Scope in `stored`. Secrets erscheinen als `********`, wenn gesetzt, sonst `null` | `settings:read` |
| `GET /settings/{key}` (`getSetting`) | eine Einstellung in derselben Form; `404` bei unbekanntem Key | `settings:read` |
| `PUT /settings/{key}` (`updateSetting`) | Body `value` und optional `scope` (Default `global`). Speichert den Wert; `value: null` (oder weggelassen) entfernt ihn, sodass der Default wieder gilt. Idempotent, wirkt ohne Neustart in allen Containern. `422` mit Feldfehlern (`value`, `scope`) bei falschem Typ, Wert außerhalb der Grenzen oder nicht unterstütztem Scope; `404` bei unbekanntem Key | `settings:manage` |
| `POST /mail/test` (`sendTestMail`) | Body `to`. Sendet eine Testmail synchron mit den aktuellen `mail.*`-Einstellungen, auch bei `mail.enabled = false`. `200` mit Erfolgsmeldung; `422` mit Feldfehlern bei ungültiger Adresse (`to`) oder fehlenden Einstellungen (`mail.smtp.host`, `mail.from.address`); `502` als Problem Details mit der SMTP-Meldung im `detail` | `settings:manage` |
| `GET /mail/outbox` (`listMailOutbox`) | Postausgang, neueste zuerst: `data[]` mit `id`, `to`, `subject`, `templateKey`, `status`, `attempts`, `lastError`, `createdAt`, `sentAt`, `nextAttemptAt` und `meta` mit `total`, `page`, `pageSize`. Filter `status` (`QUEUED`, `SENT`, `FAILED`), Pagination `page` (ab 1) und `pageSize` (1–200, Default 50). Mail-Körper werden nie ausgegeben. `422` bei unbekanntem Status oder ungültiger Seite | `settings:manage` |
| `GET /locations` (`listLocations`) | aktive Standorte in `bbox=minLon,minLat,maxLon,maxLat` (WGS 84), nächstgelegene zur Box-Mitte zuerst, höchstens `limit` (1–2000, Default 500). Je Standort `id`, `name`, Adresse, `latitude`/`longitude`, `operator`, `evsesTotal`, `evsesAvailable` (Status `AVAILABLE`, nicht älter als `map.status_max_age_minutes`, Default 60), `maxPowerKw`, `statusObservedAt` (neuester Statuswert), `availability` (`AVAILABLE`, `OCCUPIED`, `OUT_OF_ORDER`, `UNKNOWN`) und `facilities` (`greenEnergy`, `freeParking`, `roofedParking`, `barrierFreeParkingSpaces`, `priceUrl`; je Wert `null`, wenn keine Quelle ihn kennt); `meta` mit `returned`, `limit`, `truncated` und `attribution` (je beteiligter Quelle `source`, `text`, `license` aus der Tabelle `source`). Optionale Filter (UND-verknüpft; die Zähler eines passenden Standorts umfassen weiter alle EVSEs): `connector` (OCPI-Steckerstandard, mehrfach oder kommagetrennt, einer genügt, Groß-/Kleinschreibung egal), `minPowerKw` und `maxPowerKw` (Leistungsbereich eines EVSE, je > 0, ≤ 1000, Minimum nicht über Maximum), `operator` (Teil des Betreibernamens, ≤ 100 Zeichen), `operatorId` (IDs aus `listOperators`, mehrfach oder kommagetrennt, eine genügt), `availableOnly`, `adHoc` (Ad-hoc-Zahlung: `QR_CODE`, `WEBSITE`, `CREDIT_CARD_PAYABLE`, `DEBIT_CARD_PAYABLE`, `CONTACTLESS_CARD_SUPPORT`, `CHIP_CARD_SUPPORT`, `PED_TERMINAL` oder `CASH` in `authMethods`), `roaming`, `greenEnergy`, `barrierFree` (je `true`/`false`, `false` = kein Filter). `connector`, `minPowerKw`, `maxPowerKw`, `adHoc` und `roaming` müssen für dasselbe EVSE gelten (`connector=IEC_62196_T2&minPowerKw=150` findet keinen Standort, der nur 22 kW Typ 2 neben 150 kW CCS hat). Je Standort zusätzlich `maxAvailablePowerKw` (höchste Leistung der gerade freien EVSEs, `null` ohne freies). `422` mit Feldfehlern `bbox`, `limit`, `connector`, `minPowerKw`, `maxPowerKw`, `operator`, `operatorId` bzw. dem Namen des Flags | `none` |
| `GET /operators` (`listOperators`) | Betreiber mit mindestens einem aktiven Standort, meiste Standorte zuerst (dann Name): `id`, `name`, `operatorId`, `locations`; optional `q` (≤ 100 Zeichen, Teil von Name oder Operator-ID), `id` (UUID, mehrfach; nur diese Betreiber) und `limit` (1–100, Default 20); `meta` mit `returned`, `limit`. `422` bei ungültiger `id`, zu langem `q` oder `limit` außerhalb des Bereichs | `none` |
| `GET /locations/{id}` (`getLocation`) | ein aktiver Standort (`data.location` wie oben) mit allen aktiven EVSEs (`data.evses`: `evseId`, `hasEvseId` – `false` bei einer Surrogat-ID von chargeglobe –, `status` mit `statusSince` und `statusObservedAt`, `UNKNOWN` wenn zu alt, `maxPowerKw`, `powerType`, `connectors` mit `standard`, `format`, `powerType`, `maxPowerKw`, `authMethods` – Namen aus `AuthMethod` ([03](03-datenmodell.md)), leer wenn unbekannt –, `roaming` – Ladekarten anderer Anbieter akzeptiert, `null` wenn unbekannt) und `meta.attribution`; `404` bei unbekannter Id. Der Aufruf bittet den Crawler um einen frischen Status des Standorts (sein Abfragepunkt wird mit dem nächsten freien Request gelesen, höchstens alle 2 min) | `none` |
| `GET /evses/{evseId}` (`getEvse`) | ein EVSE; `evseId` ist die EVSE-ID in beliebiger Schreibweise (`AT*VIE*E1234*1`, `AT-VIE-E1234-1`, `ATVIEE12341`, Groß-/Kleinschreibung egal, auch Surrogat-IDs `XX*CG*E…`) oder die interne UUID. `data`: `id`, `evseId`, `hasEvseId`, `lifecycle` (auch nicht mehr aktive EVSEs), `location` (`id`, Name, Adresse, `latitude`/`longitude`, `region` als ISO-3166-2-Code), `operator` (`id`, `name`, `operatorId`; ohne eigenen der des Standorts), `physicalReference`, `maxPowerKw`, `powerType`, `connectors`, `authMethods`, `roaming`, `status` (`status`, `since`, `observedAt`, `source`, `stale` – älter als `map.status_max_age_minutes` oder fehlend; der Status wird hier nicht durch `UNKNOWN` ersetzt), `firstSeen`, `lastSeen`, `lastConfirmedAt`; am Standort zusätzlich `access` (`PUBLIC`/`RESTRICTED`), `openingTimes` (`twentyFourSeven`, `description` mit dem Text der Quelle; `null` wenn unbekannt) und `facilities` wie in `listLocations`. Zusatzdaten: `sources` (Quell-Datensätze zu EVSE und Standort: `source`, `entity` `EVSE`/`LOCATION`, `externalId`, `firstSeen`, `lastSeen`) und `provenance` (je Attribut `entity`, `attribute` als Spaltenname, z. B. `max_power_kw`, `source`, `observedAt` der letzten Bestätigung, aus `attribute_origin`); `meta.attribution` aus den Quell-Datensätzen des EVSE. `404` bei unbekannter ID | `evse:read` |
| `GET /evses/{evseId}/status/history` (`getEvseStatusHistory`) | Statusintervalle, die [`from`, `to`) überlappen (`from` inklusive, `to` exklusiv, ISO 8601, ohne Offset UTC; Default `to` = jetzt, `from` = `to` − 7 Tage), älteste zuerst, nicht beschnitten (`to: null` = andauernd): `data` mit `id`, `evseId`, `from`, `to`, `intervals[]` (`status`, `from`, `to`, `source`, `confidence`); optional `source` (Quellen-Key). `422` mit Feldfehler `from` bzw. `to` bei unlesbaren Zeiten, `from` nicht vor `to` oder mehr als 90 Tagen; `404` bei unbekannter ID | `evse:read` |
| `GET /evses/{evseId}/prices` (`getEvsePrices`) | Ad-hoc-Preis (ohne Vertrag, AFIR) des EVSE und Vergleich mit Ladekarten: `data` mit `id`, `evseId`, `tariffKind` (`AD_HOC`, gilt für `current` und `history`), `from`, `to`, `example` (`kwh` 30, `minutes` 40), `current` (`price`, `since`, `observedAt`; `null` ohne bekannten Preis), `history[]` (Perioden mit gleichem Preis, die [`from`, `to`) überlappen, älteste zuerst, nicht beschnitten, `to: null` = gilt noch; Default `to` = jetzt, `from` = `to` − 90 Tage, höchstens 366 Tage) und `emps[]` (je jetzt gültigem Tarif, der am EVSE greift – siehe `listTariffs` –: `emp`, `empName`, `tariffId`, `tariff`, `monthlyFee` (nicht Teil der Beispielrechnung) und `price` mit Quelle `emp:<key>` und dem DC-Preis, wenn das EVSE oder ein Stecker DC ist, sonst dem AC-Preis; aufsteigend nach `price.exampleCost`, leer ohne passenden Tarif). Je Preis `source`, `currency`, `pricePerKwh`, `pricePerMinute`, `sessionFee`, `blockingFeePerMinute`, `blockingAfterMinutes`, `freeOfCharge` (Beträge `null`, wenn die Quelle sie nicht nennt) und `exampleCost` (30 kWh in 40 min: Startgebühr + Energie + Zeit + Blockiergebühr für die Minuten nach `blockingAfterMinutes`; Unbekanntes zählt 0); `meta.attribution`. `422` bei ungültigem oder zu langem Zeitraum, `404` bei unbekanntem EVSE | `price:read` |
| `GET /emps` (`listEmps`) | Ladekarten- und App-Anbieter nach Name: `key`, `name`, `website`, `tariffs` (Anzahl Tarife) | `price:read` |
| `PUT /emps/{key}` (`putEmp`) | Body `name` (Pflicht, höchstens 100 Zeichen), `website` (optional, http/https); legt an (`201`, `Location`) oder ersetzt (`200`), idempotent. `key` 2–40 Zeichen aus Kleinbuchstaben, Ziffern und `-`; `422` mit Feldfehlern | `tariff:write` |
| `DELETE /emps/{key}` (`deleteEmp`) | löscht einen Anbieter, `204`; `409` solange er Tarife hat, `404` bei unbekanntem Schlüssel | `tariff:write` |
| `GET /tariffs` (`listTariffs`) | Tarife nach Anbieter und Name, optional `emp=<key>`: `id`, `emp`, `name`, `currency`, `validFrom`, `validTo` (exklusiv, `null` = offen), `pricePerKwhAc`, `pricePerKwhDc`, `pricePerMinute`, `sessionFee`, `blockingFeePerMinute`, `blockingAfterMinutes`, `monthlyFee`, `roamingOnly`, `operatorIds`. Geltung: mit `operatorIds` nur an EVSEs dieser Betreiber, sonst mit `roamingOnly` an EVSEs mit `roaming = true`, sonst überall | `price:read` |
| `PUT /tariffs/{id}` (`putTariff`) | legt den Tarif mit der vom Client gewählten UUID an (`201`, `Location`) oder ersetzt ihn (`200`), idempotent. Felder wie `listTariffs`; Regeln: `emp` bekannt, `name` höchstens 100 Zeichen, `currency` drei Buchstaben (gespeichert in Großbuchstaben), `validFrom` Pflicht, `validTo` danach, Beträge 0–1000, `blockingFeePerMinute` und `blockingAfterMinutes` (0–1440) nur gemeinsam, höchstens 100 bekannte `operatorIds`, `roamingOnly` Default `false`; `422` mit Feldfehlern | `tariff:write` |
| `DELETE /tariffs/{id}` (`deleteTariff`) | löscht den Tarif, `204` (auch bei unbekannter Id) | `tariff:write` |
| `GET /evses/{evseId}/occupancy` (`getEvseOccupancy`) | Belegungsprofil der letzten `weeks` Wochen (1–12, Default 4) bis jetzt aus `evse_occupancy_hourly`: `data.days[]` mit sieben Zeilen ab Montag (`weekday` 1–7, `day` z. B. `MONDAY`), je 24 Werte `chargingShare` (Anteil der Statusmeldungen mit `CHARGING`, 0–1, `null` ohne Meldung) und `observations`; dazu `utilization` (Anteil über den ganzen Zeitraum), `observations`, `from`, `to`, `weeks`, `timeZone` (IANA-Name für Wochentag und Stunde, Parameter `timeZone`, Default `Europe/Vienna`). Die Anteile sind Stichproben im Abfrageintervall der Quellen, keine gemessenen Dauern. `422` bei `weeks` außerhalb 1–12 oder unbekannter Zeitzone, `404` bei unbekannter ID | `evse:read` |
| `GET /map/config` (`getMapConfig`) | `styleUrl` (Kartenstil im hellen Modus), `styleUrlDark` (dunkler Modus aus `map.style_url_dark`, bei leerem Wert gleich `styleUrl`), `attribution`, `center` (`latitude`, `longitude`) und `zoom` der Basiskarte aus den Einstellungen `map.*`; die Kartenseite der Web-App lädt sie bei jedem Aufruf, Änderungen wirken also nach einem Neuladen der Seite. Kacheln, Glyphen und Sprites holt der Browser direkt von den Hosts der Style-URL (Default: `tiles.openfreemap.org`), nicht über die API | `none` |
| `GET /search` (`search`) | Suche nach `q` (2–100 Zeichen): zuerst aktive EVSEs, deren EVSE-ID in beliebiger Schreibweise mit `q` beginnt, dann aktive Standorte, deren Name, Straße, PLZ und Ort jedes Wort von `q` enthalten (ähnlichste zuerst); höchstens `limit` (1–50, Default 10). Je Treffer `type` (`EVSE`/`LOCATION`), `label`, `detail` (Adresse), `locationId`, `latitude`, `longitude`, `evseId`. `422` bei fehlendem oder zu kurzem `q` bzw. `limit` außerhalb des Bereichs | `none` |
| `GET /users` (`listUsers`) | Konten, neueste zuerst: `data[]` mit `id`, `email`, `state` (`ACTIVE`, `PENDING_CONFIRMATION`, `PENDING_APPROVAL`, `DISABLED`), `emailConfirmed`, `createdAt`, `approvedAt`, `disabledAt`, `roles`; `meta` mit `total`, `page`, `pageSize`. Filter `q` (Teil der E-Mail-Adresse), `page` (ab 1), `pageSize` (1–100, Default 25); `422` bei ungültiger Seite | `user:read` |
| `GET /users/{id}` (`getUser`) | ein Konto wie oben; `404` bei unbekannter Id | `user:read` |
| `PUT /users/{id}/roles` (`setUserRoles`) | Body `roles` (Namen aus `listRoles`, darf leer sein); ersetzt die Rollen. `422` bei unbekannten Rollen, `409` wenn danach kein aktives, freigeschaltetes Konto mit `user:manage` übrig wäre; neue Rechte gelten ab dem nächsten Access-Token (höchstens 15 min) | `user:manage` |
| `PUT /users/{id}/approval` (`setUserApproval`) | Body `approved`; die erste Freischaltung schickt dem Konto eine Mail. `409` für das eigene Konto oder wenn kein Verwalter übrig bliebe | `user:manage` |
| `PUT /users/{id}/enabled` (`setUserEnabled`) | Body `enabled`; `false` sperrt das Konto sofort für Login, Refresh und seine API-Keys (ausgestellte Access-Tokens laufen binnen 15 min ab). `409` für das eigene Konto oder wenn kein Verwalter übrig bliebe | `user:manage` |
| `POST /users/{id}/confirmation-mail` (`sendUserConfirmationMail`) | neue Bestätigungsmail für ein unbestätigtes Konto, `202`; `409` bei bereits bestätigter Adresse | `user:manage` |
| `GET /roles` (`listRoles`) | Rollen mit `name`, `permissions`, `users` (Anzahl Konten), `fullCatalog` (Admin: immer alle Permissions), `default` (Standardrolle, wird beim Start angelegt) | `user:read` |
| `PUT /roles/{name}` (`putRole`) | Body `permissions`; legt die Rolle an (`201`) oder ersetzt ihre Permissions (`200`). Name 1–50 Zeichen aus Buchstaben, Ziffern, Leerzeichen, `-`, `_`; `422` bei ungültigem Namen oder unbekannter Permission, `409` für `Admin` oder wenn kein Verwalter übrig bliebe | `role:manage` |
| `DELETE /roles/{name}` (`deleteRole`) | löscht eine Rolle ohne Konten, `204`; `409` für Standardrollen oder Rollen mit Konten, `404` bei unbekannter Rolle | `role:manage` |
| `GET /admin/crawler/status` (`getCrawlerStatus`) | Live-Zustand der Crawler je Quelle: `enabled`, `budgetPerHour` (`provider.request_budget_per_hour`), `requestsLastHour`, `pause` (`failures`, `until`, `blocked`, `reason`; `null` solange die Quelle laufen darf), `tiles` (Abfragepunkte: `total`, `due`, `hot`, `normal`, `quiet`, `onDemand`, `oldestPoll`) und `plans` (`jobType`, `target`, `nextRunAt`, `leasedBy`, `lastRunAt`, `lastResult`); `queue` mit offenen und fehlerhaften Ingest-Nachrichten. Für Abfragen alle paar Sekunden gedacht | `jobs:read` |
| `GET /conflicts` (`listConflicts`) | Konflikte zwischen Quellen, neueste zuerst: `entityType` (LOCATION/EVSE), `entityId`, `entityLabel` (Name und Adresse bzw. EVSE-ID; `null`, wenn es die Entität nicht mehr gibt), `attribute` (`identity`: ein Datensatz passt zu mehreren Standorten, `values` mit `source`, `externalId`, `candidates`; `evse_id`: zwei Schreibweisen einer EVSE-ID), `values`, `detectedAt`, `candidates` (bei `identity` die Kandidaten mit `locationId`, `label` und `distanceMeters` zum Datensatz, nächster zuerst; sonst leer); standardmäßig offene, mit `resolved=true` gelöste (`resolvedAt`, `resolution`); höchstens `limit` (1–500, Default 50); `meta` mit `returned`, `limit`, `open` (alle offenen). `422` bei `limit` außerhalb des Bereichs | `conflict:resolve` |
| `POST /conflicts/{id}/resolve` (`resolveConflict`) | entscheidet einen offenen Konflikt, Body `action`: `merge` mit `targetId` (einer der `candidates`, aktiver Standort) bei `identity` – der Datensatz der Quelle gehört künftig zu diesem Standort (`MANUAL`), seine EVSEs folgen beim nächsten Lauf; `separate` bei `identity` – der nächste Lauf legt einen eigenen Standort an; `keep` bei `evse_id` – die gespeicherte EVSE-ID bleibt. `note` (höchstens 500 Zeichen) wird an `resolution` angehängt. Idempotent: ein schon gelöster Konflikt antwortet `200` mit seinem Stand. Audit-Eintrag `conflict.resolve`. `404` bei unbekanntem Konflikt, `422` bei unbekannter oder unpassender Aktion oder `targetId` außerhalb der Kandidaten | `conflict:resolve` |
| `GET /admin/crawler/runs` (`listCrawlerRuns`) | letzte Crawler-Läufe, neueste zuerst, höchstens `limit` (1–500, Default 50), optional einer Quelle (`provider`): `jobType`, `target` (Region(en) oder „point n/m (lat,lon, k locations)“), `startedAt`, `finishedAt`, `requests`, `records`, `result` (`RUNNING`/`OK`/`FAILED`), `error`, `cooldownUntil`. `422` bei `limit` außerhalb des Bereichs | `jobs:read` |

Schreibende Aktionen (Ersteinrichtung, Registrierung, Bestätigung, Login, Passwort-Reset anfordern/ausführen, Key anlegen/widerrufen, Einstellung ändern, Testmail senden, Passwort ändern, Einstellungen speichern, Rollen zuweisen, Konto freischalten/sperren, Rolle anlegen/ändern/löschen, Favorit merken/entfernen, Anbieter und Tarif anlegen/ändern/löschen als `emp.*` und `tariff.*`; ein PUT ohne Änderung schreibt keinen Eintrag) landen mit Akteur (Nutzer bzw. API-Key) im `audit_log`; bei Einstellungen ohne den Wert.

Vorgesehene Endpunkte:

| Methode & Pfad | Zweck | Permission |
|---|---|---|
| `PUT /users/{id}/permissions`, `DELETE /users/{id}` | direkte Zusatzrechte, Konto löschen; fremde API-Keys verwalten | `user:manage` |
| `GET /audit-log` | schreibende Aktionen, filterbar nach Akteur/Entität/Zeit | `audit:read` |
| `POST /evses/{id}/merge` | zwei EVSEs von Hand zusammenführen | `conflict:resolve` |
| `POST /sources/{key}/run` | Job anstoßen | `provider:manage` |
| `GET /jobs/instances` | laufende Crawler-/Worker-Instanzen und ihre Leases | `jobs:read` |
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
| `GET /evses/{evseId}/prices?scenario=…` | Vergleich wie heute, mit wählbarem Szenario statt 30 kWh / 40 min | `scenario=DC_50KW_30KWH_40MIN` |
| `GET /evses/{evseId}/sources` | Provenienz: Quell-Datensätze, Match-Methode, Attribut-Herkunft | |

### Standorte (anonym, außer Historie: `evse:read`)

| Methode & Pfad | Zweck |
|---|---|
| `GET /locations`, `GET /locations/{id}` | vorhanden, siehe Tabelle der vorhandenen Endpunkte oben; vorgesehen sind zusätzlich Filter (Stecker, Leistung, Betreiber, nur frei, Ad-hoc-Zahlung, Roaming, Ökostrom, barrierefrei) |
| `GET /locations/{id}/status/history` | aggregierte Verfügbarkeit (frei/gesamt) über Zeit |

### Stammdaten & Meta (`evse:read`)

| Methode & Pfad | Zweck |
|---|---|
| `GET /regions` | Liste der Regionen |
| `GET /sources` | Quellen mit Lizenz, Attribution, Capabilities, Health (`lastSuccess`, `freshnessP50`) |
| `GET /stats` | Zähler (EVSEs je Region/Betreiber/Leistungsklasse, Statusfrische) |
| `GET /changes?since=` | Feed von Änderungen (neue/entfernte EVSEs, Preisänderungen) für Downstream-Consumer |

### Karte (anonym)
- `GET /map/config` (vorhanden) – Style-URLs der Basiskarte für hellen und dunklen Modus, Attributionstext, Start-Ausschnitt (aus dem Config-Service, `map.*`).
- `GET /tiles/{z}/{x}/{y}.mvt` (ab Phase 2) – Vector-Tiles mit Standort-Markern inkl. Status-Zusammenfassung; alternativ via pg_tileserv/Martin direkt aus Postgres.

### Routing (`route:read`)
`POST /routes` (`calculateRoute`, vorhanden) – schnellste Route mit dem Auto auf OSM-Straßendaten durch `waypoints` in ihrer Reihenfolge (2–10 Positionen mit `latitude` −90…90 und `longitude` −180…180: Start, optionale Zwischenziele, Ziel). Antwort: `data` mit `distanceMeters`, `durationSeconds` (ohne Verkehr, gerundet) und `geometry` (GeoJSON LineString, Positionen als [Länge, Breite]); `meta.attribution` mit OpenStreetMap (ODbL). `422` mit `errors.waypoints` bei ungültigen Wegpunkten und wenn keine Straßenverbindung existiert; `503`, solange die Routing-Engine nicht läuft (optionaler Dienst der Installation). Gebiet: der installierte Extrakt (Österreich). Der Aufruf verändert nichts und ist beliebig wiederholbar; POST statt GET, damit Koordinaten nicht in URLs und Logs stehen. Positionen werden weder gespeichert noch geloggt. Vorgesehen: Profile (`car`, `bike`), Meiden von Maut/Autobahn, Abbiegehinweise.

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
    "authMethods": [ "RFID_READER", "APP", "CREDIT_CARD_PAYABLE" ],
    "roaming": true,
    "status": { "status": "CHARGING", "since": "2026-10-14T07:40:00+00:00", "observedAt": "2026-10-14T08:15:00+00:00", "source": "econtrol", "stale": false },
    "firstSeen": "2026-03-01T06:00:00+00:00",
    "lastSeen": "2026-10-14T08:15:00+00:00",
    "lastConfirmedAt": "2026-10-14T06:00:00+00:00"
  },
  "meta": { "attribution": [ { "source": "econtrol", "text": "Datenquelle: Ladestellenverzeichnis der E-Control (ladestellen.at)", "license": "…" } ] }
}
```

Preise stehen nicht in `getEvse`, sondern in `getEvsePrices` (Ad-hoc-Preis und Ladekarten, `price:read`).

## Querschnitt

- **Caching**: Listen/Status 30 s Output-Cache, Stammdaten 10 min, Historie 5 min; `ETag` überall.
- **Rate-Limits**: `/auth/*` je IP nach `auth.rate_limit_per_minute` (Default 20 req/min), `429` als Problem Details mit `Retry-After`. Vorgesehen für die übrigen Endpunkte: anonym 60 req/min & 5.000/Tag je IP; authentifiziert nach `rate_limit_tier` (Standard 600 req/min).
- **Attribution**: jede Antwort enthält `meta.attribution[]` mit Lizenztexten der beteiligten Quellen (Pflicht z. B. bei ODbL/CC BY).
- **Versionierung**: Breaking Changes nur über `/api/v2`; additive Felder jederzeit.
- **Bulk-Export**: täglicher Dump (`/exports/evses.csv.gz`, `/exports/evses.geojson.gz`, Tarife als JSON) für Forschung/Analysen – mit Lizenzhinweis.
- **Realtime (später)**: Server-Sent Events `GET /stream/status?bbox=` für Statuswechsel.
