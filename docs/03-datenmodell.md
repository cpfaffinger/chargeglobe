# 03 – Datenmodell

## Prinzipien

1. **Kanonisches Modell ≠ Quellmodell.** Quellen liefern Rohdaten; der ACL mappt auf das kanonische Modell. Rohdaten werden als Snapshot aufbewahrt.
2. **Provenienz ist Pflicht.** Jede Entität kennt ihre Quell-Datensätze; jedes Attribut kennt die Quelle, aus der sein aktueller Wert stammt.
3. **Zeit ist überall.** Status und Preise sind Zeitreihen; Stammdaten haben `first_seen`/`last_seen`/`last_confirmed_at`.
4. **OCPI 2.2.1 als Vokabular**: Status-Enums, Steckertypen, Tarifelemente werden 1:1 übernommen, damit Quellen mit OCPI-Export verlustfrei abgebildet werden.

## Entitäten

```
Region ─┐
        │ 1..n
Operator (CPO) ─┬─ 1..n ─ Location ─ 1..n ─ ChargingStation? ─ 1..n ─ Evse ─ 1..n ─ Connector
                │                                                   │
                │                                                   ├─ EvseStatusObservation (Zeitreihe)
                │                                                   ├─ EvseStatusInterval    (abgeleitet)
                │                                                   └─ (Geltung über Betreiber/Roaming) ─ Tariff ─ Emp
                │                                                           │
                └─ Source ─ SourceRecord (Quell-ID ↔ kanonische ID) ─ RawSnapshot   PriceObservation (Zeitreihe)
                                  └─ AttributeOrigin
```

### Stammdaten

| Tabelle | Wichtige Spalten | Anmerkung |
|---|---|---|
| `region` | `code` (PK, `AT-9` Wien, `AT-3` NÖ, …), `name`, `geom geography(MultiPolygon,4326)` (GiST) | ISO 3166-2 Codes; die neun Bundesländer kommen aus OpenStreetMap (Grenzrelationen `admin_level=4`, ODbL, „© OpenStreetMap contributors“) als vereinfachtes GeoJSON im Repo (`src/ChargeGlobe.Infrastructure/Persistence/Seed/regions-at.geojson`, neu erzeugbar mit `scripts/regions/build-regions-at.sh`); die API schreibt fehlende oder geänderte Regionen beim Start (`RegionSeeder`) und berechnet dann `location.region_code` aller Standorte neu |
| `operator` | `id`, `name`, `country`, `operator_id` (z. B. `EVN`, eindeutig je `country`), `website`, `is_cpo`, `is_emp` | EVSE-ID-Präfix `AT*EVN` → Operator |
| `location` | `id uuid`, `name`, `address`, `postal_code`, `city`, `country`, `geom geography(Point,4326)` (GiST; trägt die Box-Abfrage der Karte und den Korridor entlang einer Route), `operator_id`, `region_code`, `opening_times jsonb` (OCPI `Hours`), `access` (`PUBLIC`/`RESTRICTED`), `green_energy` (100 % erneuerbar laut Betreiber, OCPI `EnergyMix.is_green_energy`), `free_parking`, `roofed_parking`, `barrier_free_parking_spaces` (Anzahl), `price_url` (Preisseite des Betreibers, OCPI `Tariff.tariff_alt_url`), `lifecycle` (`ACTIVE`/`PLANNED`/`REMOVED`), `first_seen`, `last_seen`, `last_confirmed_at`, `park_id uuid?` und `park_locations` (Ladepark, abgeleitet; Teilindex `ix_location_park_id`) | `region_code` setzt der Trigger `location_assign_region` bei Insert und bei Änderung von `geom` (Region, deren Polygon den Punkt enthält). **Ladepark**: aktive Standorte desselben Betreibers in höchstens `location.park_radius_meters` (Default 25 m) Abstand zueinander, auch über eine Kette, teilen `park_id` (kleinste Standort-Id des Parks) und `park_locations` (Zahl der Standorte darin); allein stehende haben `null` und 1. Berechnet `LocationParks` per `ST_ClusterDBSCAN` in EPSG 3035 (Meter) je Betreiber; die Spalten sind nicht im EF-Modell (Migration `LocationParks`). Ein Park bündelt nur die Anzeige, die Identität der Standorte bleibt (z. B. die E-Control-„Stationen“ `AT*EVN*E2453` und `AT*EVN*E2454` am selben Punkt: zwei Standorte, ein Park) |
| `charging_station` | `id`, `location_id`, `label` (Kennung der Quelle, bei der E-Control der Säulenteil der EVSE-ID, z. B. `AT*MER*E006566`; eindeutig je Standort, Index `IX_charging_station_location_id_label`), `physical_reference` | Ladestation (Säule, Wallbox), soweit eine Quelle sie benennt (`ProviderEvseSnapshot.StationExternalId`); der Ingest legt sie je Standort und Kennung an und setzt `evse.charging_station_id` (Attribut `charging_station` mit Präzedenz). Ladepunkte ohne Kennung bleiben ohne Station |
| `evse` | `id uuid`, `evse_id_norm` (UNIQUE, z. B. `ATEVNE12345`), `evse_id_display` (`AT*EVN*E1234*5`), `location_id`, `charging_station_id?`, `operator_id`, `max_power_kw`, `power_type` (OCPI: `AC_1_PHASE`, `AC_2_PHASE`, `AC_2_PHASE_SPLIT`, `AC_3_PHASE`, `DC`), `physical_reference`, `auth_methods text[]` (Vokabular `AuthMethod`, siehe unten; leer = unbekannt), `roaming` (Ladekarten anderer Anbieter werden akzeptiert; `null` = unbekannt), `first_seen`, `last_seen`, `last_confirmed_at`, `lifecycle` (`ACTIVE`/`PLANNED`/`REMOVED`), `identity_kind` (`EVSE_ID` / `SURROGATE`) | Ladepunkte ohne bekannte EVSE-ID bekommen eine Surrogat-ID `XX*CG*E<16 Hex-Zeichen aus dem Zufallsteil der UUID>` und `identity_kind=SURROGATE`; `XX` ist kein Ländercode, Kollisionen mit echten IDs sind ausgeschlossen |
| `connector` | `id`, `evse_id`, `ordinal` (ab 1, eindeutig je EVSE), `standard` (OCPI `ConnectorType`: `IEC_62196_T2`, `IEC_62196_T2_COMBO`, `CHADEMO`, `DOMESTIC_F`, `TESLA_S`, …, zusätzlich `UNKNOWN`), `format` (`SOCKET`/`CABLE`), `power_type`, `max_voltage`, `max_amperage`, `max_power_kw` | |

