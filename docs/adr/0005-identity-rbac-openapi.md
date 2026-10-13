# ADR-0005: Nutzerverwaltung mit RBAC und agentenfähige OpenAPI-API

**Status:** akzeptiert · **Datum:** 2026-10-13

## Kontext
Die Daten sollen anonym lesbar bleiben, aber Pflege (Tarife, Konflikte, Provider-Konfiguration) und Administration brauchen Identitäten und abgestufte Rechte. Neben Menschen sollen KI-Agenten die API eigenständig nutzen und Pflegevorschläge einreichen können; dafür muss der API-Vertrag maschinenlesbar und vollständig sein.

## Entscheidung
- **ASP.NET Core Identity** (EF Core, Postgres) für Nutzer und Rollen; **feingranulare Permissions** (`<ressource>:<aktion>`) als eigentliche Autorisierungseinheit. Rollen sind editierbare Berechtigungsbündel; Code prüft nur Permissions, nie Rollennamen.
- **Zwei Authentifizierungsschemata**, die in dieselben Permission-Claims münden: JWT (SPA-Login, Refresh-Token als HttpOnly-Cookie) und API-Keys (`X-Api-Key`, nur Hash gespeichert, Rechte ⊆ Rechte des Eigentümers, Ablauf, Rate-Limit-Tier).
- **OpenAPI 3.1** (eingebaute .NET-10-Generierung) ist der einzige API-Vertrag: operationId, Beschreibungen, Beispiele, Security Schemes, `x-required-permission` je Operation, Problem-Details-Fehler, Idempotency-Key für POST, `GET /me` zur Selbstauskunft, `GET /permissions` als Katalog.
- Standardrollen: `Admin`, `Maintainer`, `Analyst`, `Agent`, `Viewer`. Agenten erhalten standardmäßig nur Lese- und Vorschlagsrechte; Vorschläge werden von Maintainern freigegeben.
- Jede schreibende Aktion landet im Audit-Log mit Akteur (Nutzer oder Key).

## Alternativen
- Externer Identity-Provider (Keycloak, Auth0): zusätzlicher Betriebsaufwand; OIDC-Login kann später additiv über External Logins kommen.
- Reines Rollen-Modell ohne Permissions: zu grob, Rollen müssten bei jedem neuen Endpunkt im Code angefasst werden.
- Eigener MCP-Server als primäre Agenten-Schnittstelle: würde Logik duplizieren; stattdessen OpenAPI als Quelle, MCP später generiert.

## Konsequenzen
- Jeder neue Endpunkt muss Permission, operationId und Beschreibung deklarieren; ein Test prüft die OpenAPI-Datei auf Vollständigkeit dieser Felder.
- Angular bekommt Login, Guard nach Permissions und Admin-Seiten für Nutzer, Rollen, API-Keys.
- Der Permission-Katalog lebt in Domain und wird beim Start mit der Datenbank abgeglichen (neue Permissions werden angelegt, `Admin` erhält sie automatisch).
