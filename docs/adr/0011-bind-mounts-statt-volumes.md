# ADR-0011: Persistente Daten nur in Bind-Mounts relativ zur Compose-Datei

**Status:** akzeptiert · **Datum:** 2026-10-14

## Kontext
Die Daten des Stacks (Postgres-Cluster, self-signed Zertifikat) sollen dort liegen, wo das Deployment liegt: sichtbar im Dateisystem neben `docker-compose.yml`, mit normalen Werkzeugen zu sichern, zu kopieren, umzuziehen oder bewusst zurückzusetzen. Docker-Volumes (benannte wie anonyme) liegen dagegen unter `/var/lib/docker/volumes`, hängen am Compose-Projektnamen, lassen sich nur über Docker-Befehle untersuchen und können von globalen Aufräumbefehlen (`docker volume prune`, `docker compose down -v`) erfasst werden – auf einem Host, den sich chargeglobe mit fremden Stacks teilt.

## Entscheidung
1. **Keine Docker-Volumes.** `docker-compose.yml` deklariert keine benannten Volumes (kein Top-Level-Schlüssel `volumes:`), und die Images deklarieren kein `VOLUME`. Jeder Mount eines Services ist ein **Bind-Mount mit einem Pfad relativ zur Compose-Datei** (`./…`).
2. **Persistente Daten liegen unter `./data/`**, je Zweck ein Unterverzeichnis:

   | Pfad | Container | Inhalt |
   |---|---|---|
   | `./data/postgres` | `db`: `/home/postgres/pgdata` | Postgres-Cluster inkl. Data-Protection-Schlüsselring und verschlüsselten Secrets |
   | `./data/tls` | `web`: `/etc/nginx/tls` | self-signed Zertifikat ([ADR-0009](0009-https-only-self-signed.md)) |

   Neue persistente Daten (z. B. der Graph des `routing`-Service) bekommen ein weiteres Unterverzeichnis von `./data/`.
3. **Rechte ohne Handarbeit:** Der einmalige Service `data-init` (Image des `db`-Service, läuft als root und endet sofort) legt die Verzeichnisse an und übergibt `./data/postgres` an den Postgres-Benutzer des Images (uid/gid 1000, Modus 700). `db` startet erst nach seinem erfolgreichen Ende (`service_completed_successfully`).
4. **`./data/` gehört nicht ins Repo und nicht in Build-Kontexte:** Eintrag in `.gitignore` und in beiden `.dockerignore`-Dateien (der Postgres-Cluster ist für den Build-Benutzer nicht lesbar).
5. **Testpfad** ([ADR-0010](0010-tests-auf-dev-server-per-compose.md)): hält keine Daten; `test-db` läuft im `tmpfs`, Ergebnisse kommen per Bind-Mount nach `./artifacts`. Auch dort gibt es keine Volumes.
6. **Betrieb:** `docker compose down` (auch mit `-v`) lässt `./data/` unberührt. Zurücksetzen einer Umgebung heißt bewusst: Stack stoppen, `./data/` löschen, Stack starten – danach ist die Instanz leer und der Setup-Assistent (`/setup`) erscheint. Sicherung: `pg_dump` im laufenden Betrieb oder Kopie von `./data/` bei gestopptem Stack.

## Alternativen
- Benannte Volumes (bisheriger Stand `pgdata`, `tls`): von Docker verwaltet, aber außerhalb des Deployment-Verzeichnisses und anfällig für globale Aufräumbefehle.
- Datenpfad über eine `.env`-Variable konfigurierbar: zusätzliche Variable gegen das Ziel einer minimalen `.env` ([ADR-0006](0006-konfiguration-env-minimal-config-service.md)); wer die Daten woanders braucht, verschiebt das ganze Deployment-Verzeichnis oder legt `./data` als Symlink an.
- Rechte per manuellem `chown` dokumentieren: fehleranfällig bei jeder neuen Umgebung.

## Konsequenzen
- Ein Deployment-Verzeichnis (live `/etc/docker/compose/chargeglobe`) enthält Code, `.env` und Daten; Umzug = Stack stoppen, `.env` und `./data/postgres` kopieren (mit demselben Datenbank-Image, damit die TimescaleDB-Version passt), starten; `./data/tls` entsteht neu.
- Löschen von `./data/` vernichtet alle Daten der Instanz einschließlich der verschlüsselten Secrets im Config-Service; Deploy-Anweisungen nennen deshalb `./data/` statt Volumes als „nicht löschen“.
- Ein neuer Service mit persistenten Daten braucht ein Unterverzeichnis von `./data/` und, falls er nicht als root schreibt, einen Eintrag in `data-init`.
- Ein Test in `ChargeGlobe.Api.Tests` prüft beide Compose-Dateien und die Dockerfiles: kein Top-Level-`volumes:`, kein `VOLUME`, jeder Mount beginnt mit `./`.
