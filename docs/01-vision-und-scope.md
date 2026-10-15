# 01 – Vision & Scope

## Ziel

Eine **offene, quellenübergreifende Datenbank aller öffentlichen E-Ladepunkte**, die

1. **Inventar** führt: Standort, Betreiber, Ladepunkte, Stecker, Leistung – mit vollständiger **Provenienz** (aus welcher Quelle stammt welches Attribut, wann wurde es zuletzt bestätigt),
2. **Belegung** zyklisch abfragt und **je Ladepunkt** eine Statushistorie führt (nicht nur je Ladesäule),
3. **Preise je Ladepunkt** über verschiedene Apps/Ladekarten (EMPs) und Tarifmodelle hinweg trackt und vergleichbar macht,
4. all das über eine **öffentliche API** und ein **Web-Frontend mit Karte** bereitstellt,
5. über ein **generisches Provider-Interface mit Anti-Corruption-Layer** beliebige weitere Quellen aufnehmen kann,
6. für **Menschen und KI-Agenten gleichermaßen nutzbar** ist: eine vollständig per OpenAPI 3.1 beschriebene API, Nutzerverwaltung mit rollenbasierten Berechtigungen (RBAC) und API-Keys mit denselben Berechtigungen.

## Anforderungen an Web-App und Betrieb

| Nr. | Anforderung | Umsetzung |
|---|---|---|
| R1 | **Kartenansicht** aller Ladepunkte ohne nutzungsabhängige Kosten und ohne Lizenzrisiko (kein Google Maps) | MapLibre GL JS + OpenStreetMap-Vektorkacheln (OpenFreeMap, bei Bedarf selbst gehostet) – [ADR-0007](adr/0007-karte-standort-routing.md) |
| R2 | **Eigener Standort** des Betrachters auf der Karte | Browser-Geolocation, nur im Browser verarbeitet, nicht gespeichert – [ADR-0007](adr/0007-karte-standort-routing.md) |
| R3 | **Routenfindung** in der Karte auf Straßendaten | selbst gehostete OSM-Routing-Engine (Valhalla) hinter `POST /routes` – [ADR-0007](adr/0007-karte-standort-routing.md) |
| R4 | **Benutzerkonten**: ohne Anmeldung nur die Karte; alle erweiterten Funktionen und Customizings nur für angemeldete Nutzer | Selbstregistrierung, Rolle `User`, Permission-Modell – [ADR-0005](adr/0005-identity-rbac-openapi.md) |
| R5 | **Zentraler Mail-Service** mit konfigurierbarem SMTP-Server (Host, Port, Benutzer, `plain`/`starttls`/`ssl`, Absenderadresse) | Outbox + SMTP-Versand, Konfiguration im Config-Service – [ADR-0008](adr/0008-zentraler-mail-service-smtp.md) |
| R6 | **HTTPS-only** am App-Port mit dauerhaft self-signed Zertifikat; gültiges Zertifikat liefert der externe Loadbalancer | nginx im `web`-Container – [ADR-0009](adr/0009-https-only-self-signed.md) |

**Abgrenzung anonym / angemeldet (R4):**

| | anonym | angemeldet (Rolle `User`) |
|---|---|---|
| Karte mit Ladepunkten, Basisdaten (Betreiber, Stecker, Leistung), aktueller Status | ja | ja |
| eigener Standort auf der Karte | ja | ja |
| Belegungshistorie, Auslastungsprofil, Preisvergleich über EMPs, Provenienz | nein | ja |
| Routenfindung | nein | ja |
| Customizings: Favoriten, gespeicherte Filter, Standard-Kartenausschnitt, eigene Ladekarten für den Preisvergleich | nein | ja |
| API-Keys für Skripte und Agenten | nein | ja |

## Nicht-Ziele (vorerst)

