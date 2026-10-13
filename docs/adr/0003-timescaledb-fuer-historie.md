# ADR-0003: TimescaleDB (+ PostGIS) für Status- und Preishistorie

**Status:** akzeptiert · **Datum:** 2026-10-13

## Kontext
Statusbeobachtungen: ~4 Mio Zeilen/Tag in Phase 1, ~50 Mio/Tag bei DACH-Ausbau. Abfragen: Zeitbereich je EVSE, Aggregate je Stunde/Wochentag, Retention. Gleichzeitig Geo-Queries für die Karte.

## Entscheidung
PostgreSQL 17 mit TimescaleDB (Hypertables, Compression, Continuous Aggregates, Retention Policies) und PostGIS im selben Cluster (Image `timescale/timescaledb-ha`). Rohbeobachtungen als Hypertable mit 180 Tagen Retention; abgeleitete Statusintervalle als normale Tabelle ohne Retention; stündliche Aggregate als Continuous Aggregate.

## Alternativen
- Natives Partitioning + pg_partman: möglich, aber Compression und Continuous Aggregates müssten nachgebaut werden.
- ClickHouse/InfluxDB: zweite Datenbank, zweite Betriebskomplexität – unverhältnismäßig für die Größenordnung.

## Konsequenzen
- EF Core kann Hypertables nicht anlegen → Raw-SQL-Migrationen für Timescale-Objekte.
- Timescale-Lizenz (TSL) für Community-Features ist für Self-Hosting unproblematisch; falls ein Betreiber ein reines Apache-2-Setup braucht, bleibt der Fallback „natives Partitioning“ durch die Repository-Abstraktion möglich.
