# Endpunkt-Notiz: Octopus Electroverse – App (Android, `energy.octopus.electricjuice.android`)

| | |
|---|---|
| Analysiert am | 2026-10-17 |
| Analysiert von | chargeglobe |
| Methode | Statische Analyse der APK (Version 2026.09.08, Kotlin/Compose, Apollo-GraphQL, Mapbox; stark obfuskiert, Klassen unter `defpackage`): Strings in `resources.arsc`, Karten-Quelle in `defpackage/pik.java`; Abruf-Versuche mit curl |
| Tier | 2 (inoffizieller App-Endpunkt) |
| Zugang | **Konto nötig** (Kartenkacheln verlangen eine Betreibergruppe aus dem Konto; GraphQL: 403 ohne App-/Token-Kontext) – **nicht anonym nachgestellt** |
| ToS geprüft | nein; Konten werden nicht angelegt (Regel: keine Registrierung/Anmeldung durch den Agenten) |
| Lizenz / Attribution | keine Angabe |
| Rate-Limit beobachtet | – |
| Capabilities | (laut App) StaticInventory · Roaming-Preise je Ladepunkt und Nutzerprofil · Kartenkacheln (MVT) – im Test **keine** Daten erhalten |

## Befund

| Zweck | URL | Beleg |
|---|---|---|
| GraphQL (Apollo, `application/graphql-response+json`, Multipart-Subscriptions) | `https://api.electroverse.com/graphql/` | `resources.arsc` |
| GraphQL-Subscriptions | `wss://api.electroverse.com/ws/graphql/` | `resources.arsc` |
| **Ladepunkt-Kacheln** | `https://api.electroverse.com/rest/locations/tiles/elastic/{z}/{x}/{y}?operator_groups[]=<Gruppe>` | `resources.arsc`; Zusammenbau in `defpackage/pik.java` (`j79Var.b.concat("{z}/{x}/{y}")` plus Query `operator_groups[]=`), Quelle `CHARGE_POINT_SOURCE_ID` im Mapbox-Stil |
| Rabatt-Kacheln | `https://api.electroverse.com/rest/mvt/discounts/` | `resources.arsc` |
| Ländergrenzen | `https://electroverse.com/geojson/countries.json` | `resources.arsc` |

Die Karte der App ist eine **Mapbox-Vektorkarte**: Die Ladepunkte kommen als Mapbox Vector Tiles (MVT) aus einem Elasticsearch-gestützten Dienst. Details zu einem Ladepunkt, Preise und Verfügbarkeit laufen über GraphQL (Apollo; Operationen sind als kompilierte Klassen vorhanden, nicht ausgewertet).

## Nachstellungsversuch (2026-10-17)

| Aufruf | Ergebnis |
|---|---|
| `GET …/tiles/elastic/12/2210/1446` (Kachel Klagenfurt) | 500 (HTML-Fehlerseite, Parameter `operator_groups` fehlt) |
| `GET …/tiles/elastic/12/2210/1446?operator_groups[]=x` | 400 `operator_groups` (Wert muss eine existierende Gruppe sein) |
| `POST …/graphql/` mit `{"query":"{ __typename }"}` (mit und ohne `okhttp`-User-Agent) | 403 Forbidden |

Der Wert für `operator_groups[]` (`str` im Konstruktor von `pik`) wird aus dem Zustand der angemeldeten Sitzung gebildet (Betreibergruppen des Kontos bzw. der Region); eine allgemeine Gruppe zum Raten gibt es nach dem Befund nicht. Ein Weitermachen hätte Kontoanlage, Anmeldung oder das Erraten interner Gruppen-IDs erfordert; beides wurde nicht gemacht.

## Status- und Stecker-Mapping, Preise
Nicht ableitbar ohne Antwortdaten. Electroverse zeigt laut `docs/07` Roaming-Preise vieler österreichischer Betreiber; diese sind im GraphQL-Schema zu erwarten, aber nur mit Konto erreichbar.

## Replay
Kein Skript (kein anonymer Abruf möglich).

## Offene Punkte / Risiken
- **Nächster Schritt, wenn Electroverse als Preisquelle interessant bleibt:** Mit Zustimmung des Betreibers (eigenes Electroverse-Konto) den Datenverkehr der App auf eigenem Gerät mitschneiden (`docs/05`, App-Analyse-Methodik): Wert von `operator_groups[]`, GraphQL-Operationen für Standortdetails und Preise, Rate-Limits. Das Konto bleibt beim Betreiber, Zugangsdaten nie im Repo.
- Die Webkarte `electroverse.com/map` (in `docs/07` als P1 geführt) nutzt vermutlich dieselbe API; ihre Netzwerkaufrufe sind im Browser sichtbar und dort zu prüfen, statt den App-Weg zu gehen.
- Die Nutzungsbedingungen (`electroverse.com/legal/terms`) vor jedem automatischen Abruf lesen; Roaming-Preise sind Geschäftsdaten von Octopus und ihren Partnern.
