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
| `location` | `id uuid`, `name`, `address`, `postal_code`, `city`, `country`, `geom geography(Point,4326)` (GiST), `operator_id`, `region_code`, `opening_times jsonb` (OCPI `Hours`), `access` (`PUBLIC`/`RESTRICTED`), `green_energy` (100 % erneuerbar laut Betreiber, OCPI `EnergyMix.is_green_energy`), `free_parking`, `roofed_parking`, `barrier_free_parking_spaces` (Anzahl), `price_url` (Preisseite des Betreibers, OCPI `Tariff.tariff_alt_url`), `lifecycle` (`ACTIVE`/`PLANNED`/`REMOVED`), `first_seen`, `last_seen`, `last_confirmed_at` | `region_code` setzt der Trigger `location_assign_region` bei Insert und bei Änderung von `geom` (Region, deren Polygon den Punkt enthält) |
| `charging_station` | `id`, `location_id`, `label`, `physical_reference` | optional; nur wenn Quelle Säulen-Ebene liefert |
| `evse` | `id uuid`, `evse_id_norm` (UNIQUE, z. B. `ATEVNE12345`), `evse_id_display` (`AT*EVN*E1234*5`), `location_id`, `charging_station_id?`, `operator_id`, `max_power_kw`, `power_type` (OCPI: `AC_1_PHASE`, `AC_2_PHASE`, `AC_2_PHASE_SPLIT`, `AC_3_PHASE`, `DC`), `physical_reference`, `auth_methods text[]` (Vokabular `AuthMethod`, siehe unten; leer = unbekannt), `roaming` (Ladekarten anderer Anbieter werden akzeptiert; `null` = unbekannt), `first_seen`, `last_seen`, `last_confirmed_at`, `lifecycle` (`ACTIVE`/`PLANNED`/`REMOVED`), `identity_kind` (`EVSE_ID` / `SURROGATE`) | Ladepunkte ohne bekannte EVSE-ID bekommen eine Surrogat-ID `XX*CG*E<16 Hex-Zeichen aus dem Zufallsteil der UUID>` und `identity_kind=SURROGATE`; `XX` ist kein Ländercode, Kollisionen mit echten IDs sind ausgeschlossen |
| `connector` | `id`, `evse_id`, `ordinal` (ab 1, eindeutig je EVSE), `standard` (OCPI `ConnectorType`: `IEC_62196_T2`, `IEC_62196_T2_COMBO`, `CHADEMO`, `DOMESTIC_F`, `TESLA_S`, …, zusätzlich `UNKNOWN`), `format` (`SOCKET`/`CABLE`), `power_type`, `max_voltage`, `max_amperage`, `max_power_kw` | |

**Zahlungs- und Authentifizierungsarten (`AuthMethod`)**, gespeichert in `evse.auth_methods`, sortiert in dieser Reihenfolge: `RFID_READER` (Ladekarte), `APP`, `QR_CODE`, `WEBSITE`, `CHARGING_CONTRACT` (Ladekarte oder Vertrag ohne Angabe des Mediums), `CREDIT_CARD_PAYABLE`, `DEBIT_CARD_PAYABLE`, `CONTACTLESS_CARD_SUPPORT` (NFC-Karte), `CHIP_CARD_SUPPORT`, `PED_TERMINAL` (Terminal mit PIN), `CASH`. Werte mit OCPI-2.2.1-Gegenstück (`Capability`) tragen dessen Namen; App, QR-Code, Website, Vertrag und Bargeld kennt OCPI nicht, AFIR und die Quellen schon. Ad-hoc bezahlbar ohne Vertrag ist ein Ladepunkt mit `QR_CODE`, `WEBSITE`, `CREDIT_CARD_PAYABLE`, `DEBIT_CARD_PAYABLE`, `CONTACTLESS_CARD_SUPPORT`, `CHIP_CARD_SUPPORT`, `PED_TERMINAL` oder `CASH`.

### Provenienz

