# Endpunkt-Notiz: EVN Drive – App (Android, `com.evn.autoladen`)

| | |
|---|---|
| Analysiert am | 2026-10-17 |
| Analysiert von | chargeglobe |
| Methode | Statische Analyse der APK (Version 2.6.0, Expo/React Native, Hermes-Bundle): `assets/app.config`, Strings im `index.android.bundle`; Abruf der Endpunkte mit curl (1 Aufruf je Variante, kein Crawling) |
| Tier | 2 (inoffizieller App-Endpunkt) |
| Zugang | **anonym** (kein Key, kein Token, keine Spezial-Header) |
| ToS geprüft | nein, **offen** (vor produktiver Nutzung: AGB `evn.at/agb`, Portal-Impressum, Rückfrage beim Betreiber) |
| Lizenz / Attribution | keine Angabe |
| Rate-Limit beobachtet | keins ausgelöst (wenige Aufrufe) |
| Capabilities | StaticInventory · EvseIds · LiveStatusPerEvse · BulkByBoundingBox (mit Cluster-Zoomstufen) – kein Preis, kein BulkAll, kein Delta |

## Befund

EVN Drive ist ein Mandanten-Build („`TENANT: evn`“) des **e-mobil-Portals** (`emobilportal.at`) von e-vo eMobility (NestJS-Backend). In `app.config` stehen: `API_URL=https://www.emobilportal.at/api/next`, `WEB_API_URL=https://www.emobilportal.at/api`, `OPERATOR_TOKEN=AT*EVN`, Kartenmitte 48.2 / 15.6167. Die Karte der App zeigt nicht nur EVN-Punkte, sondern **alle Ladepunkte, die das Portal kennt** (im Grazer Test: Energie Graz, Energie Steiermark, ENIO, SMATRICS, ChargePoint, da emobil …). Der Expo-Account der App heißt `e-vo_emobility`; ob andere Mandanten (z. B. ÖAMTC ePower, laut `docs/07` seit 2025 auf e-vo CPMS) dasselbe Backend nutzen, ist nicht geprüft (die ÖAMTC-App spricht `oeamtc.at`, siehe [oeamtc-epower.md](oeamtc-epower.md)).

## Endpunkte

Der Kartenabruf läuft über `WEB_API_URL`, nicht über `API_URL` (`/api/next/v1/chargeStations/chargeAtlas` → 404).

### `GET https://www.emobilportal.at/api/v1/chargeStations/chargeAtlas`
- Parameter (alle Pflicht, sonst 400 mit Klartext je Feld): `topLeftLat`, `topLeftLong`, `bottomRightLat`, `bottomRightLong` (Bounding Box, WGS84), `zoom` (Zahl).
- Header: keine.
- Verhalten nach Zoom: **Zoom 12** liefert Geohash-Cluster (`type:"cluster"`, `count`, `key`, `location`), **Zoom 15** liefert Stationen (`type:"charge_station"`) mit allen Ladepunkten. Die Schwelle dazwischen ist nicht ausgemessen.
- Antwort Zoom 15, Ausschnitt Graz (47.05–47.09 N, 15.40–15.47 E): 200, 241 kB, **146 Stationen, 315 Ladepunkte**.
```json
{
  "id": "14f05dfe-fd9f-4fe6-bbc3-b2a72fe273df",
  "stationId": "14f05dfe-fd9f-4fe6-bbc3-b2a72fe273df",
  "stationNameOriginal": "da emobil - KS1 Tower Graz Tiefgarage",
  "type": "charge_station",
  "operatorName": "da emobil", "operatorToken": "AT*DAE",
  "address": "Waagner-Biro-Straße 102, Graz",
  "location": { "latitude": 47.0804, "longitude": 15.411338 },
  "isOpen24Hours": true,
  "openingTimes": [ { "when": "EVERYDAY", "periods": [ { "begin": "00:00", "end": "23:59" } ] } ],
  "accessibility": [ "FREE_PUBLICLY_ACCESSIBLE" ],
  "hotlinePhoneNumber": "+435022772299",
  "additionalInfo": [ { "lang": "de-DE", "value": "Ladestation KS1 Tower Graz 22kW" } ],
  "chargePoints": [ {
    "evseId": "AT*DAE*E0002108", "maxPower": 22, "plugs": [ "TYPE_2_OUTLET" ], "status": "AVAILABLE",
    "isPublic": true, "isChargingAllowed": false, "isNationalCalibrationLawCompliant": false,
    "authorizationGroups": [ "01431a99-…" ]
  } ]
}
```
- Mapping auf Ingest-Modell:
  - `chargePoints[].evseId` → `ProviderEvseSnapshot.EvseId` (mit `*`, normalisieren)
  - `stationId` → Standort-Schlüssel der Quelle, `location` → Koordinaten, `operatorToken` → Betreiber-ID
  - `status` → Status (Tabelle unten), `maxPower` (kW) → Leistung, `plugs[]` → Stecker
  - `isChargingAllowed` bezieht sich vermutlich auf den (anonymen) Aufrufer (siehe Offene Punkte), nicht auf die Hardware.

