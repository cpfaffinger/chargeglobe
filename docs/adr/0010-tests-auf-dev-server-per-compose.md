# ADR-0010: Tests laufen auf dem Dev-Server über einen eigenen Compose-Startpfad

**Status:** akzeptiert · **Datum:** 2026-10-13

## Kontext
Auf dem Entwicklerrechner ist Docker nur über WSL verfügbar, das .NET-SDK nur pro Nutzer installiert und kein Chrome vorhanden. Testläufe hängen damit an rechnerspezifischen Umwegen. Der Dev-Server `srv-dev2` hat Docker, genügend Ressourcen und ist ohnehin das Deploy-Ziel. Eine GitHub-CI ist nicht im Scope.

## Entscheidung
1. **Tests werden nach Möglichkeit auf `srv-dev2` ausgeführt, nicht lokal.** Die lokale WSL-/Docker-Umgebung ist keine Voraussetzung mehr; lokale Läufe bleiben erlaubt, sind aber nicht maßgeblich. Maßgeblich für die Abnahme vor einem Merge ist der Lauf auf dem Server.
2. **Der Testlauf ist ein eigener Startpfad des Compose-Setups:** `docker-compose.test.yml` im Repo-Root mit eigenem Projektnamen `chargeglobe-test`. Statt der App starten dort Testcontainer:
   - `test-db`: `timescale/timescaledb-ha:pg17` ohne Volume (tmpfs), für Integrationstests,
   - `test-smtp`: `axllent/mailpit`, nimmt jede Mail im Arbeitsspeicher an; die Integrationstests senden per SMTP dorthin und lesen die zugestellte Mail über dessen HTTP-API (Adressen in `TestSmtp__Host`, `TestSmtp__Port`, `TestSmtp__ApiUrl`),
   - `test-dotnet` (`tests/Dockerfile.dotnet`, Basis `mcr.microsoft.com/dotnet/sdk:10.0`): führt `dotnet test --collect:"XPlat Code Coverage"` und ReportGenerator aus und endet mit Exitcode ≠ 0, wenn ein Test fehlschlägt oder die Line Coverage unter 100 % liegt; nicht abgedeckte Zeilen werden als `Datei:Zeile` ausgegeben,
   - `test-web` (`src/web/Dockerfile.test`, Basis `node:24` mit Chromium): führt `ng test --code-coverage --watch=false` mit dem Karma-Launcher `ChromeHeadlessNoSandbox` aus, baut danach das Produktions-Bundle (`ng build --configuration production`) und endet mit Exitcode ≠ 0 bei Testfehlschlag, Statement Coverage unter 100 % oder fehlgeschlagenem Build.

   Der Quellcode wird beim Build in die Test-Images kopiert (kein Bind-Mount des Checkouts); Ergebnisse (`Summary.txt`, Coverage-Berichte) landen über ein Bind-Mount in `./artifacts/`. Es werden keine Ports veröffentlicht.
3. **Ein Aufruf, ein Ergebnis:** `scripts/test.sh` startet beide Testservices nacheinander, gibt die Coverage-Zusammenfassungen aus, räumt auf und liefert einen Gesamt-Exitcode. Integrationstests sprechen `test-db` über eine Verbindungszeichenfolge aus der Umgebung an (`ConnectionStrings__TestDb`; kein Docker-in-Docker, kein Docker-Socket im Container); jede Test-Fixture legt dort ihre eigene Datenbank an und löscht sie wieder.
4. **Getrennt von der laufenden App:** Der zu testende Branch wird nach `origin` gepusht und auf dem Server in ein eigenes Verzeichnis `/opt/chargeglobe-test` ausgecheckt. `/opt/chargeglobe` (laufender Stack, `.env`, Volumes `pgdata` und `tls`) wird vom Testlauf nicht berührt.
5. **Aufräumen nach jedem Lauf ist erlaubt und vorgesehen:** `docker compose -f docker-compose.test.yml down -v --rmi local --remove-orphans` und Löschen von `/opt/chargeglobe-test`. Bereinigt werden ausschließlich Ressourcen des Projekts `chargeglobe-test`; globale Befehle wie `docker system prune` oder `docker volume prune` sind tabu, weil auf dem Host fremde Stacks (`companyhero`) denselben Docker-Daemon nutzen.

## Alternativen
- Lokale Ausführung als Standard: hängt an WSL, PATH-Sonderfällen und einem Ersatzbrowser; Ergebnisse sind nicht reproduzierbar zwischen Rechnern.
- GitHub Actions: derzeit ausdrücklich nicht im Scope.
- Profil `test` in `docker-compose.yml`: teilte Projektname, Netz und Volumes mit der laufenden App; ein Aufräumen könnte Produktivdaten des Dev-Stacks treffen.
- Testcontainers mit Docker-Socket im Testcontainer: gäbe dem Testlauf Zugriff auf alle Container des Hosts, auch die der fremden App.

## Konsequenzen
- Feature-Branches werden vor dem Merge nach `origin` gepusht und nach dem Merge dort wieder gelöscht.
- Die Test-Images enthalten SDK, Node und Chromium und sind groß; sie werden nach dem Lauf entfernt, der Build-Cache des Daemons bleibt erhalten.
- Tests dürfen keine rechnerspezifischen Annahmen treffen (Pfade, installierte Browser); alles Nötige steht in den Test-Images.
- `CLAUDE.md` (Workflow je Prompt, Befehle) nennt den Server-Testlauf als Abnahmeweg.