| Tabelle | Spalten | Zweck |
|---|---|---|
| `source` | `key` (PK: `econtrol`, `ocm`, `osm`, `evn`, `oeamtc`, …), `display_name`, `tier` (`OFFICIAL`, `OPEN_DATA`, `PUBLIC_WEB`, `APP_BACKEND`, `PARTNER`), `license`, `attribution_text`, `capabilities text[]` (Namen der Provider-Capabilities) | Registry der Quellen; die Felder kommen aus dem Descriptor des Adapters. Ob ein Provider aktiv ist, steht im Config-Service (`enabled`, Scope `provider:<key>`), nicht hier |
| `source_record` | PK `source_key` + `external_id` + `entity_type` (`LOCATION`/`EVSE`/`CONNECTOR`), `entity_id`, `match_method` (`EVSE_ID_EXACT`, `EVSE_ID_NORMALIZED`, `GEO_OPERATOR`, `GEO_ONLY`, `MANUAL`, `CREATED` = Datensatz hat die Entität angelegt), `match_confidence` (0–1), `first_seen`, `last_seen`, `last_snapshot_id` | Quell-ID ↔ kanonische Entität (n:1) |
| `attribute_origin` | PK `entity_type` + `entity_id` + `attribute` (`geom`, `max_power_kw`, `name`, …), `source_key`, `observed_at`, `value_hash` (SHA-256) | welches Attribut stammt woher |
| `raw_snapshot` | `id` (bigint), `source_key`, `job_id`, `fetched_at`, `request_fingerprint` (z. B. `location:<externe Id>`), `content_hash` (SHA-256 des Payloads), `payload jsonb`, `http_status` | Replay; eindeutig je `source_key` + `content_hash` (ein erneut gesehener Datensatz aktualisiert nur `fetched_at`/`job_id`); Retention `ingest.snapshot_retention_days` (30) |
| `match_decision` | PK `source_key` + `external_id`, `decision` (`MERGE`/`SEPARATE`), `location_id?` (gewählter Standort, `SET NULL` beim Löschen), `conflict_id`, `decided_at`, `decided_by?` (Nutzer) | Entscheidung eines Maintainers über einen mehrdeutigen Datensatz; der Merge liest `SEPARATE` vor den Geo-Schritten |
| `source_conflict` | `id`, `entity_type`, `entity_id`, `attribute`, `values jsonb` (je Quelle), `detected_at`, `resolved_at`, `resolution` | Review-Queue im Admin (Index auf offene Konflikte) |

`source_key` verweist fachlich auf `source.key`, ist aber bewusst kein Fremdschlüssel: Adapter können liefern, bevor ihre `source`-Zeile existiert, und die Zeitreihen sparen sich die Prüfung je Insert.

### Status (Zeitreihen)

| Tabelle | Spalten | Anmerkung |
|---|---|---|
| `evse_status_observation` (Hypertable, Tages-Chunks) | PK `evse_id` + `time` + `source_key`, `status` (OCPI-Enum), `source_status_raw` (Originalstring), `source_reported_at` (falls Quelle einen Zeitstempel liefert), `latency_ms` (`time` − `source_reported_at`) | jede Beobachtung jeder Quelle; Kompression nach 7 Tagen (segmentiert nach `evse_id`, `source_key`), Retention 180 Tage |
| `evse_status_interval` | PK `evse_id` + `valid_from`, `status`, `valid_to` (NULL = aktuell), `source_key`, `confidence` (0–1) | nur bei Statuswechsel; höchstens ein offenes Intervall je EVSE (Unique-Index auf `evse_id` mit `valid_to IS NULL`); Basis für Belegungshistorie und Auslastungsstatistik |
| `evse_status_current` | `evse_id` (PK), `status`, `since`, `observed_at`, `source_key` | denormalisiert für Karten-Queries; ob der Wert veraltet ist, wird beim Lesen aus `observed_at` berechnet (`IsStale`) |
| `evse_occupancy_hourly` (Continuous Aggregate über `evse_status_observation`) | `bucket` (1 h, UTC), `evse_id`, `obs_charging` (Beobachtungen mit `CHARGING`), `obs_available` (`AVAILABLE`), `obs_total` (alle) | für Heatmaps/Auslastung ohne Scan der Rohdaten (`GET /evses/{evseId}/occupancy`); Real-Time-Aggregation (`materialized_only = false`): Stunden oberhalb der Materialisierungsgrenze kommen direkt aus den Rohdaten; Refresh-Policy alle 30 min für die letzten 7 Tage bis 1 h vor jetzt; keine Retention (bleibt nach dem Löschen der Rohdaten erhalten) |
| `location_status_observation` (Hypertable, Tages-Chunks) | PK `location_id` + `time` + `source_key`, `evses_total`, `evses_available` | für Quellen, die nur Standort-Summen liefern (z. B. „3 von 8 frei“); Kompression und Retention wie oben |

