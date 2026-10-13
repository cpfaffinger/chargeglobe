# ADR-0006: Minimale `.env`, zentraler Config-Service, skalierbare Crawler/Worker in Compose

**Status:** akzeptiert · **Datum:** 2026-10-13

## Kontext
Die App wird als Docker-Compose-Stack betrieben. Betreiber müssen Infrastruktur-Parameter wie den exponierten Port anpassen können, sollen aber nicht Dutzende fachliche Einstellungen in einer Datei pflegen. Crawling und Import müssen mit wachsendem Scope (AT → DACH) horizontal skalieren, ohne zusätzliche Infrastruktur wie Message-Broker.

## Entscheidung
1. **`.env` enthält nur Infrastruktur und Bootstrap** (Port, DB-Zugang, JWT-Secret, Admin-Seed, Instanzanzahlen, Log-Level, Basis-URL). **`.env.example` listet jede verfügbare Variable** mit Default und Kommentar; die Liste ist vollständig, nicht beispielhaft.
2. **Alles Fachliche steuert ein zentraler Config-Service**: Katalog von `SettingDefinition`s im Code (Key, Typ, Default, Beschreibung, Validierung, Scope), Werte in `app_setting`, Secrets verschlüsselt (Data-Protection), typisierte Accessoren mit Cache und `LISTEN/NOTIFY`-Invalidierung, Pflege über `GET/PUT /settings` (`settings:manage`) und ein generisch aus dem Katalog gerendertes Admin-UI. Änderungen wirken in allen Containern ohne Neustart. Provider-API-Keys gehören hierhin, nicht in `.env`.
3. **Crawler und Worker sind getrennte, zustandslose Container** aus einem Image (`--role crawler|worker`). Ihre Anzahl steht in `.env` (`CRAWLER_REPLICAS`, `WORKER_REPLICAS`) und wird von Compose per `deploy.replicas` umgesetzt. Arbeitsverteilung über Postgres: `poll_plan` mit Leases (`FOR UPDATE SKIP LOCKED`), `ingest_queue` zwischen Crawler und Worker, clusterweiter Token-Bucket je Provider, `job_lease` für Single-Runner-Jobs.

## Alternativen
- Alle Einstellungen in `.env`/`appsettings.json`: einfach, aber Neustart bei jeder Änderung, keine UI, Secrets im Klartext auf der Platte, Datei wächst mit jedem Provider.
- Message-Broker (RabbitMQ/Redis) für die Verteilung: zusätzlicher Service, den Compose und der Betreiber pflegen müssten; Postgres-Queues reichen für die Lastklasse (10³–10⁴ Nachrichten/Minute).
- Kubernetes-HPA statt Compose-Replicas: nicht im Scope, Compose ist Vorgabe.

## Konsequenzen
- Beim Start gleicht die API den Settings-Katalog mit `app_setting` ab (fehlende Keys = Default, unbekannte Keys = Warnung). Crawler und Worker lesen nur; solange die Datenbank beim Start noch nicht migriert ist, arbeiten sie mit den Defaults und versuchen es alle 5 s erneut.
- Der Typ einer Einstellung ist `string`, `int`, `number`, `bool`, `enum` oder `secret`; ob ein Wert ein Secret ist, steht im Katalog, nicht in der Tabelle.
- Der Schlüsselring der Data-Protection-API liegt unverschlüsselt in der Datenbank (`data_protection_key`). Die Verschlüsselung der Secrets schützt damit API-Antworten, Logs und Auszüge von `app_setting`, nicht gegen jemanden mit Vollzugriff auf die Datenbank.
- Laufzeit-Limits, die aus Einstellungen kommen, müssen so gebaut sein, dass eine Änderung sofort greift (Beispiel: das Auth-Rate-Limit führt den Grenzwert im Partitionsschlüssel).
- Tests für den Config-Service prüfen, dass jede `SettingDefinition` Typ, Beschreibung und einen gültigen oder bewusst fehlenden Default hat und dass `.env.example` genau die Umgebungsvariablen enthält, die Code und `docker-compose.yml` lesen.
- Rate-Limits sind clusterweit, nicht pro Prozess; ein einzelner Crawler darf das Budget nie allein ausschöpfen.