### `GET https://www.emobilportal.at/api/v1/chargeStations/searchEvseId?evseId=…`
- Parameter `evseId` (Pflicht, String). Der Aufruf **ohne** Parameter ergibt 400 „evseId should not be empty“; die Antwort mit gültiger EVSE-ID wurde nicht abgerufen.

### Weitere im Bundle belegte Pfade (nicht aufgerufen)
- Anonym erreichbar, Parameter bekannt: `GET /api/next/v1/places/autocomplete?searchString=…` (400 ohne Parameter, also Route vorhanden).
- Mit Login (401 „No authentication token provided“): `/api/v1/InfrastructureChargeStations`, `/v1/InfrastructureChargeDetailRecords`, `/v1/ChargingTariffContracts`, `/v1/ChargePricing`, `/v1/ChargeStations/SearchInEvseIdAddressStationName`, `/v1/UserSettings/charge-point-favorites`, `/v1/charges`, `ChargePointsSecured/Execute…Command`. Tarife und Preise gehören zu den Verträgen des angemeldeten Kunden.

## Status-Mapping

| Rohwert | OCPI-Status |
|---|---|
| `AVAILABLE` | `AVAILABLE` |
| `OCCUPIED` | `CHARGING` (belegt; ob geladen oder nur parkend, nicht unterscheidbar) |
| `OUT_OF_SERVICE` | `OUTOFORDER` |

Beobachtet im Grazer Test: 251 / 49 / 15. Weitere Werte (z. B. Reservierung) wurden nicht gesehen.

## Stecker-Mapping

| Rohwert | ConnectorStandard |
|---|---|
| `TYPE_2_OUTLET` | `IEC_62196_T2` (Steckdose) |
| `TYPE_2_CONNECTOR_WITH_CABLE` | `IEC_62196_T2` (festes Kabel) |
| `CCS_COMBO_2_PLUG_WITH_CABLE` | `IEC_62196_T2_COMBO` |
| `CHADEMO` | `CHADEMO` |
| `TYPE_F_SCHUKO` | `DOMESTIC_F` |

## Preisdarstellung
Der anonyme Kartenabruf enthält **keine Preise**. Preise und Tarife liegen hinter dem Login (`ChargePricing`, `ChargingTariffContracts`). EVN-Tarife bleiben im Tarifkatalog (`docs/07`).

## Replay
`docs/endpunkte/replay/evn-drive.sh` (anonym). Beispiel St. Pölten: `S=48.10 N=48.30 W=15.50 E=15.75 ./evn-drive.sh`.

## Offene Punkte / Risiken
- **Wert für chargeglobe:** Zweitquelle mit Live-Status je EVSE für ganz Österreich (alle Betreiber im e-vo-Portal), EVSE-IDs direkt verwertbar. Abdeckung gegenüber E-Control nicht ausgemessen; im Grazer Ausschnitt 315 Ladepunkte (V-Go 256, ÖAMTC 133 Stationen).
- Statuswerte sind **nicht** die Originalmeldungen der Betreiber, sondern die des Portals (Aktualität/Latenz unbekannt; `last_updated` fehlt in der Antwort).
- Polling-Last: Ein Ausschnitt in Stadtgröße liefert 240 kB; ein landesweiter Durchlauf in Kacheln muss gedrosselt werden (≤ 1 Anfrage/s, `docs/05`).
- Das Backend gehört dem Portalbetreiber (Expo-Account `e-vo_emobility`): ToS/Erlaubnis klären (Betreiber EVN bzw. e-vo anfragen), bevor ein Adapter gebaut wird.
- Bedeutung von `isChargingAllowed` und `authorizationGroups` (Berechtigungsgruppen des Aufrufers) ungeklärt.
