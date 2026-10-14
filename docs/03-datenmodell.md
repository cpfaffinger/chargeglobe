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
                │                                                   └─ EvseTariffAssignment ─ Tariff ─ Emp
                │                                                           │
                └─ Source ─ SourceRecord (Quell-ID ↔ kanonische ID) ─ RawSnapshot   PriceObservation (Zeitreihe)
                                  └─ AttributeOrigin
```

### Stammdaten

| Tabelle | Wichtige Spalten | Anmerkung |
|---|---|---|
| `region` | `code` (PK, `AT-9` Wien, `AT-3` NÖ, …), `name`, `geom geography(MultiPolygon,4326)` (GiST) | ISO 3166-2 Codes; Polygone aus OSM-Admin-Grenzen (Seed vorgesehen) |
| `operator` | `id`, `name`, `country`, `operator_id` (z. B. `EVN`, eindeutig je `country`), `website`, `is_cpo`, `is_emp` | EVSE-ID-Präfix `AT*EVN` → Operator |
| `emp` | `id`, `name`, `app_name`, `country` | Ladekarten-/App-Anbieter (ÖAMTC ePower, Electroverse, …); ein Unternehmen kann CPO und EMP sein |
| `location` | `id uuid`, `name`, `address`, `postal_code`, `city`, `country`, `geom geography(Point,4326)` (GiST), `operator_id`, `region_code`, `opening_times jsonb` (OCPI `Hours`), `access` (`PUBLIC`/`RESTRICTED`), `lifecycle` (`ACTIVE`/`PLANNED`/`REMOVED`), `first_seen`, `last_seen`, `last_confirmed_at` | `region_code` setzt der Trigger `location_assign_region` bei Insert und bei Änderung von `geom` (Region, deren Polygon den Punkt enthält) |
| `charging_station` | `id`, `location_id`, `label`, `physical_reference` | optional; nur wenn Quelle Säulen-Ebene liefert |
| `evse` | `id uuid`, `evse_id_norm` (UNIQUE, z. B. `ATEVNE12345`), `evse_id_display` (`AT*EVN*E1234*5`), `location_id`, `charging_station_id?`, `operator_id`, `max_power_kw`, `power_type` (OCPI: `AC_1_PHASE`, `AC_2_PHASE`, `AC_2_PHASE_SPLIT`, `AC_3_PHASE`, `DC`), `physical_reference`, `first_seen`, `last_seen`, `last_confirmed_at`, `lifecycle` (`ACTIVE`/`PLANNED`/`REMOVED`), `identity_kind` (`EVSE_ID` / `SURROGATE`) | Ladepunkte ohne bekannte EVSE-ID bekommen eine Surrogat-ID `XX*CG*E<16 Hex-Zeichen aus dem Zufallsteil der UUID>` und `identity_kind=SURROGATE`; `XX` ist kein Ländercode, Kollisionen mit echten IDs sind ausgeschlossen |
| `connector` | `id`, `evse_id`, `ordinal` (ab 1, eindeutig je EVSE), `standard` (OCPI `ConnectorType`: `IEC_62196_T2`, `IEC_62196_T2_COMBO`, `CHADEMO`, `DOMESTIC_F`, `TESLA_S`, …, zusätzlich `UNKNOWN`), `format` (`SOCKET`/`CABLE`), `power_type`, `max_voltage`, `max_amperage`, `max_power_kw` | |

### Provenienz

| Tabelle | Spalten | Zweck |
|---|---|---|
| `source` | `key` (PK: `econtrol`, `ocm`, `osm`, `evn`, `oeamtc`, …), `display_name`, `tier` (`OFFICIAL`, `OPEN_DATA`, `PUBLIC_WEB`, `APP_BACKEND`, `PARTNER`), `license`, `attribution_text`, `capabilities text[]` (Namen der Provider-Capabilities) | Registry der Quellen; die Felder kommen aus dem Descriptor des Adapters. Ob ein Provider aktiv ist, steht im Config-Service (`enabled`, Scope `provider:<key>`), nicht hier |
| `source_record` | PK `source_key` + `external_id` + `entity_type` (`LOCATION`/`EVSE`/`CONNECTOR`), `entity_id`, `match_method` (`EVSE_ID_EXACT`, `EVSE_ID_NORMALIZED`, `GEO_OPERATOR_HEURISTIC`, `MANUAL`, `CREATED` = Datensatz hat die Entität angelegt), `match_confidence` (0–1), `first_seen`, `last_seen`, `last_snapshot_id` | Quell-ID ↔ kanonische Entität (n:1) |
| `attribute_origin` | PK `entity_type` + `entity_id` + `attribute` (`geom`, `max_power_kw`, `name`, …), `source_key`, `observed_at`, `value_hash` (SHA-256) | welches Attribut stammt woher |
| `raw_snapshot` | `id` (bigint), `source_key`, `job_id`, `fetched_at`, `request_fingerprint` (z. B. `location:<externe Id>`), `content_hash` (SHA-256 des Payloads), `payload jsonb`, `http_status` | Replay; eindeutig je `source_key` + `content_hash` (ein erneut gesehener Datensatz aktualisiert nur `fetched_at`/`job_id`); Retention `ingest.snapshot_retention_days` (30) |
| `source_conflict` | `id`, `entity_type`, `entity_id`, `attribute`, `values jsonb` (je Quelle), `detected_at`, `resolved_at`, `resolution` | Review-Queue im Admin (Index auf offene Konflikte) |

`source_key` verweist fachlich auf `source.key`, ist aber bewusst kein Fremdschlüssel: Adapter können liefern, bevor ihre `source`-Zeile existiert, und die Zeitreihen sparen sich die Prüfung je Insert.

### Status (Zeitreihen)

| Tabelle | Spalten | Anmerkung |
|---|---|---|
| `evse_status_observation` (Hypertable, Tages-Chunks) | PK `evse_id` + `time` + `source_key`, `status` (OCPI-Enum), `source_status_raw` (Originalstring), `source_reported_at` (falls Quelle einen Zeitstempel liefert), `latency_ms` (`time` − `source_reported_at`) | jede Beobachtung jeder Quelle; Kompression nach 7 Tagen (segmentiert nach `evse_id`, `source_key`), Retention 180 Tage |
| `evse_status_interval` | PK `evse_id` + `valid_from`, `status`, `valid_to` (NULL = aktuell), `source_key`, `confidence` (0–1) | nur bei Statuswechsel; höchstens ein offenes Intervall je EVSE (Unique-Index auf `evse_id` mit `valid_to IS NULL`); Basis für Belegungshistorie und Auslastungsstatistik |
| `evse_status_current` | `evse_id` (PK), `status`, `since`, `observed_at`, `source_key` | denormalisiert für Karten-Queries; ob der Wert veraltet ist, wird beim Lesen aus `observed_at` berechnet (`IsStale`) |
| `evse_occupancy_hourly` (Continuous Aggregate, vorgesehen) | `bucket` (1 h), `evse_id`, `obs_charging`, `obs_available`, `obs_total` | für Heatmaps/Auslastung ohne Scan der Rohdaten |
| `location_status_observation` (Hypertable, Tages-Chunks) | PK `location_id` + `time` + `source_key`, `evses_total`, `evses_available` | für Quellen, die nur Standort-Summen liefern (z. B. „3 von 8 frei“); Kompression und Retention wie oben |

**Umgesetzt (eine Quelle):** Jeder Statuswert wird Beobachtung (`ON CONFLICT DO NOTHING`, damit eine erneut verarbeitete Nachricht nichts doppelt schreibt). Ist er neuer als `evse_status_current.observed_at` und ändert er den Status, wird das offene Intervall geschlossen und ein neues geöffnet; sonst wird nur `observed_at` fortgeschrieben. Ältere Werte ändern nichts.

**Status-Zusammenführung bei mehreren Quellen (vorgesehen):** `evse_status_current` folgt der Quelle mit der höchsten Status-Präzedenz, die innerhalb ihres Frische-Fensters liegt (CPO-direkt > Roaming-EMP > Aggregator). Widersprüche innerhalb 60 s werden als `confidence < 1` markiert, nicht als Wechsel.

### Identität & Zugriff

| Tabelle | Spalten | Anmerkung |
|---|---|---|
| `AspNetUsers`, `AspNetRoles`, `AspNetUserRoles` | Standard ASP.NET Core Identity | E-Mail als Login, Passwort-Hash, Lockout |
| `role_permission` | `role_id`, `permission` | Rollen sind Berechtigungsbündel; Permissions kommen aus dem Katalog in Domain |
| `user_permission` | `user_id`, `permission`, `granted_by`, `granted_at` | direkte Zusatzrechte ohne Rolle |
| `api_key` | `id`, `name`, `key_hash`, `key_prefix` (erste 8 Zeichen zur Anzeige), `owner_user_id`, `permissions text[]` (Teilmenge der Rechte des Eigentümers), `expires_at`, `last_used_at`, `revoked_at`, `rate_limit_tier` | Klartext wird nur bei Erstellung einmal zurückgegeben |
| `audit_log` (Hypertable) | `time`, `actor_user_id?`, `actor_api_key_id?`, `action`, `entity_type`, `entity_id`, `payload jsonb`, `ip` | jede schreibende Aktion |

### Konfiguration & Job-Verteilung

| Tabelle | Spalten | Anmerkung |
|---|---|---|
| `app_setting` | `key`, `scope` (`global`, `provider:<key>`, `region:<code>`), `value jsonb`, `updated_by`, `updated_at`; Primärschlüssel `key` + `scope` | Werte des zentralen Config-Service; Defaults und Typen (auch „Secret“) stehen im Katalog im Code, Secrets sind verschlüsselt (Schlüsselring in `data_protection_key`) |
| `email_outbox` | `id`, `to`, `subject`, `body_text`, `body_html`, `template_key`, `status` (`QUEUED`, `SENT`, `FAILED`), `attempts`, `last_error`, `created_at`, `sent_at`, `next_attempt_at` | Postausgang des Mail-Service; der Worker versendet fällige Einträge (`status = QUEUED` und `next_attempt_at` leer oder erreicht), versendete werden nach `mail.retention_days` gelöscht |
| `poll_plan` | `id`, `provider_key`, `job_type` (`INVENTORY_FULL`, `STATUS_POLL`), `region_code`, `min_longitude`/`min_latitude`/`max_longitude`/`max_latitude` (Bounding-Box der Region), `interval`, `next_run_at`, `leased_by?`, `lease_until?`, `last_run_at?`, `last_result?`; eindeutig je `provider_key` + `job_type` + `region_code` | Arbeitsvorrat der Crawler; Claim per `FOR UPDATE SKIP LOCKED`, Lease 1 h |
| `provider_rate_budget` | `provider_key` (PK), `tokens`, `refilled_at`, `day_count`, `day_reset_at` (nächste Mitternacht UTC) | clusterweiter Token-Bucket; eine Zeilensperre je Token |
| `ingest_queue` | `id` (bigint), `provider_key`, `job_id`, `payload jsonb` (`IngestBatch`: Standorte mit `raw_snapshot`-Id oder Statuswerte), `enqueued_at`, `attempts`, `next_attempt_at?`, `error?`, `processed_at?` | Übergabe Crawler → Worker; gesperrt wird per Zeilensperre in der Verarbeitungstransaktion statt per Lease-Spalten; Retention `ingest.queue_retention_days` nach Verarbeitung |
| `job_lease` | `job_name` (PK), `leased_by`, `lease_until` | Single-Runner-Jobs (derzeit `ingest-retention`) |

### Preise

Tarife folgen dem OCPI-Tariff-Modell, weil nur das die Realität abbildet (kWh-Preis + Minutenpreis ab Minute X + Sessiongebühr + Blockiergebühr mit Zeitfenstern, Tag/Nacht, Leistungsklassen).

| Tabelle | Spalten | Anmerkung |
|---|---|---|
| `tariff` | `id`, `emp_id` (NULL = Ad-hoc-Preis des CPO), `operator_id?`, `name`, `product` (z. B. „ÖAMTC ePower Mitglied“, „EVN Ladekarte“, „Electroverse“), `currency`, `elements jsonb` (OCPI `TariffElement[]` mit `price_components` und `restrictions`), `source_key`, `valid_from`, `valid_to`, `url`, `verified_by` (`AUTO`/`MANUAL`) | Versionierung über `valid_from/valid_to` |
| `evse_tariff_assignment` | `evse_id`, `tariff_id`, `connector_id?`, `observed_at`, `valid_from`, `valid_to`, `source_key` | welcher Tarif gilt an welchem Ladepunkt (Zeitverlauf) |
| `price_observation` (Hypertable) | `time`, `evse_id`, `emp_id?`, `source_key`, `price_kwh`, `price_min`, `price_session`, `blocking_fee_min`, `blocking_after_min`, `currency`, `raw jsonb`, `tariff_id?` | flache Beobachtung „App X zeigt an Ladepunkt Y Preis Z“ – auch wenn das volle Tarifmodell nicht ableitbar ist |
| `price_scenario` | `code` (`AC_11KW_20KWH_2H`, `DC_50KW_30KWH_40MIN`, `HPC_150KW_40KWH_20MIN`), Parameter | für die Vergleichsrechnung in API/Frontend |

Die API berechnet pro Ladepunkt und EMP die **Szenario-Kosten** aus dem Tarif (`elements`) und, falls kein Tarif bekannt, aus der letzten `price_observation`.

## Identity Resolution

### EVSE-ID-Normalisierung (`EvseId` Value Object)

1. Trim, Uppercase.
2. Erkenne Format: `AT*EVN*E1234*5`, `AT-EVN-E1234-5`, `ATEVNE12345`; selten auch DIN-Format `+43*811*…` (OICP erlaubt beides).
3. `country` = erste 2 Zeichen, `operator_id` = 3 Zeichen nach erstem Separator (bzw. Position 3–5 ohne Separator), `outlet_id` = Rest ab `E`.
4. `evse_id_norm` = alle Separatoren (`*`, `-`, Leerzeichen) entfernt. **Achtung:** ohne Separatoren können theoretisch Kollisionen entstehen (`E12*34` vs. `E123*4`). Praktisch ist das vernachlässigbar, wird aber beim Insert geprüft: existiert bereits ein EVSE mit gleicher `evse_id_norm`, aber anderer separierter Form, wird ein `source_conflict` erzeugt statt gemerged.
5. Validierung: Regex `^[A-Z]{2}\*?[A-Z0-9]{3}\*?E[A-Z0-9\*]{1,30}$`.

### Matching-Kaskade für einen Quell-Datensatz

Umgesetzt im `IngestProcessor` sind die Schritte 1, 2 und 5: EVSE-ID (gleiche Schreibweise → `EVSE_ID_EXACT`; ein Standort, dessen EVSE schon bekannt ist → `EVSE_ID_NORMALIZED`; gleiche normalisierte Form, andere Separatoren → `source_conflict` statt Merge), gelernter `source_record` (jede weitere Lieferung derselben Quell-Id), sonst neue Entität (`CREATED`, ohne EVSE-ID als Surrogat; liefert die Quelle später eine noch freie EVSE-ID, ersetzt sie das Surrogat, eine vergebene erzeugt einen Konflikt). Ein `source_record`, dessen Entität gelöscht wurde, wird neu zugeordnet. Die Geo-Heuristiken (3, 4) kommen mit der zweiten Quelle. Standorte ohne Ländercode werden übersprungen (nicht geraten).


| Schritt | Methode | Confidence |
|---|---|---|
| 1 | EVSE-ID vorhanden und normalisiert gleich | 1.0 |
| 2 | Quelle hat keine EVSE-ID, aber eine eigene Station-ID, die schon in `source_record` gemappt ist | 1.0 (gelernt) |
| 3 | Heuristik: gleicher Operator + Distanz ≤ 40 m + gleiche Steckerkombination/Leistungsklasse + gleiche Anzahl Ladepunkte am Standort | 0.8 – Auto-Merge, aber markiert |
| 4 | Heuristik: Distanz ≤ 40 m + gleiche Anzahl Ladepunkte, Operator unbekannt | 0.5 – nur Vorschlag, landet in Review-Queue |
| 5 | kein Match | neues EVSE mit `identity_kind=SURROGATE`; sobald später eine Quelle mit EVSE-ID auf dieselbe Station matcht (Schritt 3), wird die Surrogat-ID durch die echte ersetzt und `source_record` nachgezogen |

Besonderheit **Tesla Supercharger**: Tesla vergibt EVSE-IDs im Roaming (sichtbar in EMP-Apps und im amtlichen Verzeichnis), zeigt sie in der eigenen App aber nicht. Hier ist Schritt 3 (Operator `Tesla` + Geo + Stallanzahl) der Regelfall; die Station-Ebene der Tesla-Quelle wird dann über die amtliche Quelle auf einzelne EVSEs verteilt. Wenn eine Quelle nur Standort-Status liefert („3 von 8 frei“), wird das als `location_status_observation` gespeichert und **nicht** auf einzelne EVSEs geraten.

### Quellen-Präzedenz je Attribut (Default, konfigurierbar)

Umgesetzt ist die Vorstufe: Ein Attribut (`name`, `address`, `postal_code`, `city`, `geom`, `operator_id`, `access`, `opening_times` am Standort; `location_id`, `operator_id`, `max_power_kw`, `power_type`, `physical_reference`, `connectors` am EVSE) gehört der Quelle, die es zuerst gesetzt hat (`attribute_origin`); nur sie ändert es. Bleiben alle Werte gleich, wird die Entität bestätigt (`last_confirmed_at`), sonst nur gesehen (`last_seen`). Die folgende Tabelle ist das Ziel, sobald eine zweite Quelle angebunden ist:


| Attribut | Reihenfolge |
|---|---|
| Koordinaten | CPO-Quelle > amtliches Verzeichnis > OSM > OCM > EMP-Apps |
| Name/Adresse | amtliches Verzeichnis > CPO > EMP > OCM > OSM |
| Stecker/Leistung | CPO > amtliches Verzeichnis > EMP > OCM > OSM |
| Status | CPO > EMP (Roaming) > Aggregator |
| Ad-hoc-Preis | amtliches Verzeichnis/NAP > CPO-App > EMP-App |
| EMP-Tarif | jeweilige EMP-App > Tarifaggregatoren > manuelle Pflege |

## Schema (Auszug aus der Migration `ChargingDomain`)

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
```

Vorgesehen ist das Continuous Aggregate `evse_occupancy_hourly` (`time_bucket('1 hour', time)` je `evse_id` mit Zählern für `CHARGING`, `AVAILABLE` und gesamt); es braucht eine eigene Migration außerhalb einer Transaktion.

Geo-Queries laufen über `location.geom` mit GiST-Index; die Karten-API liefert EVSE-Cluster per Server-Side-Tiles erst ab Phase 2, im MVP reicht bbox + Limit.
