# ADR-0002: Modularer Monolith statt Microservices

**Status:** akzeptiert · **Datum:** 2026-10-13

## Kontext
Open-Source-Projekt, kleines Team, Datenmengen im Bereich von Millionen Zeilen/Tag, aber keine harten Latenzanforderungen. Hauptkomplexität liegt in Quellen-Adaptern und Datenqualität, nicht in Skalierung.

## Entscheidung
Eine Solution mit klar getrennten Projekten (Domain, Application, Infrastructure, Providers.*, Api, Crawler, Worker). Drei Prozessrollen (Api, Crawler ×N, Worker ×M) aus einem Compose-Stack, eine Datenbank. Kein Message-Broker; Domain-Events über Outbox-Tabelle, Arbeitsverteilung über Lease-/Queue-Tabellen in Postgres ([ADR-0006](0006-konfiguration-env-minimal-config-service.md)).

## Konsequenzen
- Einfaches lokales Setup (`docker compose up`), niedrige Einstiegshürde für Contributors.
- Provider-Adapter sind trotzdem isoliert (eigene Projekte, nur Abhängigkeit auf Abstractions) und könnten später ausgelagert werden.
- Skalierung passiert über die Anzahl der Crawler-/Worker-Instanzen (`.env`), nicht über neue Services.