**Zahlungs- und Authentifizierungsarten (`AuthMethod`)**, gespeichert in `evse.auth_methods`, sortiert in dieser Reihenfolge: `RFID_READER` (Ladekarte), `APP`, `QR_CODE`, `WEBSITE`, `CHARGING_CONTRACT` (Ladekarte oder Vertrag ohne Angabe des Mediums), `CREDIT_CARD_PAYABLE`, `DEBIT_CARD_PAYABLE`, `CONTACTLESS_CARD_SUPPORT` (NFC-Karte), `CHIP_CARD_SUPPORT`, `PED_TERMINAL` (Terminal mit PIN), `CASH`. Werte mit OCPI-2.2.1-Gegenstück (`Capability`) tragen dessen Namen; App, QR-Code, Website, Vertrag und Bargeld kennt OCPI nicht, AFIR und die Quellen schon. Ad-hoc bezahlbar ohne Vertrag ist ein Ladepunkt mit `QR_CODE`, `WEBSITE`, `CREDIT_CARD_PAYABLE`, `DEBIT_CARD_PAYABLE`, `CONTACTLESS_CARD_SUPPORT`, `CHIP_CARD_SUPPORT`, `PED_TERMINAL` oder `CASH`.

### Provenienz

| Tabelle | Spalten | Zweck |
|---|---|---|
| `source` | `key` (PK: `econtrol`, `ocm`, `bev-address`, `osm`, `evn`, `oeamtc`, …), `display_name`, `tier` (`OFFICIAL`, `OPEN_DATA`, `PUBLIC_WEB`, `APP_BACKEND`, `PARTNER`), `license`, `attribution_text`, `capabilities text[]` (Namen der Provider-Capabilities) | Registry der Quellen; die Felder kommen aus dem Descriptor des Adapters. Ob ein Provider aktiv ist, steht im Config-Service (`enabled`, Scope `provider:<key>`), nicht hier |
| `source_record` | PK `source_key` + `external_id` + `entity_type` (`LOCATION`/`EVSE`/`CONNECTOR`), `entity_id`, `match_method` (`EVSE_ID_EXACT`, `EVSE_ID_NORMALIZED`, `GEO_OPERATOR`, `GEO_ONLY`, `MANUAL`, `CREATED` = Datensatz hat die Entität angelegt), `match_confidence` (0–1), `first_seen`, `last_seen`, `last_snapshot_id` | Quell-ID ↔ kanonische Entität (n:1) |
| `attribute_origin` | PK `entity_type` + `entity_id` + `attribute` (`geom`, `max_power_kw`, `name`, …), `source_key`, `observed_at`, `value_hash` (SHA-256) | welches Attribut stammt woher |
| `raw_snapshot` | `id` (bigint), `source_key`, `job_id`, `fetched_at`, `request_fingerprint` (z. B. `location:<externe Id>`), `content_hash` (SHA-256 des Payloads), `payload jsonb`, `http_status` | Replay; eindeutig je `source_key` + `content_hash` (ein erneut gesehener Datensatz aktualisiert nur `fetched_at`/`job_id`); Retention `ingest.snapshot_retention_days` (30) |
| `match_decision` | PK `source_key` + `external_id`, `decision` (`MERGE`/`SEPARATE` für Standorte, `KEEP` für Ladepunkte), `location_id?` (gewählter Standort, `SET NULL` beim Löschen), `conflict_id`, `decided_at`, `decided_by?` (Nutzer) | Entscheidung eines Maintainers oder Agenten über einen mehrdeutigen Datensatz; der Merge liest `SEPARATE` vor den Geo-Schritten und `KEEP` vor dem EVSE-ID-Konflikt |
| `source_conflict` | `id`, `entity_type`, `entity_id`, `attribute`, `values jsonb` (je Quelle, bei `identity` mit `record` des Datensatzes), `detected_at`, `resolved_at`, `resolution` | Review-Queue im Admin und für Agenten (Index auf offene Konflikte); je `source` + `externalId` in `values` höchstens ein offener Konflikt je Attribut |

`source_key` verweist fachlich auf `source.key`, ist aber bewusst kein Fremdschlüssel: Adapter können liefern, bevor ihre `source`-Zeile existiert, und die Zeitreihen sparen sich die Prüfung je Insert.

### Status (Zeitreihen)

| Tabelle | Spalten | Anmerkung |
|---|---|---|
| `evse_status_observation` (Hypertable, Tages-Chunks) | PK `evse_id` + `time` + `source_key`, `status` (OCPI-Enum), `source_status_raw` (Originalstring), `source_reported_at` (falls Quelle einen Zeitstempel liefert), `latency_ms` (`time` − `source_reported_at`) | jede Beobachtung jeder Quelle; Kompression nach 7 Tagen (segmentiert nach `evse_id`, `source_key`), Retention 180 Tage |
| `evse_status_interval` | PK `evse_id` + `valid_from`, `status`, `valid_to` (NULL = aktuell), `source_key`, `confidence` (0–1) | nur bei Statuswechsel; höchstens ein offenes Intervall je EVSE (Unique-Index auf `evse_id` mit `valid_to IS NULL`); Teilindex `IX_evse_status_interval_fault_valid_from` auf `valid_from` für `OUTOFORDER`/`INOPERATIVE` (Störungsphasen der Datenfrische); Basis für Belegungshistorie und Auslastungsstatistik |
| `evse_status_current` | `evse_id` (PK), `status`, `since`, `observed_at`, `source_key`, `previous_status?`, `previous_since?` (Status vor dem aktuellen und sein Beginn) | denormalisiert für Karten-Queries; ob der Wert veraltet ist, wird beim Lesen aus `observed_at` berechnet (`IsStale`); der angezeigte Status folgt der Störungsglättung (unten) |
| `evse_occupancy_hourly` (Continuous Aggregate über `evse_status_observation`) | `bucket` (1 h, UTC), `evse_id`, `obs_charging` (Beobachtungen mit `CHARGING`), `obs_available` (`AVAILABLE`), `obs_total` (alle) | für Heatmaps/Auslastung ohne Scan der Rohdaten (`GET /evses/{evseId}/occupancy`); Real-Time-Aggregation (`materialized_only = false`): Stunden oberhalb der Materialisierungsgrenze kommen direkt aus den Rohdaten; Refresh-Policy alle 30 min für die letzten 7 Tage bis 1 h vor jetzt; keine Retention (bleibt nach dem Löschen der Rohdaten erhalten) |
| `location_status_observation` (Hypertable, Tages-Chunks) | PK `location_id` + `time` + `source_key`, `evses_total`, `evses_available` | für Quellen, die nur Standort-Summen liefern (z. B. „3 von 8 frei“); Kompression und Retention wie oben |