**Umgesetzt (eine Quelle):** Jeder Statuswert wird Beobachtung (`ON CONFLICT DO NOTHING`, damit eine erneut verarbeitete Nachricht nichts doppelt schreibt). Ist er neuer als `evse_status_current.observed_at` und ändert er den Status, wird das offene Intervall geschlossen und ein neues geöffnet; sonst wird nur `observed_at` fortgeschrieben. Ältere Werte ändern nichts.

**Status-Zusammenführung bei mehreren Quellen (vorgesehen):** `evse_status_current` folgt der Quelle mit der höchsten Status-Präzedenz, die innerhalb ihres Frische-Fensters liegt (CPO-direkt > Roaming-EMP > Aggregator). Widersprüche innerhalb 60 s werden als `confidence < 1` markiert, nicht als Wechsel.

### Identität & Zugriff

| Tabelle | Spalten | Anmerkung |
|---|---|---|
| `AspNetUsers`, `AspNetRoles`, `AspNetUserRoles` | Standard ASP.NET Core Identity; dazu in `AspNetUsers` `CreatedAt`, `ApprovedAt?` (leer = wartet auf Freischaltung durch einen Admin), `DisabledAt?` (gesetzt = gesperrt) | E-Mail als Login, Passwort-Hash, Lockout |
| `user_preferences` | PK `user_id` (`ON DELETE CASCADE`), `document jsonb` (`home`, `filters`, `mapOptions`, Schema in `docs/06`), `updated_at` | Einstellungen eines Kontos, geräteübergreifend |
| `favorite_location` | PK `user_id` + `location_id` (beide mit `ON DELETE CASCADE`), `created_at` | Favoriten eines Kontos, höchstens 200 |
| `role_permission` | `role_id`, `permission` | Rollen sind Berechtigungsbündel; Permissions kommen aus dem Katalog in Domain |
| `user_permission` | `user_id`, `permission`, `granted_by`, `granted_at` | direkte Zusatzrechte ohne Rolle |
| `api_key` | `id`, `name`, `key_hash`, `key_prefix` (erste 8 Zeichen zur Anzeige), `owner_user_id`, `permissions text[]` (Teilmenge der Rechte des Eigentümers), `expires_at`, `last_used_at`, `revoked_at`, `rate_limit_tier` | Klartext wird nur bei Erstellung einmal zurückgegeben |
| `audit_log` (Hypertable) | `time`, `actor_user_id?`, `actor_api_key_id?`, `action`, `entity_type`, `entity_id`, `payload jsonb`, `ip` | jede schreibende Aktion |

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
| `job_lease` | `job_name` (PK), `leased_by`, `lease_until` | Single-Runner-Jobs (derzeit `ingest-retention`) |

### Preise

Zwei Arten von Preisen (`TariffKind`): der **Ad-hoc-Preis** (`AD_HOC`, ohne Vertrag, von den Quellen je Ladepunkt gemeldet) und der **Tarif einer Ladekarte oder App** (`EMP`, je Anbieter von Hand gepflegt). Beide werden in dieselben Preisbestandteile übersetzt (`PriceComponents`: kWh-Preis, Minutenpreis, Startgebühr, Blockiergebühr ab Minute X, kostenlos) und mit derselben Beispielrechnung verglichen: 30 kWh in 40 Minuten (`PriceExample`), Unbekanntes zählt 0.

