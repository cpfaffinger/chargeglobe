# Endpunkt-Notiz: IONITY – App (Android, `com.cleevio.ionity.android.app`)

| | |
|---|---|
| Analysiert am | 2026-10-17 |
| Analysiert von | chargeglobe |
| Methode | Statische Analyse der APK (Version 2.500.1, Kotlin, Retrofit/OkHttp, Cognito/Amplify für den Login): `BaseUrl`-Klasse, Retrofit-Interface `LocationsApiService`, OkHttp-Interceptor für die Pflicht-Header; Abruf mit curl |
| Tier | 2 (inoffizieller App-Endpunkt, „Ad-hoc-BFF“ der App) |
| Zugang | **anonym**, aber zwei feste Pflicht-Header (Plattform + App-Version) |
| ToS geprüft | nein, **offen** |
| Lizenz / Attribution | keine Angabe |
| Rate-Limit beobachtet | keins ausgelöst (wenige Aufrufe) |
| Capabilities | StaticInventory (nur IONITY-Standorte) · AdHocPrice (je Stecker, kWh) · LiveStatusPerConnector · ByLocationId · BulkAll (Standortliste) – **keine EVSE-IDs** im Standortabruf |

## Befund

Die App spricht ein **BFF für Ad-hoc-Laden**: `https://adhoc-bff.ionity.cloud/api/` (CDN `https://cdn.ionity.cloud/app/`, Feature-Flags unter `/featurehub`). Sie zeigt ausschließlich IONITY-Standorte (`cpoIdentifier: IONITY_CPO`). Der Server prüft zwei Header, die der Interceptor `y0d` jedem Aufruf mitgibt:

| Header | Wert (App 2.500.1) |
|---|---|
| `x-adhoc-platform` | `ANDROID_V2` |
| `x-adhoc-app-feature-version` | `v2.500.1` |

Ohne sie: 400 `{"message":["Platform or App Feature Version missing! Please update your app."],"code":"1000001"}`. Der Server kann alte App-Versionen sperren, der Wert muss dann nachgezogen werden.

## Endpunkte

Retrofit-Interface `LocationsApiService` (Basis `https://adhoc-bff.ionity.cloud/api/`):

| Methode, Pfad | Parameter | Zugang |
|---|---|---|
| `GET v1/location/static` | optional `cpos` | **anonym (getestet)** |
| `GET v3/location/{uuid}` | – | **anonym (getestet)** |
| `GET v2/location/dynamic`, `GET v1/location/dynamic` | `latitude`, `longitude`, `radius` (m), `cpos` | verlangt zusätzlich Header `x-adhoc-user-email` (400 „Required header … not present“) – Nutzerbezug, **nicht getestet** |
| `POST v1/location/by-ids` | Body `LocationByIdsRequest` | nicht getestet |
| `GET v1/evse/uuids-by-evse-id?evseid=…` | EVSE-ID | nicht getestet (Brücke EVSE-ID → interne UUID) |

Weitere Pfade der App (Session, Zahlung, Profil, `oauth/token`) setzen ein Konto voraus und wurden nicht aufgerufen.

### `GET https://adhoc-bff.ionity.cloud/api/v1/location/static`
- Antwort: 200, 175 kB, **902 Standorte** (ganz Europa; Österreich und Grenzraum 77 im Rechteck 46–49 N / 9,5–17 E).
```json
{ "locations": [ { "uuid": "0ee9563c-deb0-46d2-b5fd-2241c7e166d6", "name": "IONITY Kaiserwald",
    "longitude": "15.382045", "latitude": "46.959244", "cpoIdentifier": "IONITY_CPO",
    "locationId": "…", "imageUrl": null } ] }
```
- Koordinaten sind **Strings**. Ein Bounding-Box-Filter fehlt serverseitig; der Client filtert lokal (Liste ist klein, ein Abruf genügt).

