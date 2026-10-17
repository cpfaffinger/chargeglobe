# Endpunkt-Notiz: ÖAMTC ePower – App (Android, `at.oeamtc.epower`)

| | |
|---|---|
| Analysiert am | 2026-10-17 |
| Analysiert von | chargeglobe |
| Methode | Statische Analyse der APK (Version 1.0, Kotlin/Retrofit/Moshi, Karte über ADAC-Navigationskomponente und Mapbox): `assets/app_config.json`, Bibliothek `at.oeamtc.smr` (Smart Routing, Version 0.36.3) mit `BuildConfig`, `SmartRoutingKt`, Retrofit-Interfaces `PoiService`, `EpwrChargingService`; Abruf mit curl |
| Tier | 2 (inoffizieller App-Endpunkt; **fest eingebauter Basic-Client der App**) |
| Zugang | POI-Suche: statischer Basic-Client der App (`AuthenticationHeaders.BASIC_AUTH`), kein Konto nötig. Verfügbarkeit/Sessions (`epwr-charging/*`): OAuth-Token eines angemeldeten Kontos |
| ToS geprüft | nein, **offen** (`oeamtc.at/apps/nutzungsbedingungen/oeamtcapp`); wie bei V-Go ein geteilter App-Credential, Klärung mit dem ÖAMTC nötig |
| Lizenz / Attribution | keine Angabe |
| Rate-Limit beobachtet | keins ausgelöst (5 Aufrufe) |
| Capabilities | StaticInventory · EvseIds · AdHocPrice (kWh/Minute, je Station) · BulkByBoundingBox/Kreis/Korridor (Paging) – **kein Live-Status ohne Konto** (`availability: UNKNOWN`) |

## Befund

Die App ist eine Hülle um das ÖAMTC-Portal: Menüpunkte in `assets/app_config.json` sind Web-Seiten (`www.oeamtc.at/meine-services/app/epower/…`), die Karte („Routenplaner“) ist die native Bibliothek `at.oeamtc.smr`. Diese ruft ein einheitliches **GIS-Backend** des ÖAMTC auf:

- Basis: `https://www.oeamtc.at` (Produktion; Test: `webint.oeamtc.at`, `beta.oeamtc.at`, `webtestlbgw.oeamtc.at`) plus `BASE_PATH = /api/mob/oeamtc/` (Konstante in `at.oeamtc.smr.BuildConfig`).
- Je POI-Kategorie ein Dienst `gis-<kategorie>/<kategorie>/search` (POST) und `…/detail/{id}` (GET): `gis-efuel` (E-Ladestationen), `gis-fuel`, `gis-parking`, `gis-camping`, `gis-mobility`, `gis-traffic` u. a., dazu `gis-location/location/search|nearby`.
- Header `AUTH_TYPE: BASIC_AUTH` ist nur ein Marker für den App-Interceptor, der daraus `Authorization: Basic …` macht. Der Wert steht fest in `at.oeamtc.android.smartrouting.SmartRoutingKt` (`UrlConfiguration(…, mapOf("Authorization", "Basic …"))`); Benutzername `mob`. Das Replay-Skript erwartet den kompletten Header-Wert in `OEAMTC_BASIC`.
- Das Routing/Navigieren läuft über einen ADAC-Dienst (`https://geoservices-a.junghans-schneider.de`, Kartenstil und Navigation; `adaconlineapim.adac.de/fcd` für Verkehrsdaten); beides ist für die Ladepunktdaten nicht relevant.

## Endpunkte

### `POST https://www.oeamtc.at/api/mob/oeamtc/gis-efuel/efuel/search`
- Query: `count` (Seitengröße, 100 getestet), `start` (Offset); Standard sind 10 Treffer.
- Header: `Authorization: Basic …`, `Content-Type: application/json`.
- Body (`EFuelPoiFilter`), geografische Auswahl wahlweise als Box, Kreis oder Korridor:
```json
{ "geographic": { "boundingBox": { "longitudeMin": 15.40, "longitudeMax": 15.47, "latitudeMin": 47.05, "latitudeMax": 47.09 } } }
```
  `"circle": {"centerLongitude","centerLatitude","radius"}` bzw. `"corridor"` sind die Alternativen. Weitere Filter im Modell: `efuels` (`plugTypes`, `capacitiesKw`, `oeamtcEPowerStation`, `greenEnergy` …), `operators`, `operatorType`, `openingHours`, `favoriteOnly`, `ids`.
