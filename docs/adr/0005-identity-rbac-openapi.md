# ADR-0005: Nutzerverwaltung mit RBAC und agentenfähige OpenAPI-API

**Status:** akzeptiert · **Datum:** 2026-10-13

## Kontext
Ohne Anmeldung soll nur die Karte nutzbar sein; alle erweiterten Funktionen und persönlichen Einstellungen (Customizings) stehen ausschließlich angemeldeten Nutzern zur Verfügung. Pflege (Tarife, Konflikte, Provider-Konfiguration) und Administration brauchen Identitäten und abgestufte Rechte. Neben Menschen sollen KI-Agenten die API eigenständig nutzen und Pflegevorschläge einreichen können; dafür muss der API-Vertrag maschinenlesbar und vollständig sein.

## Entscheidung
- **ASP.NET Core Identity** (EF Core, Postgres) für Nutzer und Rollen; **feingranulare Permissions** (`<ressource>:<aktion>`) als eigentliche Autorisierungseinheit. Rollen sind editierbare Berechtigungsbündel; Code prüft nur Permissions, nie Rollennamen.
- **Zwei Authentifizierungsschemata**, die in dieselben Permission-Claims münden: JWT (SPA-Login, Refresh-Token als HttpOnly-Cookie) und API-Keys (`X-Api-Key`, nur Hash gespeichert, Rechte ⊆ Rechte des Eigentümers, Ablauf, Rate-Limit-Tier).
- **OpenAPI 3.1** (eingebaute .NET-10-Generierung) ist der einzige API-Vertrag: operationId, Beschreibungen, Beispiele, Security Schemes, `x-required-permission` je Operation, Problem-Details-Fehler, Idempotency-Key für POST, `GET /me` zur Selbstauskunft, `GET /permissions` als Katalog.
- **Anonym ist nur die Karte**: Kartendaten (`GET /locations`, `GET /locations/{id}` mit Basisdaten und aktuellem Status, Karten-Kacheln, `GET /map/config`), dazu `GET /health`, `GET /permissions` und die Auth-Endpunkte. Alles andere verlangt ein Benutzerkonto oder einen API-Key mit passender Permission.
- **Selbstregistrierung** für jedermann: `POST /auth/register` → Bestätigungsmail → `POST /auth/confirm-email`; Passwort-Reset über `POST /auth/password/forgot` und `POST /auth/password/reset`. Mails laufen über den zentralen Mail-Service ([ADR-0008](0008-zentraler-mail-service-smtp.md)). Neue Konten erhalten die Rolle `User`; Login erst nach bestätigter Adresse.
- **Customizings je Nutzer**: Einstellungen (`GET/PUT /me/preferences`, JSON nach Schema: Standard-Kartenausschnitt, gespeicherte Filter, eigene Ladekarten) und Favoriten (`GET /me/favorites`, `PUT/DELETE /me/favorites/{locationId}`).
- Standardrollen: `Admin`, `Maintainer`, `Analyst`, `Agent`, `User`. `User` bündelt die Lese-Permissions der erweiterten Funktionen (`evse:read`, `price:read`, `route:read`) sowie `apikey:manage` für eigene Keys. Agenten erhalten standardmäßig nur Lese- und Vorschlagsrechte; Vorschläge werden von Maintainern freigegeben.
- Jede schreibende Aktion landet im Audit-Log mit Akteur (Nutzer oder Key).

## Alternativen
- Externer Identity-Provider (Keycloak, Auth0): zusätzlicher Betriebsaufwand; OIDC-Login kann später additiv über External Logins kommen.
- Reines Rollen-Modell ohne Permissions: zu grob, Rollen müssten bei jedem neuen Endpunkt im Code angefasst werden.
- Eigener MCP-Server als primäre Agenten-Schnittstelle: würde Logik duplizieren; stattdessen OpenAPI als Quelle, MCP später generiert.

## Konsequenzen
- Jeder neue Endpunkt muss Permission, operationId und Beschreibung deklarieren; ein Test prüft die OpenAPI-Datei auf Vollständigkeit dieser Felder.
- Angular bekommt Registrierung, Login, Passwort-Reset, Guard nach Permissions, eine Einstellungsseite und Admin-Seiten für Nutzer, Rollen, API-Keys. Anonyme Besucher sehen die Karte; erweiterte Funktionen sind sichtbar, führen aber zur Anmeldung.
- Die API ist weiterhin für jeden nutzbar, für mehr als die Kartendaten aber nur mit kostenlosem Konto bzw. API-Key; das erlaubt Rate-Limits und Missbrauchsschutz je Konto.
- Der Permission-Katalog lebt in Domain und wird beim Start mit der Datenbank abgeglichen (neue Permissions werden angelegt, `Admin` erhält sie automatisch).