### `GET https://adhoc-bff.ionity.cloud/api/v3/location/{uuid}`
- Antwort (Kaiserwald, Dobl bei Graz, gekürzt):
```json
{ "uuid": "0ee9563c-…", "name": "IONITY Kaiserwald", "address": "Neue Welt 98", "postalCode": "8143",
  "city": "Dobl", "country": "AT", "latitude": "46.959244", "longitude": "15.382045",
  "connectors": [ { "uuid": "8a00ac97-…", "number": 1, "physicalReference": "01", "maxPower": 350000,
      "type": "CCS", "status": "OCCUPIED", "stateOfCharge": 37.0,
      "adhocPrice": { "name": "IONITY DIRECT", "unit": "kWh", "amount": "0.75", "currency": "EUR" },
      "chargingPrice": { "name": "N/A", "amount": "0.0" }, "blockingFee": null,
      "discountedChargingPrice": null, "lowestChargingPrice": null, "discount": null } ],
  "preAuthAmountForCharging": { "amount": "40.00", "currency": "EUR" }, "calculatedBlockingFee": null }
```
- Mapping auf Ingest-Modell:
  - `connectors[].uuid` → Ladepunkt-Schlüssel der Quelle (**keine EVSE-ID**; Zuordnung über `v1/evse/uuids-by-evse-id` oder über Standort + `physicalReference`, siehe Offene Punkte)
  - `maxPower` (Watt!) → kW = Wert / 1000
  - `adhocPrice.amount` (String, EUR/kWh) → Ad-hoc-Preis (`unit: kWh`), Tarifname `IONITY DIRECT`
  - `status` → Status (Tabelle unten); `stateOfCharge` ist der Ladestand des angeschlossenen Autos (Datenschutz-sensibel, nicht übernehmen)

## Status-Mapping

| Rohwert | OCPI-Status |
|---|---|
| `AVAILABLE` | `AVAILABLE` |
| `OCCUPIED` | `CHARGING` |

Nur diese zwei Werte gesehen; weitere (Störung, offline) wurden in den Stichproben nicht beobachtet.

## Stecker-Mapping

| Rohwert | ConnectorStandard |
|---|---|
| `CCS` | `IEC_62196_T2_COMBO` |

## Preisdarstellung
Je Stecker, wie die App dem Ad-hoc-Kunden anzeigt: `adhocPrice` (kWh-Preis, Tarif „IONITY DIRECT“; Langkampfen 0,69, Kaiserwald 0,75 EUR/kWh). `chargingPrice` ist im anonymen Abruf ein Platzhalter (`N/A`, 0.0); `discountedChargingPrice`, `lowestChargingPrice` und `discount` (Passport-/Abo-Preise) bleiben ohne Konto `null`. Blockiergebühr: im Test `null`. Die Preise sind **standortabhängig** (0,69 vs. 0,75), daher je Standort zu holen, nicht netzweit anzunehmen.

## Replay
`docs/endpunkte/replay/ionity.sh` (anonym): Liste laden, Ausschnitt filtern, erstes Ergebnis mit Details und Preisen ausgeben.

## Offene Punkte / Risiken
- **Wert für chargeglobe:** Ad-hoc-Preis je IONITY-Standort ohne Login; je österreichischem Standort ein Aufruf pro Lauf (nicht ausgezählt: das Rechteck 46–49 N / 9,5–17 E enthält 77 Standorte inkl. Bayern, Slowenien und Tschechien), Preisänderungen sind selten (täglich reicht). Status je Stecker kommt als Nebenprodukt.
- Keine EVSE-IDs: Die Zuordnung zu `AT*ION*E…`-Ladepunkten aus der E-Control läuft über Koordinaten + Stecker-Nummer oder über `v1/evse/uuids-by-evse-id` (Richtung EVSE-ID → UUID; ob der Aufruf anonym geht, ist ungeprüft).
- Pflicht-Header mit App-Version: Versionssperre möglich; Wert aus aktuellem APK nachführen.
- ToS prüfen (Ionity-AGB/App-Nutzungsbedingungen); `x-adhoc-user-email` macht deutlich, dass `dynamic` für angemeldete Nutzer gedacht ist, daher nicht mit Platzhalter-Adresse umgehen.