- Antwort: Ausschnitt Graz, 200, 255 kB für 100 Stationen, `header.totalCount` = **133** (Paging mit `start=100`). Kopf: `{"header":{"etag":"…","start":0,"count":100,"totalCount":133},"results":[…]}`. Eine Station (gekürzt, ÖAMTC-eigene Station Klagenfurt):
```json
{ "header": { "lastImported": "2026-10-17T06:19:11.405Z", "etag": "\"22b0…\"" },
  "data": {
    "header": { "id": "120_E3MB16ZZ7ET177XTDKSJ1SYHJ", "lastUpdated": "2026-10-17T06:19:11.405Z",
                "categories": [ "EFUEL" ], "names": [ { "type": "NAME", "value": "ÖAMTC Klagenfurt - DC" } ],
                "oeamtcEPowerStation": true, "chargePointOperator": "ÖAMTC", "eMobilityProvider": [ "ÖAMTC" ] },
    "addresses": [ { "type": "LOCATION", "street": "Alois-Schader-Straße", "houseNumber": "11", "postalCode": "9020",
                    "city": "Klagenfurt am Wörthersee", "country": "AUT", "longitude": 14.323832, "latitude": 46.627107 } ],
    "openings": [ { "openingHours": { "monday": [ { "from": "07:00", "to": "18:00" } ] } } ],
    "facilities": { "authenticationOptions": [ { "type": "APP", "isAvailable": true } ],
                    "paymentOptions": [ { "type": "BANKOMAT", "isAvailable": true } ] },
    "operator": { "companyName": "ÖAMTC" },
    "prices": [ { "prices": [ { "unit": "PER_KWH", "price": 0.54 }, { "unit": "PER_MINUTE", "price": 0.0 } ] } ],
    "chargingPoints": [ { "id": "AT*120*E0000615", "capacityKw": 100.0, "cost": "FREE", "plugs": [ "CCS_TYP2" ], "availability": "UNKNOWN" } ],
    "greenEnergy": true, "freeParking": false, "stationActive": true, "priceUrl": null } }
```
- Mapping auf Ingest-Modell:
  - `chargingPoints[].id` → `ProviderEvseSnapshot.EvseId` (mit `*`, normalisieren); `data.header.id` → Stationsschlüssel (UUID oder `120_…`)
  - `addresses[0]` → Adresse/Koordinaten; `header.chargePointOperator` → Betreiber (Klartext, keine Betreiber-ID)
  - `capacityKw` → Leistung; `plugs[]` → Stecker; `prices[].prices[]` → Ad-hoc-Preis der **Station** (nicht je Ladepunkt)
  - `cost` (`FREE`/`PAID`/`UNKNOWN`) → Kostenkennzeichen je Ladepunkt; `availability` → Status (siehe unten)

### `GET …/gis-efuel/efuel/detail/{id}`
Einzelstation, gleiche Struktur mit Details; nicht aufgerufen.

### Mit Konto (OAuth, nicht aufgerufen)
`POST …/epwr-charging/charge-points/search-availabilities` (Body `EpwrFilter`), `GET …/epwr-charging/charge-points/{evseId}`, `GET …/epwr-charging/charging-sessions/active`. Das sind die Live-Verfügbarkeiten; die anonyme POI-Suche liefert `availability: UNKNOWN`.

## Status-Mapping

| Rohwert (`availability`) | OCPI-Status |
|---|---|
| `UNKNOWN` | `UNKNOWN` |

Im Grazer Test waren **alle 336 Ladepunkte `UNKNOWN`**. Weitere Werte (vermutlich `AVAILABLE`/`OCCUPIED`/`OUT_OF_ORDER`) gibt es erst über den OAuth-Dienst; sie wurden nicht beobachtet und daher nicht gemappt.

## Stecker-Mapping

| Rohwert | ConnectorStandard |
|---|---|
| `TYP2` | `IEC_62196_T2` |
| `CCS_TYP2` | `IEC_62196_T2_COMBO` |
| `CHA_DE_MO` | `CHADEMO` |
| `SCHUKO_PLUG` | `DOMESTIC_F` |

(Graz: 253 / 67 / 15 / 1.)

## Preisdarstellung
Je Station eine Liste von Preisen mit `unit` (`PER_KWH`, `PER_MINUTE`) und `price` in EUR; im Graz-Test hatten 77 von 100 Stationen einen Preis (z. B. 0,44 EUR/kWh, 0,00 EUR/min bei ÖAMTC-eigenen Stationen), 23 keinen. Ob der Preis der ÖAMTC-Mitglieds-/Roamingpreis oder der Ad-hoc-Preis des Betreibers ist, ist aus dem Feld nicht ersichtlich (Auffällig: die ÖAMTC-eigene Station Klagenfurt steht bei 0,54, Graz bei 0,44.) `priceUrl` war im Test `null`. `cost: FREE` markiert kostenlose Ladepunkte (156 von 336 gezählten), `UNKNOWN` (93) bedeutet kein Preis hinterlegt.

## Replay
`OEAMTC_BASIC="Basic …" docs/endpunkte/replay/oeamtc-epower.sh` (Bounding Box, Seitengröße und Offset über `COUNT` und `START`).

## Offene Punkte / Risiken
- **Wert für chargeglobe:** Stammdaten und Preise für den ÖAMTC-Bestand und viele Partner (Ausschnitt Graz: Energie Graz, Energie Steiermark, da emobil, ENIO …), mit `lastUpdated` je Station; für Live-Status taugt die anonyme POI-Suche nicht.
- Der Basic-Client ist ein von allen App-Installationen geteilter Zugang. Wie bei V-Go: vor einem Adapter Erlaubnis des ÖAMTC einholen; Credential nur im Admin-UI, nie im Repo.
- Die Preisfelder haben unklare Semantik (Mitglied/Nichtmitglied/Roaming); gegen den Tarifkatalog (`docs/07`) prüfen, bevor sie als Ad-hoc-Preis gelten.
- Betreiber ist Klartext ohne ID; Betreibernamen sind uneinheitlich (z. B. „Energie Graz GmbH“ und „Energie Graz GmbH (CPO)“) und brauchen eine Zuordnungstabelle.
- Die Anzahl der Treffer ist pro Ausschnitt begrenzt durch Paging (`count`); große Ausschnitte erfordern mehrere Seiten.