- Kein Starten/Stoppen von Ladevorgängen, keine Abrechnung, kein eigener EMP-Betrieb.
- Keine Ladeplanung nach Fahrzeugmodell (Ladekurven, Temperatur, Steigung); die Karte empfiehlt Ladestopps nur aus Verbrauch, Akkukapazität und Ladezustand. Routenfindung selbst ist Teil der App (R3).
- Keine Benachrichtigungen (Alerts bei frei werdendem Ladepunkt oder Preisänderung) im MVP.

## Begriffe

Die deutsche Alltagssprache („Ladesäule“) und die Standards (OCPI, OICP, ISO 15118) weichen voneinander ab. chargeglobe verwendet intern konsequent die Standard-Begriffe:

| Begriff (intern) | Deutsch / Alltag | Bedeutung | ID |
|---|---|---|---|
| **Location** | Ladestandort, Ladepark | Adresse/Koordinate mit 1..n Ladesäulen | intern (UUID) + Quell-IDs |
| **ChargingStation** (optional) | Ladesäule, Wallbox | physisches Gerät mit 1..n Ladepunkten | intern; manche Quellen liefern diese Ebene gar nicht |
| **EVSE** | **Ladepunkt** | das, was gleichzeitig genau *ein* Fahrzeug laden kann; Status-Träger | **EVSE-ID** (z. B. `AT*EVN*E1234*5`) |
| **Connector** | Stecker | Typ (Type 2, CCS, CHAdeMO, Schuko), max. Leistung; mehrere Stecker an einem EVSE teilen sich die Belegung | Nummer innerhalb des EVSE |

**Zur „Netzpunkt-ID“:** Die Anforderung lautet, dass es je Ladesäule eine über alle Apps hinweg wiederfindbare Kennung gibt. In Österreich und im europäischen Roaming ist das die **EVSE-ID** (ISO 15118 / DIN SPEC 91286-Format `<Land>*<Betreiber-ID>*E<Ladepunkt-ID>`), die jedem Ladepunkt zugeordnet ist und von CPOs, Roaming-Plattformen (Hubject, Gireve, e-clearing) und den meisten Apps angezeigt wird. Sie ist der **Primärschlüssel für Identität** in chargeglobe (siehe [ADR-0001](adr/0001-evse-id-als-identitaet.md)). Begriffe wie „Netzpunkt“ oder „Zählpunkt“ (die 33-stellige Zählpunktnummer des Netzbetreibers) sind zwar ebenfalls eindeutig, aber in Apps nicht sichtbar und werden daher nur als *optionales Attribut* geführt, falls eine Quelle sie liefert. Details zur ID-Vergabe in Österreich in [08 – Datenquellen & Recht](08-datenquellen-und-recht.md).

## Regionen

Der Scope wird nicht hart codiert, sondern über eine `region`-Tabelle mit PostGIS-Polygonen (Verwaltungsgrenzen aus OSM / data.gv.at) gesteuert. Crawler werden je Region und Provider aktiviert.

| Phase | Regionen |
|---|---|
| 1 | Wien, Niederösterreich, Oberösterreich, Burgenland |
| 2 | restliche Bundesländer (Stmk, Ktn, Sbg, Tirol, Vbg) |
| 3 | Deutschland, Schweiz, Liechtenstein |
| 4 | Italien (Südtirol zuerst) |

Die Provider-Abstraktion ist von Anfang an länderfähig: EVSE-ID-Präfixe (`AT`, `DE`, `CH`, `IT`), Länder-Registries und nationale Datenquellen (NAPs) sind Konfiguration, nicht Code.

## Qualitätsziele

| Ziel | Messgröße |
|---|---|
| Abdeckung | Anteil der im E-Control-Verzeichnis geführten Ladepunkte der Scope-Regionen, die in chargeglobe vorhanden sind (Ziel ≥ 99 %) |
| Statusfrische | Median-Alter der letzten Statusbeobachtung je Ladepunkt (Ziel ≤ 5 min für DC, ≤ 10 min für AC) |
| Preisabdeckung | Anteil der Ladepunkte mit mindestens Ad-hoc-Preis + 3 EMP-Tarifen |
| Quellen-Robustheit | Ausfall eines Providers degradiert nur dessen Attribute, nie das Inventar |