| Tabelle | Spalten | Anmerkung |
|---|---|---|
| `emp` | `id`, `key` (eindeutig, 2–40 Zeichen aus Kleinbuchstaben, Ziffern und `-`, z. B. `oeamtc`), `name`, `website?` | Ladekarten-/App-Anbieter (ÖAMTC ePower, SMATRICS, Electroverse, …); ein Unternehmen kann CPO und EMP sein. Löschen nur ohne Tarife (FK `RESTRICT`) |
| `tariff` | `id` (vom Client gewählte UUID), `emp_id`, `name`, `currency` (`varchar(3)`), `valid_from`, `valid_to?` (exklusiv), `price_per_kwh_ac`, `price_per_kwh_dc`, `price_per_minute`, `blocking_fee_per_minute` (je `numeric(10,4)`), `session_fee`, `monthly_fee` (je `numeric(10,2)`), `blocking_after_minutes`, `roaming_only`, `operator_ids uuid[]` | Geltung: mit `operator_ids` nur an den EVSEs dieser Betreiber (eigenes Netz; Betreiber des EVSE, sonst des Standorts), sonst mit `roaming_only` an allen EVSEs mit `roaming = true`, sonst überall. Für Gleichstrom (`power_type` des EVSE `DC` oder ein DC-Stecker) zählt `price_per_kwh_dc`, sonst `price_per_kwh_ac`. Die Grundgebühr gehört nicht zur Beispielrechnung. Preisänderungen = neuer Tarif mit neuem `valid_from`, der alte bekommt `valid_to` |
| `price_observation` (Hypertable, Chunks zu 30 Tagen, Kompression nach 30 Tagen, keine Löschfrist) | PK `evse_id` + `time` + `source_key` + `tariff_kind`; `tariff_kind` (`AD_HOC`), `currency`, `price_per_kwh`, `price_per_minute`, `blocking_fee_per_minute` (je `numeric(10,4)`), `session_fee` (`numeric(10,2)`), `blocking_after_minutes`, `free_of_charge` | eine Zeile je **Preisänderung** eines Ladepunkts und einer Quelle (`time` = erste Beobachtung des Preises); unveränderte Meldungen erzeugen keine Zeile. `tariff_kind` `EMP` ist für Preise vorgesehen, die EMP-Apps je Ladepunkt melden |
| `evse_price_current` | PK `evse_id` + `source_key` + `tariff_kind`, dieselben Preisspalten, `since` (seit wann so), `observed_at` (zuletzt bestätigt) | aktueller Preis je Ladepunkt und Quelle; Vergleichsbasis für die Änderungserkennung im Ingest |

Der Preisvergleich je Ladepunkt (`GET /evses/{evseId}/prices`, Feld `emps`) rechnet zur Abfragezeit: alle Tarife, die jetzt gelten und am EVSE greifen, mit dem AC- oder DC-Preis, aufsteigend nach den Kosten der Beispielrechnung. Tarife sind wenige Zeilen und werden im Speicher gefiltert.

Vorgesehen: Tarifelemente nach OCPI (Zeitfenster, Tag/Nacht, Leistungsklassen) statt der flachen Spalten, wählbare Szenarien (AC 11 kW / 20 kWh / 2 h, DC 50 kW / 30 kWh / 40 min, HPC 150 kW / 40 kWh / 20 min) und eine Zuordnung Tarif ↔ Ladepunkt aus den Roaming-Listen der EMPs, wo `roaming` allein nicht reicht.

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

Regeln der Geo-Schritte (3, 4): Kandidaten sind nur aktive Standorte, die die liefernde Quelle noch nicht kennt (eine Quelle liefert denselben Standort nicht unter zwei Ids) und die in derselben Nachricht nicht schon einem anderen Datensatz der Quelle zugeordnet wurden. Passen mehrere Kandidaten, entsteht ein `source_conflict` mit `attribute = identity`, `entity_id` = nächster Kandidat und `values` = `{"source", "externalId", "latitude", "longitude", "candidates": [Standort-Ids]}`; der Datensatz wird nicht übernommen, bis jemand entscheidet. Entscheidungen (`POST /conflicts/{id}/resolve`) stehen in `match_decision`: `MERGE` legt den `source_record` des Datensatzes auf den gewählten Kandidaten (`match_method = MANUAL`, Confidence 1), der nächste Lauf findet ihn über Schritt 2; `SEPARATE` lässt der Merge die Geo-Schritte für diesen Datensatz überspringen, der nächste Lauf legt einen eigenen Standort an (`CREATED`). Ein offener Konflikt (`identity` wie `evse_id`) wird nur einmal angelegt, auch wenn jeder Lauf der Quelle ihn wiederholt. An einem per Geo-Schritt oder von Hand (`MANUAL`) zugeordneten Standort legt ein Datensatz keine EVSEs ohne EVSE-ID an, solange andere Quellen dort aktive EVSEs haben – sie würden dieselben Ladepunkte doppelt zählen; EVSEs mit EVSE-ID laufen durch Schritt 1.

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
