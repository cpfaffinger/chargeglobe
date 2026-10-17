# Mitwirken an chargeglobe

Danke für dein Interesse. chargeglobe ist eine offene Datenbank für öffentliche E-Ladepunkte (Inventar, Status-Historie, Preise) mit API und Web-Frontend. Dieses Dokument beschreibt, wie Beiträge ablaufen.

## Bevor du loslegst

- Lies [CLAUDE.md](CLAUDE.md) (Arbeitsregeln, Befehle) und die Dokumentation unter [docs/](docs/), vor allem [02 Architektur](docs/02-architektur.md), [03 Datenmodell](docs/03-datenmodell.md) und für neue Datenquellen [04 Provider-Interface](docs/04-provider-interface-acl.md) und [05 Crawling](docs/05-crawling-konzept.md).
- Größere Änderungen bitte zuerst als Issue beschreiben, damit wir Richtung und Umfang abstimmen.
- Mit [Verhaltenskodex](CODE_OF_CONDUCT.md): respektvoll, sachlich, offen für Rückfragen.

## Regeln für Code und Doku

- **Sprache:** Dokumentation und Diskussion auf Deutsch; Code, Bezeichner, Kommentare, Commit-Nachrichten und OpenAPI-Texte auf Englisch.
- **Code ist die Wahrheit:** Wer Code ändert, passt die betroffene Doku im selben Pull Request an. Dateien beschreiben nur den aktuellen Stand, keine Historie.
- **Begriffe:** Location, ChargingStation, EVSE, Connector; fachlicher Schlüssel ist die EVSE-ID.
- **Datenquellen:** Jeder Adapter übersetzt im Adapter (Anti-Corruption-Layer), hat Descriptor, Status-Mapping, Fixtures und Replay-Test, hält die Rate-Limits der Quelle ein und nutzt nur Endpunkte, die anonym erreichbar sind und deren Nutzungsbedingungen geprüft sind ([08](docs/08-datenquellen-und-recht.md)). Keine fremden API-Keys oder andere Secrets im Repository.
- **API:** Jeder Endpunkt hat `operationId`, `summary`, `description`, Tags, Beispiel-Antworten und deklariert seine Permission; Fehler als RFC-9457-Problem-Details ([06](docs/06-api.md)).
- **Tests:** 100 % Statement Coverage für .NET und Angular. Der maßgebliche Lauf ist `scripts/test.sh` über `docker-compose.test.yml` ([ADR-0010](docs/adr/0010-tests-auf-dev-server-per-compose.md)); lokal geht es mit `dotnet test` und `npx ng test` (siehe CLAUDE.md, „Befehle“).

## Ablauf

1. Fork und Branch von `master`: `feat/<kurzname>`, `fix/<kurzname>` oder `docs/<kurzname>`.
2. Änderung mit Tests und Doku; kleine, in sich geschlossene Commits mit englischer Nachricht im Imperativ.
3. `scripts/test.sh` muss grün sein, inklusive der Coverage-Schwellen.
4. Pull Request gegen `master` mit Beschreibung: was, warum, wie getestet, welche Doku angepasst wurde.

## Daten und Rechte

Daten aus Quellen bleiben unter den Bedingungen ihrer Quelle; die API nennt sie in `meta.attribution`. Trage keine Daten bei, deren Herkunft oder Weitergaberecht unklar ist. Für die Lizenz deines Beitrags gilt die Lizenz des Repositorys (Datei `LICENSE`).