**Granularität:** Der Status gehört zum EVSE (Ladepunkt), nicht zum Stecker – so wie in OCPI, wo ein EVSE nur ein Fahrzeug zugleich lädt und seine Stecker Alternativen sind. Die E-Control meldet den Status ebenfalls je Ladepunkt (`points[].status`); rund 250 von 40.800 Ladepunkten haben 2–3 Stecker (meist Typ 2 + CCS), für sie gilt der Status gemeinsam. Ein Standort mit mehreren Ladepunkten ist **teilweise belegt**, wenn einige frei und andere belegt oder gestört sind (live rund jeder zehnte Standort); die Karte zeigt ihn als frei und nennt die übrigen (`evsesOccupied`, `evsesFaulted`, docs/06).

**Umgesetzt (eine Quelle):** Jeder Statuswert wird Beobachtung (`ON CONFLICT DO NOTHING`, damit eine erneut verarbeitete Nachricht nichts doppelt schreibt). Ist er neuer als `evse_status_current.observed_at` und ändert er den Status, wird das offene Intervall geschlossen und ein neues geöffnet; sonst wird nur `observed_at` fortgeschrieben. Ältere Werte ändern nichts.

**Störungsglättung (Anzeige):** Manche Quellen melden viele Störungen von wenigen Minuten (EVN: Median um 15 min). Eine Störung (`OUTOFORDER`/`INOPERATIVE`), die nach einem Status ohne Störung begonnen hat und kürzer als `status.fault_grace_minutes` (Default 10, 0 = aus) dauert, zählt auf der Karte, in Infokarte und Standort-Detail, Favoriten, Suche und Statistik noch nicht: angezeigt wird `previous_status` (mit `previous_since`), die Infokarte nennt die Störung als Hinweis („kurz gestört laut E-Control seit …“, API `reportedStatus`/`reportedSince`). Die Regel steht zweimal gleich: `EvseStatusCurrent.InFaultGrace`/`ShownStatus` (Standort-Detail) und die SQL-Funktion `evse_shown_status(status, previous_status, since, grace_since)` (Migration `FaultGrace`, in den Kartenabfragen). Beobachtungen, Intervalle, `evse_status_current.status`, die Ladepunkt-Seite, `GET /evses` und `/admin/crawler` bleiben beim Status der Quelle.

**Status-Zusammenführung bei mehreren Quellen (vorgesehen):** `evse_status_current` folgt der Quelle mit der höchsten Status-Präzedenz, die innerhalb ihres Frische-Fensters liegt (CPO-direkt > Roaming-EMP > Aggregator). Widersprüche innerhalb 60 s werden als `confidence < 1` markiert, nicht als Wechsel.

### Identität & Zugriff

| Tabelle | Spalten | Anmerkung |
|---|---|---|
| `AspNetUsers`, `AspNetRoles`, `AspNetUserRoles` | Standard ASP.NET Core Identity; dazu in `AspNetUsers` `CreatedAt`, `ApprovedAt?` (leer = wartet auf Freischaltung durch einen Admin), `DisabledAt?` (gesetzt = gesperrt) | E-Mail als Login, Passwort-Hash, Lockout |
| `user_preferences` | PK `user_id` (`ON DELETE CASCADE`), `document jsonb` (`home`, `filters`, `mapOptions`, Schema in `docs/06`), `updated_at` | Einstellungen eines Kontos, geräteübergreifend |
| `favorite_location` | PK `user_id` + `location_id` (beide mit `ON DELETE CASCADE`), `created_at` | Favoriten eines Kontos, höchstens 200 |
| `role_permission` | `role_id`, `permission` | Rollen sind Berechtigungsbündel; Permissions kommen aus dem Katalog in Domain |
| `user_permission` | PK `user_id` + `permission` (`ON DELETE CASCADE`) | direkte Zusatzrechte neben den Rollen, gesetzt über `PUT /users/{id}/permissions`; ein gelöschtes Konto nimmt sie mit, ebenso Rollen-Zuordnungen, API-Keys, Refresh-Tokens, Favoriten und Einstellungen (Kaskaden); `audit_log` behält seine Einträge |
| `api_key` | `id`, `name`, `key_hash`, `key_prefix` (erste 8 Zeichen zur Anzeige), `owner_user_id`, `permissions text[]` (Teilmenge der Rechte des Eigentümers), `expires_at`, `last_used_at`, `revoked_at`, `rate_limit_tier` | Klartext wird nur bei Erstellung einmal zurückgegeben |
| `audit_log` | `id bigint` (PK, aufsteigend, Cursor der Liste), `actor_user_id?`, `actor_api_key_id?`, `action` (z. B. `apikey.revoke`), `target` (z. B. `api_key:<id>`), `occurred_at` (Index) | jede schreibende Aktion, gespeichert mit der Änderung selbst; gelesen über `GET /audit-log`; Einträge älter als `audit.retention_days` (Default 365) löscht der Worker täglich |

### Konfiguration & Job-Verteilung

