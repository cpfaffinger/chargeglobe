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
| `region` | `code` (`AT-9` Wien, `AT-3` NÖ, …), `name`, `geom geography(MultiPolygon)` | ISO 3166-2 Codes; Polygone aus OSM-Admin-Grenzen |
| `operator` | `id`, `name`, `country`, `operator_id` (z. B. `EVN`), `website`, `is_cpo`, `is_emp` | EVSE-ID-Präfix `AT*EVN` → Operator |
| `emp` | `id`, `name`, `app_name`, `country` | Ladekarten-/App-Anbieter (ÖAMTC ePower, Electroverse, …); ein Unternehmen kann CPO und EMP sein |
| `location` | `id uuid`, `name`, `address`, `postal_code`, `city`, `country`, `geom geography(Point)`, `operator_id`, `region_id`, `opening_times jsonb`, `access` (`PUBLIC`/`RESTRICTED`), `first_seen`, `last_seen`, `last_confirmed_at`, `status` | `region_id` wird via PostGIS beim Insert berechnet |
| `charging_station` | `id`, `location_id`, `label`, `physical_reference` | optional; nur wenn Quelle Säulen-Ebene liefert |
| `evse` | `id uuid`, `evse_id_norm` (UNIQUE, z. B. `ATEVNE12345`), `evse_id_display` (`AT*EVN*E1234*5`), `location_id`, `charging_station_id?`, `operator_id`, `max_power_kw`, `current_type` (`AC_1_PHASE`, `AC_3_PHASE`, `DC`), `physical_reference`, `first_seen`, `last_seen`, `last_confirmed_at`, `lifecycle` (`ACTIVE`/`PLANNED`/`REMOVED`), `identity_kind` (`EVSE_ID` / `SURROGATE`) | Ladepunkte ohne bekannte EVSE-ID bekommen eine Surrogat-ID `XX*CG*E<uuid-kurz>` und `identity_kind=SURROGATE` |
| `connector` | `id`, `evse_id`, `ordinal`, `standard` (OCPI: `IEC_62196_T2`, `IEC_62196_T2_COMBO`, `CHADEMO`, `DOMESTIC_F`, `TESLA_S`, …), `format` (`SOCKET`/`CABLE`), `power_type`, `max_voltage`, `max_amperage`, `max_power_kw` | |

### Provenienz

| Tabelle | Spalten | Zweck |
|---|---|---|
| `source` | `key` (`econtrol`, `ocm`, `osm`, `evn`, `oeamtc`, …), `tier`, `license`, `attribution_text`, `capabilities jsonb`, `enabled` | Registry der Quellen |
| `source_record` | `source_key`, `external_id`, `entity_type` (`LOCATION`/`EVSE`/`CONNECTOR`), `entity_id`, `match_method` (`EVSE_ID_EXACT`, `EVSE_ID_NORMALIZED`, `GEO_OPERATOR_HEURISTIC`, `MANUAL`), `match_confidence`, `first_seen`, `last_seen`, `last_snapshot_id` | Quell-ID ↔ kanonische Entität (n:1) |
| `attribute_origin` | `entity_type`, `entity_id`, `attribute` (`geom`, `max_power_kw`, `name`, …), `source_key`, `observed_at`, `value_hash` | welches Attribut stammt woher |
| `raw_snapshot` | `id`, `source_key`, `job_id`, `fetched_at`, `request_fingerprint`, `content_hash`, `payload jsonb` (komprimiert), `http_status` | Replay; Retention 30 Tage, Dedupe über `content_hash` |
| `source_conflict` | `entity_id`, `attribute`, `values jsonb` (je Quelle), `detected_at`, `resolved_at`, `resolution` | Review-Queue im Admin |

### Status (Zeitreihen)

| Tabelle | Spalten | Anmerkung |
|---|---|---|
| `evse_status_observation` (Hypertable) | `time`, `evse_id`, `source_key`, `status` (OCPI-Enum), `source_status_raw` (Originalstring), `source_reported_at` (falls Quelle einen Zeitstempel liefert), `latency_ms` | jede Beobachtung jeder Quelle; Compression nach 7 Tagen, Retention 180 Tage |
| `evse_status_interval` | `evse_id`, `status`, `valid_from`, `valid_to` (NULL = aktuell), `source_key`, `confidence` | nur bei Statuswechsel; Basis für Belegungshistorie und Auslastungsstatistik |
| `evse_status_current` | `evse_id` (PK), `status`, `since`, `observed_at`, `source_key`, `stale` (bool, berechnet aus Alter) | denormalisiert für Karten-Queries |
| `evse_occupancy_hourly` (Continuous Aggregate) | `bucket` (1 h), `evse_id`, `obs_charging`, `obs_available`, `obs_total` | für Heatmaps/Auslastung ohne Scan der Rohdaten |
| `location_status_observation` (Hypertable) | `time`, `location_id`, `source_key`, `evses_total`, `evses_available` | für Quellen, die nur Standort-Summen liefern (z. B. „3 von 8 frei“) |