| Tabelle | Spalten | Anmerkung |
|---|---|---|
| `app_setting` | `key`, `scope` (`global`, `provider:<key>`, `region:<code>`), `value jsonb`, `updated_by`, `updated_at`; Primärschlüssel `key` + `scope` | Werte des zentralen Config-Service; Defaults und Typen (auch „Secret“) stehen im Katalog im Code, Secrets sind verschlüsselt (Schlüsselring in `data_protection_key`) |
| `email_outbox` | `id`, `to`, `subject`, `body_text`, `body_html`, `template_key`, `status` (`QUEUED`, `SENT`, `FAILED`), `attempts`, `last_error`, `created_at`, `sent_at`, `next_attempt_at` | Postausgang des Mail-Service; der Worker versendet fällige Einträge (`status = QUEUED` und `next_attempt_at` leer oder erreicht), versendete werden nach `mail.retention_days` gelöscht |
| `poll_plan` | `id`, `provider_key`, `job_type` (`INVENTORY_FULL`, `STATUS_POLL`, `TILE_PLAN`), `region_code` (bei Plänen für alle Regionen die Liste, z. B. `AT-9,AT-3,AT-4,AT-1`), `min_longitude`/`min_latitude`/`max_longitude`/`max_latitude` (Bounding-Box der Region), `interval`, `next_run_at`, `leased_by?`, `lease_until?`, `last_run_at?`, `last_result?`; eindeutig je `provider_key` + `job_type` + `region_code` | Arbeitsvorrat der Crawler; Claim per `FOR UPDATE SKIP LOCKED`, Lease 1 h |
| `provider_rate_budget` | `provider_key` (PK), `tokens`, `refilled_at`, `day_count`, `day_reset_at` (nächste Mitternacht UTC) | clusterweiter Token-Bucket; eine Zeilensperre je Token |
| `poll_tile` | `id`, `provider_key`, `number` (1 … n), `latitude`, `longitude` (eindeutig je Quelle), `location_ids uuid[]` (GIN), `priority` (`HOT`/`NORMAL`/`QUIET`), `interval_minutes`, `changes_per_day`, `last_polled_at?`, `demand_at?`, `leased_by?`, `lease_until?`, `last_result?` | Abfragepunkte der Quellen mit Punktsuche ([05](05-crawling-konzept.md)); stündlich neu berechnet, Lease 5 min |
| `provider_backoff` | `provider_key` (PK), `failures` (in Folge), `paused_until?`, `blocked`, `reason?`, `updated_at` | Pause nach Fehlern der Quelle; eine Sperre endet mit der nächsten Änderung einer Einstellung der Quelle |
| `crawler_run` | `id`, `provider_key`, `job_type`, `target` (Region(en) oder Abfragepunkt), `started_at`, `finished_at?`, `requests`, `records`, `result` (`RUNNING`/`OK`/`FAILED`), `error?`, `cooldown_until?` | Protokoll für Log und `/admin/crawler`; gelöscht nach `crawler.run_retention_days` |
| `ingest_queue` | `id` (bigint), `provider_key`, `job_id`, `payload jsonb` (`IngestBatch`: Standorte mit `raw_snapshot`-Id oder Statuswerte), `enqueued_at`, `attempts`, `next_attempt_at?`, `error?`, `processed_at?` | Übergabe Crawler → Worker; gesperrt wird per Zeilensperre in der Verarbeitungstransaktion statt per Lease-Spalten; Retention `ingest.queue_retention_days` nach Verarbeitung |
| `job_lease` | `job_name` (PK), `leased_by`, `lease_until` | Single-Runner-Jobs (`ingest-retention` hält die Lease einen Tag lang als Takt; `address-import` und `location-maintenance` geben sie nach jedem Lauf frei) |

### Adressen (Adresssuche)

| Tabelle | Spalten | Anmerkung |
|---|---|---|
| `address_street` | `id` (PK `pk_address_street`), `street`, `postal_code`, `city` (Ortschaft; nennt sie nur einen Teil der Gemeinde wie „Wien,Innere Stadt“, die Gemeinde), `municipality_code` (Gemeindekennziffer), `search_text` (Straße, PLZ, Ortschaft und Gemeinde; klein, „ß“ als „ss“, Satzzeichen als Leerzeichen) mit GIN-Trigramm-Index `ix_address_street_search` (Wortfilter) und GiST-Trigramm-Index `ix_address_street_similar` (Reihenfolge nach Ähnlichkeit), `geom geography(Point,4326)` (Mittel der Adressen), `addresses` | eine Zeile je Straße, PLZ, Ort und Gemeinde (Stichtag 01.10.2026: 143.922); Stufe 1 der Adresssuche |
| `address` | `id` (PK `pk_address`, Adresscode `ADRCD` des BEV), `street_id` (→ `address_street.id`, ohne Fremdschlüssel), `house_number` („1“, „3A“, „12/3“), `geom geography(Point,4326)`; Index `ix_address_street_number` auf `street_id`, `lower(house_number)` (`text_pattern_ops`, Präfixsuche) | alle Adressen des BEV-Adressregisters (Quelle `bev-address`, CC BY 4.0, [Endpunkt-Notiz](endpunkte/bev-adressen.md)); Stufe 2 der Adresssuche. Beide Tabellen sind nicht im EF-Modell: der Import ersetzt sie als Ganzes (`address_load` → `address_point` → `address_street_new`/`address_new` → Umbenennen in einer Transaktion, DDL in `AddressStore.CreateTablesSql`, auch von der Migration `AddressStreets` genutzt). Keine Verbindung zu `location` |
| `address_import` | `id` (immer 1), `reference_date`, `imported_at`, `checked_at`, `addresses` | importierter Stichtag, Zeitpunkt des Imports und der letzten Prüfung auf einen neueren (`address.refresh_days`) |

### Preise

Zwei Arten von Preisen (`TariffKind`): der **Ad-hoc-Preis** (`AD_HOC`, ohne Vertrag, von den Quellen je Ladepunkt gemeldet) und der **Tarif einer Ladekarte oder App** (`EMP`, aus dem Tarifkatalog). Beide werden in dieselben Preisbestandteile übersetzt (`PriceComponents`: kWh-Preis, Minutenpreis, Startgebühr, Blockiergebühr ab Minute X, kostenlos) und mit derselben Beispielrechnung verglichen, Unbekanntes zählt 0. Die **Beispielsitzung** gilt je Ladepunkt (`ChargingScenario.At` → `SessionExample`): `price.example_kwh` (Default 30 kWh) so lange, wie sie bei der kleineren Leistung von Ladepunkt (höchste Leistung der Stecker) und Beispielauto dauert – AC mit `price.vehicle_ac_kw` (11 kW), DC mit `price.vehicle_dc_kw` (150 kW Spitze) und der Ladezeit × `price.dc_taper_factor` (1,3), aufgerundet auf Minuten. Beispiele: 22 kW AC 164 min (Auto begrenzt), 3,7 kW AC 487 min (Ladepunkt begrenzt), 50 kW DC 47 min, 300 kW DC 16 min. So werden Minuten- und kWh-Tarife an langsamen und schnellen Ladepunkten fair verglichen; die Einstellungen gelten ohne Neustart (`/admin/settings`, Gruppe „Preise“).

| Tabelle | Spalten | Anmerkung |
|---|---|---|
| `emp` | `id`, `key` (eindeutig, 2–40 Zeichen aus Kleinbuchstaben, Ziffern und `-`, z. B. `oeamtc`), `name`, `website?`, `country` (ISO 3166-1) | Ladekarten-/App-Anbieter (ÖAMTC ePower, SMATRICS, …); ein Unternehmen kann CPO und EMP sein. Angelegt und aktualisiert vom Katalog-Import, bleibt bestehen, wenn eine Quelle ihn nicht mehr nennt |
| `charging_network` | `id`, `key` (eindeutig), `name`, `country`, `operators text[]` (Betreiber-IDs des EVSE-ID-Präfixes, z. B. `AT*VIE`, sortiert, groß), `source_key`, `price_note?`, `price_url?`, `sites jsonb` (Standorte mit eigener Preisseite: `name`, `latitude`, `longitude`, `url`) | ein Unternehmen mit seinen Präfixen oder ein Verbund; gehört genau einer Tarifquelle, die es mit jedem Import ersetzt |
| `tariff_plan` | `id`, `emp_id` (FK `RESTRICT`), `key` (Schlüssel des Tarifs beim Anbieter, z. B. `drive-smart`), `name`, `currency` (`varchar(3)`), `monthly_fee?` (`numeric(10,2)`), `prerequisites?`, `markets text[]`, `zones jsonb`, `notes?`, `source_key`, `source_url`, `source_date?` (Stand des Anbieters), `checked_on?` (letzte Prüfung gegen die Quelle; ändert keine Version), `confidence` (`HIGH`/`MEDIUM`/`LOW`), `valid_from`, `valid_to?` (exklusiv), `content_hash`; eindeutig (`emp_id`, `key`, `valid_from`), Index `IX_tariff_plan_open` auf `source_key` für offene Versionen | eine Version eines Tarifs ([ADR-0013](adr/0013-ladetarife-netze-zonen-katalog.md)). `zones` = Liste von Zonen (`kind` `OPERATOR`/`HOME`/`PARTNER`/`ROAMING`, `networks`, `operators`, `excludedNetworks`, `excludedOperators`, `notes`, bei Betreiberpreis `pricing: "OPERATOR_AD_HOC"` mit `markupPercent?` und `markupSessionFee?` statt eigener Preise, bei nicht veröffentlichtem Preis `pricing: "UNKNOWN"`, optional `currents` (`AC`/`DC`: Zone gilt nur für diese Stromart) und `countries` (ISO 3166-1 alpha-2: Zone gilt nur für Ladepunkte in diesen Ländern, z. B. Auslandspreise), sonst `prices[]` mit `current` AC/DC, `minKw?`, `pricePerKwh`, `pricePerMinute`, `sessionFee`, `blockingFeePerMinute`, `blockingAfterMinutes`, `maxBlockingFee?`, `blockingFrom?` (`CHARGING_END`, sonst ab Ladebeginn), `startTime?`/`endTime?` als Zeitfenster, `days?` (ISO-Wochentage), `blockingStartTime?`/`blockingEndTime?` (Fenster der abgerechneten Blockierminuten), `maxBlockingMinutes?`). Eine Änderung des Inhalts (anderer `content_hash`) schließt die Version und öffnet eine neue; die Grundgebühr gehört nicht zur Beispielrechnung |
| `price_observation` (Hypertable, Chunks zu 30 Tagen, Kompression nach 30 Tagen, keine Löschfrist) | PK `evse_id` + `time` + `source_key` + `tariff_kind`; `tariff_kind` (`AD_HOC`), `currency`, `price_per_kwh`, `price_per_minute`, `blocking_fee_per_minute` (je `numeric(10,4)`), `session_fee` (`numeric(10,2)`), `blocking_after_minutes`, `free_of_charge` | eine Zeile je **Preisänderung** eines Ladepunkts und einer Quelle (`time` = erste Beobachtung des Preises); unveränderte Meldungen erzeugen keine Zeile. `tariff_kind` `EMP` ist für Preise vorgesehen, die EMP-Apps je Ladepunkt melden |
| `evse_price_current` | PK `evse_id` + `source_key` + `tariff_kind`, dieselben Preisspalten, `since` (seit wann so), `observed_at` (zuletzt bestätigt) | aktueller Preis je Ladepunkt und Quelle; Vergleichsbasis für die Änderungserkennung im Ingest |