**Status-Zusammenführung bei mehreren Quellen:** `evse_status_current` folgt der Quelle mit der höchsten Status-Präzedenz, die innerhalb ihres Frische-Fensters liegt (CPO-direkt > Roaming-EMP > Aggregator). Widersprüche innerhalb 60 s werden als `confidence < 1` markiert, nicht als Wechsel.

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
| `poll_plan` | `id`, `provider_key`, `job_type`, `region_code?`, `tile?`, `interval`, `next_run_at`, `leased_by?`, `lease_until?`, `last_run_at`, `last_result` | Arbeitsvorrat der Crawler; `SKIP LOCKED` |
| `provider_rate_budget` | `provider_key`, `tokens`, `refilled_at`, `day_count`, `day_reset_at` | clusterweiter Token-Bucket |
| `ingest_queue` | `id`, `provider_key`, `job_id`, `payload jsonb` (Ingest-Modell), `enqueued_at`, `leased_by?`, `lease_until?`, `attempts`, `error?` | Übergabe Crawler → Worker; Retention 7 Tage nach Verarbeitung |
| `job_lease` | `job_name` (PK), `leased_by`, `lease_until` | Single-Runner-Jobs (Reconcile, Retention, Aggregate) |

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

| Schritt | Methode | Confidence |
|---|---|---|
| 1 | EVSE-ID vorhanden und normalisiert gleich | 1.0 |
| 2 | Quelle hat keine EVSE-ID, aber eine eigene Station-ID, die schon in `source_record` gemappt ist | 1.0 (gelernt) |
| 3 | Heuristik: gleicher Operator + Distanz ≤ 40 m + gleiche Steckerkombination/Leistungsklasse + gleiche Anzahl Ladepunkte am Standort | 0.8 – Auto-Merge, aber markiert |
| 4 | Heuristik: Distanz ≤ 40 m + gleiche Anzahl Ladepunkte, Operator unbekannt | 0.5 – nur Vorschlag, landet in Review-Queue |
| 5 | kein Match | neues EVSE mit `identity_kind=SURROGATE`; sobald später eine Quelle mit EVSE-ID auf dieselbe Station matcht (Schritt 3), wird die Surrogat-ID durch die echte ersetzt und `source_record` nachgezogen |

Besonderheit **Tesla Supercharger**: Tesla vergibt EVSE-IDs im Roaming (sichtbar in EMP-Apps und im amtlichen Verzeichnis), zeigt sie in der eigenen App aber nicht. Hier ist Schritt 3 (Operator `Tesla` + Geo + Stallanzahl) der Regelfall; die Station-Ebene der Tesla-Quelle wird dann über die amtliche Quelle auf einzelne EVSEs verteilt. Wenn eine Quelle nur Standort-Status liefert („3 von 8 frei“), wird das als `location_status_observation` gespeichert und **nicht** auf einzelne EVSEs geraten.

### Quellen-Präzedenz je Attribut (Default, konfigurierbar)

| Attribut | Reihenfolge |
|---|---|
| Koordinaten | CPO-Quelle > amtliches Verzeichnis > OSM > OCM > EMP-Apps |
| Name/Adresse | amtliches Verzeichnis > CPO > EMP > OCM > OSM |
| Stecker/Leistung | CPO > amtliches Verzeichnis > EMP > OCM > OSM |
| Status | CPO > EMP (Roaming) > Aggregator |
| Ad-hoc-Preis | amtliches Verzeichnis/NAP > CPO-App > EMP-App |
| EMP-Tarif | jeweilige EMP-App > Tarifaggregatoren > manuelle Pflege |

## SQL-Skizze (Auszug)

```sql
create extension if not exists postgis;
create extension if not exists timescaledb;

create table evse (
  id                  uuid primary key default gen_random_uuid(),
  evse_id_norm        text not null unique,
  evse_id_display     text not null,
  identity_kind       text not null check (identity_kind in ('EVSE_ID','SURROGATE')),
  location_id         uuid not null references location(id),
  charging_station_id uuid references charging_station(id),
  operator_id         uuid references operator(id),
  max_power_kw        numeric(7,2),
  current_type        text,
  physical_reference  text,
  lifecycle           text not null default 'ACTIVE',
  first_seen          timestamptz not null default now(),
  last_seen           timestamptz not null default now(),
  last_confirmed_at   timestamptz
);

create table evse_status_observation (
  time               timestamptz not null,
  evse_id            uuid not null references evse(id),
  source_key         text not null,
  status             text not null,
  source_status_raw  text,
  source_reported_at timestamptz,
  latency_ms         int
);
select create_hypertable('evse_status_observation', by_range('time', interval '1 day'));
alter table evse_status_observation set (timescaledb.compress,
  timescaledb.compress_segmentby = 'evse_id, source_key',
  timescaledb.compress_orderby   = 'time desc');
select add_compression_policy('evse_status_observation', interval '7 days');
select add_retention_policy('evse_status_observation', interval '180 days');

create table evse_status_interval (
  evse_id    uuid not null references evse(id),
  status     text not null,
  valid_from timestamptz not null,
  valid_to   timestamptz,
  source_key text not null,
  confidence real not null default 1,
  primary key (evse_id, valid_from)
);
create index on evse_status_interval (evse_id, valid_to) where valid_to is null;

create materialized view evse_occupancy_hourly
with (timescaledb.continuous) as
select time_bucket('1 hour', time) as bucket, evse_id,
       count(*) filter (where status = 'CHARGING')  as obs_charging,
       count(*) filter (where status = 'AVAILABLE') as obs_available,
       count(*)                                      as obs_total
from evse_status_observation
group by bucket, evse_id;
```

Geo-Queries laufen über `location.geom` mit GiST-Index; die Karten-API liefert EVSE-Cluster per Server-Side-Tiles erst ab Phase 2, im MVP reicht bbox + Limit.