Der Preisvergleich je Ladepunkt (`GET /evses/{evseId}/prices`, Felder `emps` und `unavailable`) rechnet zur Abfragezeit über alle offenen Tarifversionen: Betreiber des EVSE ist der Betreiberteil seiner EVSE-ID, bei Surrogat-IDs die Betreiber-ID (`operator.country*operator.operator_id`) des Betreibers von EVSE oder Standort; Stromart DC bei `power_type` DC (ohne Angabe bei einem DC-Stecker), sonst AC; Leistung = höchste Leistung der Stecker. Je Tarif gilt die erste passende Zone nach den Regeln von [ADR-0013](adr/0013-ladetarife-netze-zonen-katalog.md); Tarife ohne passende Zone stehen in `unavailable`. Eine Zone mit Betreiberpreis rechnet mit dem aktuellen Ad-hoc-Preis des EVSE (`evse_price_current`) mal (1 + `markupPercent`/100) je Betrag plus `markupSessionFee`; ist keiner bekannt, steht der Tarif in `priceUnknown`, ebenso jeder Tarif, dessen Zone `UNKNOWN` ist. Die gültigen Tarife (wenige Hundert Zeilen) und Netze werden je Anfrage einmal geladen und im Speicher zugeordnet. Ein unplausibler Ad-hoc-Preis (außerhalb `price.min_plausible_kwh`–`price.max_plausible_kwh` oder 0 ohne `free_of_charge`, [05](05-crawling-konzept.md#plausibilität-der-ad-hoc-preise)) trägt `plausible: false` und ist keine Basis für Betreiberpreise und den günstigsten Ad-hoc-Preis eines Standorts.

Ein Tarif gilt als **veraltet** (`stale`), wenn weder der Stand noch die letzte Prüfung innerhalb von `tariff.stale_months` (Default 6) liegt; **bald zu prüfen** ist er, wenn beide älter als die Hälfte dieser Spanne sind (Worker-Log).

Ein Anbieter, dessen Tarife alle beendet sind (z. B. eine eingestellte App), bleibt in `emp` stehen, weil seine Tarifversionen auf ihn verweisen (FK `RESTRICT`, Historie), fehlt aber in `listEmps` und damit in der Auswahl der eigenen Ladekarten; schon gespeicherte eigene Karten bleiben gültig.

Preiselemente mit Zeitfenster oder Wochentagen gelten für eine Sitzung, die jetzt (Ortszeit Europe/Vienna) beginnt; Blockierminuten zählen nur, soweit sie ab diesem Beginn in das Blockier-Zeitfenster fallen, höchstens `maxBlockingMinutes`; die Blockiergebühr ist auf `maxBlockingFee` gedeckelt und entfällt in der Beispielsitzung, wenn sie erst ab Ladeende zählt.

Hat das Netz des Betreibers einen Preis-Hinweis, liefert `getEvsePrices` ihn als `priceNote` mit der Seite des nächsten Standorts (≤ 300 m) oder des Netzes.

Das Land eines Ladepunkts für Zonen mit `countries` ist das Land seines Standorts (`location.country`), ohne Standortland der Länderteil der EVSE-ID; eine Zone eines Landes geht innerhalb ihrer Art der Zone für alle Länder vor.

Vorgesehen: Szenarien je Fahrzeug des Kontos (Leistung und Energie aus dem Fahrzeug der Ladeplanung).

## Lebenszyklus

Standorte und EVSEs sind `ACTIVE`, bis keine Quelle sie mehr meldet; dann `REMOVED` (`PLANNED` setzt derzeit keine Quelle). Nur `ACTIVE` erscheint auf der Karte, in Suche, Statistik, Ladeparks, Korridor- und Flächenabfragen und Detailseiten (`GET /locations/{id}` antwortet für entfernte Standorte `404`, `GET /evses/{id}` liefert sie mit `lifecycle: "REMOVED"`); Favoriten behalten entfernte Standorte mit `removed: true`. Zeitreihen, Preise und Quell-Datensätze bleiben erhalten.

Regeln (`LocationLifecycle`, alle 10 Minuten im Worker, siehe docs/02):

- **Gezählte Inventarläufe**: `crawler_run` mit `job_type = INVENTORY_FULL`, `result = OK`, `records > 0`, alle Ingest-Nachrichten des Laufs verarbeitet (`ingest_queue.job_id`), und mindestens halb so viele Datensätze wie der größte der drei vorherigen Läufe derselben Quelle und desselben Ziels – eine Quelle, die nur einen Bruchteil liefert, entfernt nicht den Rest. Ein Lauf deckt die Regionen seines Ziels ab (`crawler_run.target`, z. B. `AT-1,…,AT-9`). Es zählen nur Läufe, die noch in `crawler_run` stehen (`crawler.run_retention_days`).
- **Fehlender Quell-Datensatz**: ein `source_record` (Standort, oder EVSE über seinen Standort) fehlt, wenn mindestens `lifecycle.missing_runs` (Default 2) gezählte Läufe seiner Quelle über die Region des Standorts nach seinem `last_seen` begonnen haben. Inaktive Stationen liefert die E-Control-Quelle gar nicht erst (`INACTIVE` wird übersprungen), sie fehlen also ebenso. Standorte ohne Region (außerhalb der Regionspolygone) fehlen nie.
- **Entfernen**: eine Entität, deren Quell-Datensätze alle fehlen, wird `REMOVED`; bei mehreren Quellen erst, wenn jede sie nicht mehr meldet.
- **Wieder aktiv**: meldet eine Quelle die Entität wieder, setzt der Ingest sie sofort auf `ACTIVE`; ebenso der Job, wenn ein Datensatz nicht mehr fehlt (z. B. nach Erhöhen der Schwelle).
- **Leere Ladestationen**: `charging_station`-Zeilen ohne EVSE (die Quelle hat die Ladepunkte einer anderen Stationskennung zugeordnet) werden gelöscht; entfernte EVSEs behalten ihre Station.

## Identity Resolution

### EVSE-ID-Normalisierung (`EvseId` Value Object)

1. Trim, Uppercase.
2. Erkenne Format: `AT*EVN*E1234*5`, `AT-EVN-E1234-5`, `ATEVNE12345`; selten auch DIN-Format `+43*811*…` (OICP erlaubt beides).
3. `country` = erste 2 Zeichen, `operator_id` = 3 Zeichen nach erstem Separator (bzw. Position 3–5 ohne Separator), `outlet_id` = Rest ab `E`.
4. `evse_id_norm` = alle Separatoren (`*`, `-`, Leerzeichen) entfernt. **Achtung:** ohne Separatoren können theoretisch Kollisionen entstehen (`E12*34` vs. `E123*4`). Praktisch ist das vernachlässigbar, wird aber beim Insert geprüft: existiert bereits ein EVSE mit gleicher `evse_id_norm`, aber anderer separierter Form, wird ein `source_conflict` erzeugt statt gemerged.
5. Validierung: Regex `^[A-Z]{2}\*?[A-Z0-9]{3}\*?E[A-Z0-9\*]{1,30}$`.

### Matching-Kaskade für einen Quell-Datensatz

Umgesetzt im `IngestProcessor` (Reihenfolge der Prüfung: gelernter Datensatz, dann 1, 3, 4, 5). Ein `source_record`, dessen Entität gelöscht wurde, wird neu zugeordnet. Standorte ohne Ländercode werden übersprungen (nicht geraten).

| Schritt | Methode | `match_method` | Confidence |
|---|---|---|---|
| 1 | Eine EVSE-ID des Datensatzes ist bekannt: der Standort dieses EVSE (`EVSE_ID_NORMALIZED`), das EVSE selbst bei gleicher Schreibweise (`EVSE_ID_EXACT`). Gleiche normalisierte Form mit anderen Separatoren → `source_conflict` (`evse_id`) statt Merge | `EVSE_ID_EXACT`, `EVSE_ID_NORMALIZED` | 1.0 |
| 2 | Die Quell-Id ist schon in `source_record` gemappt (jede weitere Lieferung) | unverändert | gelernt |
| 3 | Standort einer anderen Quelle ≤ 50 m mit demselben Betreiber und ähnlicher Zahl an EVSEs (höchstens doppelt so viele auf einer Seite, unbekannt zählt als passend). Betreiber gleich: gleicher dreistelliger Operator-Code, sonst gleiche Namen nach Entfernen von Rechtsformen und Satzzeichen (alle Wörter des kürzeren Namens im längeren oder gleiches erstes Wort, `OperatorNames.Same`) | `GEO_OPERATOR` | 0.9 |
| 4 | Der einzige Standort einer anderen Quelle ≤ 25 m, der keinen anderen Betreiber hat (eine Seite ohne Betreiber reicht) | `GEO_ONLY` | 0.6 |
| 5 | kein Match | `CREATED` | 1.0 – neuer Standort; EVSEs ohne EVSE-ID als `SURROGATE`, eine später gelieferte freie EVSE-ID ersetzt das Surrogat, eine vergebene erzeugt einen Konflikt |

Regeln der Geo-Schritte (3, 4): Kandidaten sind nur aktive Standorte, die die liefernde Quelle noch nicht kennt (eine Quelle liefert denselben Standort nicht unter zwei Ids) und die in derselben Nachricht nicht schon einem anderen Datensatz der Quelle zugeordnet wurden. Passen mehrere Kandidaten, entsteht ein `source_conflict` mit `attribute = identity`, `entity_id` = nächster Kandidat und `values` = `{"source", "externalId", "latitude", "longitude", "candidates": [Standort-Ids], "record": {"name", "address", "postalCode", "city", "operatorId", "operatorName", "evses": [{"evseId", "maxPowerKw"}]}}` – `record` ist der Datensatz, wie die Quelle ihn geliefert hat, damit eine Entscheidung ohne Blick in die Quelle möglich ist; der Datensatz wird nicht übernommen, bis jemand entscheidet. Ein Datensatz hat je Attribut höchstens einen offenen Konflikt (Schlüssel `source` + `externalId` in `values`): jeder weitere Lauf der Quelle aktualisiert `values` und `entity_id` des offenen Konflikts, statt einen neuen anzulegen. Entscheidungen (`POST /conflicts/{id}/resolve`, gesammelt `POST /conflicts/resolve`) stehen in `match_decision`: `MERGE` legt den `source_record` des Datensatzes auf den gewählten Kandidaten (`match_method = MANUAL`, Confidence 1), der nächste Lauf findet ihn über Schritt 2; `SEPARATE` lässt der Merge die Geo-Schritte für diesen Datensatz überspringen, der nächste Lauf legt einen eigenen Standort an (`CREATED`). An einem per Geo-Schritt oder von Hand (`MANUAL`) zugeordneten Standort legt ein Datensatz keine EVSEs ohne EVSE-ID an, solange andere Quellen dort aktive EVSEs haben – sie würden dieselben Ladepunkte doppelt zählen; EVSEs mit EVSE-ID laufen durch Schritt 1.

Ein `evse_id`-Konflikt (Schritt 1: gleiche normalisierte EVSE-ID, andere Trennzeichen) hat `entity_id` = das gespeicherte EVSE und `values` = `{"stored", "<source>": gelieferte Form, "source", "externalId" (Ladepunkt-Datensatz der Quelle), "locationId" (Standort, an dem er geliefert wurde)}`; der Datensatz wird nicht übernommen. Entscheidungen: `same` legt einen `source_record` des Ladepunkt-Datensatzes auf das gespeicherte EVSE (`MANUAL`), der nächste Lauf findet es über Schritt 2 und Status wie Preise der Quelle fließen dorthin; `keep` schreibt `match_decision` `KEEP` (Schlüssel `source` + `externalId` des Ladepunkts), der Merge überspringt den Ladepunkt danach ohne Konflikt. `match_decision` ist je Quelle und externe Id eindeutig, also dürfen sich Standort- und Ladepunkt-Ids einer Quelle nicht überschneiden (bei allen angebundenen Quellen der Fall).

**Vorschläge (`suggestion` in `GET /conflicts`, angewendet von `POST /conflicts/apply-suggestions`):** eine Heuristik aus dem, was der Konflikt zeigt, nie Confidence 1. `identity` mit bekanntem `record`: der einzige Kandidat desselben Betreibers (Code oder Name wie beim Matching) mit derselben Zahl an Ladepunkten → `merge` (0,8); sonst der nächste Kandidat desselben Betreibers → `merge` (0,6); ohne Kandidaten dieses Betreibers kein Vorschlag. `identity` ohne `record` (Konflikt vor dem nächsten Lauf der Quelle): der nächste Kandidat, wenn alle Kandidaten einen Betreiber teilen → `merge` (0,6). `evse_id`: `same` (0,95), wenn `locationId` der Standort des gespeicherten EVSE ist, sonst `same` (0,6); ohne `locationId` oder ohne aktives EVSE kein Vorschlag. Typischer Fall Deutschland: ein MobiData-Standort passt zu mehreren Zeilen des Bundesnetzagentur-Registers derselben Adresse und desselben Betreibers (0 bis 10 m) – Vorschlag ist die nächste Zeile; die Ladepunkte beider Quellen bleiben nebeneinander, bis Surrogate per EVSE-ID zusammengeführt werden (offen, [09](09-roadmap.md)).

Besonderheit **Tesla Supercharger**: Tesla vergibt EVSE-IDs im Roaming (sichtbar in EMP-Apps und im amtlichen Verzeichnis), zeigt sie in der eigenen App aber nicht. Hier ist Schritt 3 (Operator `Tesla` + Geo + Stallanzahl) der Regelfall; die Station-Ebene der Tesla-Quelle wird dann über die amtliche Quelle auf einzelne EVSEs verteilt. Wenn eine Quelle nur Standort-Status liefert („3 von 8 frei“), wird das als `location_status_observation` gespeichert und **nicht** auf einzelne EVSEs geraten.

### Quellen-Präzedenz je Attribut

Jedes Attribut (`name`, `address`, `postal_code`, `city`, `geom`, `operator_id`, `access`, `opening_times`, `green_energy`, `free_parking`, `roofed_parking`, `barrier_free_parking_spaces`, `price_url` am Standort; `location_id`, `operator_id`, `max_power_kw`, `power_type`, `physical_reference`, `connectors`, `auth_methods`, `roaming` am EVSE) hat in `attribute_origin` eine besitzende Quelle. Sie ändert es bei jedem neuen Wert; eine andere Quelle übernimmt es nur, wenn ihr Tier für die Attributgruppe stärker ist und sie einen Wert liefert (`AttributePrecedence`); bei gleichem Rang bleibt der erste Besitzer. Bleiben alle Werte gleich, wird die Entität bestätigt (`last_confirmed_at`), sonst nur gesehen (`last_seen`). Die Tiers aus der Tabelle `source` stehen für die Arten von Quellen: `PARTNER` = CPO direkt, `OFFICIAL` = amtliches Verzeichnis/NAP, `APP_BACKEND`/`PUBLIC_WEB` = Apps und Web-Karten von EMPs, `COMMUNITY` = Open Charge Map, `OPEN_DATA` = OpenStreetMap; eine Quelle ohne Eintrag in `source` hat den niedrigsten Rang.

| Attributgruppe | Reihenfolge (stärkste zuerst) |
|---|---|
| Koordinaten (`geom`) | CPO > amtliches Verzeichnis > OSM > OCM > EMP-Apps/Web |
| Stecker/Leistung (`max_power_kw`, `power_type`, `connectors`) | CPO > amtliches Verzeichnis > EMP-Apps/Web > OCM > OSM |
| alle übrigen (Name, Adresse, Betreiber, Zugang, Ausstattung, Zahlung …) | amtliches Verzeichnis > CPO > EMP-Apps/Web > OCM > OSM |

Vorgesehen für weitere Datenarten: Status CPO > EMP (Roaming) > Aggregator; Ad-hoc-Preis amtliches Verzeichnis/NAP > CPO-App > EMP-App; EMP-Tarif jeweilige EMP-App > Tarifaggregatoren > manuelle Pflege.

## Schema (Auszug aus den Migrationen `ChargingDomain`, `OccupancyHourly` und `EvseAuthAndFacilities`)

Die Tabellen entstehen aus dem EF-Core-Modell (`src/ChargeGlobe.Infrastructure/Persistence/ChargingModel.cs`); was EF Core nicht modellieren kann, ergänzt die Migration per SQL:

```sql
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS timescaledb;

CREATE TABLE evse (
  id                  uuid PRIMARY KEY,
  evse_id_norm        text NOT NULL,            -- UNIQUE (IX_evse_evse_id_norm)
  evse_id_display     text NOT NULL,
  identity_kind       text NOT NULL,            -- EVSE_ID | SURROGATE
  location_id         uuid NOT NULL REFERENCES location(id) ON DELETE RESTRICT,
  charging_station_id uuid REFERENCES charging_station(id) ON DELETE SET NULL,
  operator_id         uuid REFERENCES operator(id) ON DELETE SET NULL,
  max_power_kw        numeric(7,2),
  power_type          text,
  physical_reference  text,
  auth_methods        text[] NOT NULL DEFAULT '{}',  -- AuthMethod names
  roaming             boolean,
  lifecycle           text NOT NULL,
  first_seen          timestamptz NOT NULL,
  last_seen           timestamptz NOT NULL,
  last_confirmed_at   timestamptz
);

-- je Hypertable (evse_status_observation, location_status_observation):
SELECT create_hypertable('evse_status_observation', by_range('time', INTERVAL '1 day'));
ALTER TABLE evse_status_observation SET (timescaledb.compress,
  timescaledb.compress_segmentby = 'evse_id, source_key',
  timescaledb.compress_orderby   = 'time DESC');
SELECT add_compression_policy('evse_status_observation', INTERVAL '7 days');
SELECT add_retention_policy('evse_status_observation', INTERVAL '180 days');

CREATE TRIGGER location_assign_region BEFORE INSERT OR UPDATE OF geom ON location
  FOR EACH ROW EXECUTE FUNCTION location_assign_region();   -- region_code per ST_Covers(region.geom, location.geom)

-- Migration OccupancyHourly
CREATE MATERIALIZED VIEW evse_occupancy_hourly
WITH (timescaledb.continuous, timescaledb.materialized_only = false) AS
SELECT time_bucket(INTERVAL '1 hour', time) AS bucket, evse_id,
       count(*) FILTER (WHERE status = 'CHARGING')  AS obs_charging,
       count(*) FILTER (WHERE status = 'AVAILABLE') AS obs_available,
       count(*)                                     AS obs_total
FROM evse_status_observation
GROUP BY bucket, evse_id
WITH NO DATA;
SELECT add_continuous_aggregate_policy('evse_occupancy_hourly',
  start_offset => INTERVAL '7 days', end_offset => INTERVAL '1 hour', schedule_interval => INTERVAL '30 minutes');
```

`evse_occupancy_hourly` entsteht `WITH NO DATA`: Nur so darf `CREATE MATERIALIZED VIEW … WITH (timescaledb.continuous)` in einer Transaktion laufen – in der Migrations-Transaktion von EF Core und in den DO-Blöcken des idempotenten Skripts (`dotnet ef migrations script --idempotent`); `WITH DATA` bräche dort mit „cannot run inside a transaction block“ ab. Gefüllt wird das Aggregat von der Refresh-Policy (TimescaleDB-Job, kein eigener Worker-Job); bis dahin liefert die Real-Time-Aggregation dieselben Zahlen aus den Rohdaten. Die Policy materialisiert nur die letzten 7 Tage; ältere Chunks sind komprimiert und ändern sich nicht mehr.

Geo-Queries laufen über `location.geom` mit GiST-Index; die Karten-API liefert EVSE-Cluster per Server-Side-Tiles erst ab Phase 2, im MVP reicht bbox + Limit.
