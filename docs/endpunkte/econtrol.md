# Endpunkt-Notiz: E-Control – Ladestellenverzeichnis (offizielle API)

| | |
|---|---|
| Analysiert am | 2026-10-14 |
| Analysiert von | Claude (Agent) mit dem API-Key des Betreibers, 8 lesende Requests |
| Methode | Antworten von `GET /search` ausgewertet, Felder gegen die HA-Integration ([docs/07](../07-bestandsanalyse-apps-at.md)) abgeglichen; zusätzlich die Spezifikation „Ladestellenverzeichnis“ v2.0 (Swagger 2.0, `host` `api.e-control.at`, `basePath` `/charge/1.0`), vom Betreiber am 2026-10-14 als Datei bereitgestellt (nicht im Repo; die Beispielwerte einzelner Felder sind kein gültiges JSON) |
| Tier | 0 |
| Zugang | eigener, kostenloser API-Key (Registrierung unter admin.ladestellen.at), an eine Domain gebunden: Header `apiKey: <key>` und `Referer: https://<registrierte Domain>/` |
| ToS geprüft | Nutzungsbedingungen wurden vom Betreiber bei der Registrierung akzeptiert (Click-Wrap); ihr Wortlaut ist noch nicht im Projekt archiviert. Bekannte Punkte laut Wiedergabe Dritter ([docs/08](../08-datenquellen-und-recht.md)): 2.500 Requests/h, max. 30 parallel, Quellenangabe mit Logo, Quartalsstatistik, Go-Live-Meldung, Weiterverbreitung nur nach schriftlicher Freigabe |
| Lizenz / Attribution | „Datenquelle: Ladestellenverzeichnis der E-Control (ladestellen.at)“ (Descriptor `AttributionText`, Tabelle `source`) |
| Rate-Limit beobachtet | keines (nur 8 Requests); erzwungen werden 40 Requests/min (2.400/h) mit Burst 10 je Prozess |
| Capabilities | StaticInventory · EvseIds · LiveStatusPerEvse · AdHocPrice · BulkByBoundingBox (per adaptivem Suchraster) |

Konfiguration (Config-Service, Scope `provider:econtrol`, Admin-UI `/admin/settings`): `provider.enabled`, `provider.api_key` (Secret, verschlüsselt), `provider.referer` (registrierte Domain; ein bloßer Hostname wird zu `https://<host>/` ergänzt), `provider.regions` (Default `AT-9,AT-3,AT-4,AT-1`), `provider.interval.inventory_minutes` (Default 1 Tag) und `provider.interval.status_minutes` (Default 15 min: ein Durchgang über die vier Startregionen braucht einige hundert Suchen, vier Durchgänge pro Stunde bleiben unter 2.400 Requests). Der Key steht nie in Repo, `.env` oder Logs.

## Endpunkte

### `GET https://api.e-control.at/charge/1.0/search?latitude={lat}&longitude={lon}`
- Parameter: `latitude`, `longitude` (WGS 84, Dezimalpunkt).
- Header: `apiKey`, `Referer` (siehe oben). Ohne gültigen Key: `401` mit deutschem Erklärtext.
- Antwort: JSON-Array der 100 nächsten Stationen (`distance` in km, aufsteigend), jede mit `points[]` inkl. Live-Status und Ad-hoc-Preis. Vollständiges, sanitisiertes Beispiel mit erfundenen Stationen: `fixtures/econtrol/search-wien.json`.
```json
[{ "countryId": "AT", "operatorId": "TST", "stationId": "ETEST0001", "stationStatus": "ACTIVE",
   "label": "…", "street": "…", "postCode": "1010", "city": "Wien",
   "location": { "lat": 48.2085, "lon": 16.3731 }, "distance": 0.012, "operatorName": "…",
   "openingHours": [{ "fromWeekday": "MONDAY", "fromTime": "00:00", "toWeekday": "SUNDAY", "toTime": "24:00" }],
   "points": [{ "evseId": "AT*TST*ETEST0001*1", "capacityKw": 22, "freeOfCharge": false,
                "priceCentKwh": 59, "priceCentMin": null, "blockingFeeCentMin": 10, "blockingFeeFromMinute": 240, "startFeeCent": 0,
                "connectorType": [{ "key": "STYPE2", "legacy": true, "consumerName": "TYPE_2_AC" }],
                "electricityType": ["AC_3_PHASE"], "status": "AVAILABLE" }] }]
```
- Personenbezogene Felder (`contactName`, `phoneCountryCode`, `regionCode` = Telefonvorwahl, `phoneNumber`, `extensionNumber`, `email`, `owner`) entfernt der Client sofort; sie landen weder im Ingest-Modell noch in `raw_snapshot` (DSGVO, [docs/08](../08-datenquellen-und-recht.md)).
- Mapping auf Ingest-Modell (`EControlMapper`):
  - `countryId*operatorId*stationId` → `ProviderLocationSnapshot.ExternalId`; `label`, `street`/`postCode`/`city`, `location`, `operatorName`, `operatorId`; `Access = PUBLIC` (das Verzeichnis enthält nur öffentlich zugängliche Ladepunkte)
  - `openingHours`: genau `MONDAY 00:00 – SUNDAY 24:00` → `TwentyFourSeven`, sonst Text
  - `points[].evseId` → `ProviderEvseSnapshot.EvseId` (roh) und `ExternalId`; ohne EVSE-ID `<Station>#<Position>`
  - `capacityKw` → `MaxPowerKw` (EVSE und Stecker); `electricityType` mit genau einem OCPI-Wert → `PowerType`, sonst `null`
  - `status` → `Status` (Tabelle unten), Rohwert in `StatusRaw`
  - Preise: Cent → Euro (`priceCentKwh`, `priceCentMin`, `startFeeCent` → `SessionFee`, `blockingFeeCentMin`, `blockingFeeFromMinute`); `freeOfCharge: true` → 0 €/kWh und 0 € Sessiongebühr; immer Ad-hoc (`EmpKey = null`), Währung EUR

### Laut Spezifikation, noch nicht live geprüft
Die Spezifikation deklariert kein Security-Schema; alle Operationen nennen `401`/`403` als mögliche Antwort, der Zugriff hängt also vom Key und seinen Rechten ab. Live geprüft ist bisher nur `GET /search`; `/stations` und `/countries/AT/stations` (ohne Betreiber) antworteten `401` und stehen nicht in der Spezifikation.

| Endpunkt | Inhalt laut Spezifikation | Nutzen für chargeglobe |
|---|---|---|
| `GET /datex2/versions` | unterstützte DATEX-II-Versionen (derzeit ein eigener Endpunkt je Version) | Health-Probe für den Bulk-Zugang |
| `GET /datex2/v3.5/energy-infrastructure-table-publication` | **alle** Sites, Stationen und Ladepunkte (`EnergyInfrastructureTablePublication`; JSON oder XML) mit Betreiber, Lage, Öffnungszeiten, Tarifinformation (`rates`), Zugangs- und Authentifizierungsarten | Inventar in **einem** Request statt Suchraster |
| `GET /datex2/v3.5/energy-infrastructure-status-publication` | Status **aller** Ladepunkte (`RefillPointStatus.status`: `AVAILABLE`, `BLOCKED`, `CHARGING`, `FAULTED`, `INOPERATIVE`, `OCCUPIED`, `OUT_OF_ORDER`, `OUT_OF_STOCK`, `PLANNED`, `REMOVED`, `RESERVED`, `UNAVAILABLE`) mit `lastUpdated` je Ladepunkt, dazu `openingStatus`, `fault`, `newRates` | Status in **einem** Request, `lastUpdated` als `source_reported_at` (Latenz messbar); eigenes Status-Mapping nötig |
| `GET /search/stations` | Filtersuche mit Paging (`fromIndex`, `maxResults`; Antwort mit `totalResults`, `fromIndex`, `endIndex`), Filter u. a. `stationStatuses`, `evseId` (Wildcard `%`), `connectorTypes`, `minEnergyInKw`, `providers`, `roaming`, `freeOfCharge`, Koordinaten + `maxDistanceInKm` | Fallback für einen vollständigen Abzug ohne Raster; gezielte Abfrage einzelner EVSEs |
| `GET /search?latitude=&longitude=` | wie oben beschrieben; zusätzlich Filter `capacity` (Mindestleistung kW) und `types` (Steckertypen) | derzeit genutzt |
| `GET /countries/{cc}/operators/basic` | Basisdaten aller Betreiber eines Landes | Betreiber-Stammdaten und Filter |
| `GET /countries/{cc}/operators/{op}/stations`, `…/stations/{id}`, `…/points/{evseId}` | Stationen eines Betreibers bzw. ein Ladepunkt („based on user permissions“) | vermutlich nur für eigene Betreiberdaten; nicht für den Abzug |
| `GET /enumerations/connectorTypes`, `electricityTypes`, `authenticationModes`, `electronicPaymentProviders`, `providers`, `vehicleTypes`, `smartServices` | Wertelisten der Quelle | Mapping-Tabellen gegen die vollständige Liste prüfen; `providers` als Startkatalog der EMPs |
| `GET /nutscodes`, `GET /point-data-export/region-codes`, `GET /point-data-export/xlsx?regionCodes=` | NUTS-1-Codes; Export als Excel je Bezirk/Bundesland | nicht benötigt (Regionen aus OSM, Excel nicht maschinenfreundlich) |

Zusätzliche Felder je Station bzw. Ladepunkt, die das Ingest-Modell noch nicht übernimmt: `providers`/`otherProviders` (akzeptierte Ladekarten bzw. Anbieter), `electronicPaymentProviders`, `authenticationModes`, `roaming`, `priceUrl`, `contractBasedPayment`, `greenEnergy`, `austrianEcoLabel`, `freeParking`, `roofedParking`, `illuminatedParking`, `parkingPlaces`, `barrierFreeParkingPlaces`, `vehicleTypes` und maximale Fahrzeugmaße, `smartService`, `nutsCode`, `timezone`, Koordinaten je Ladepunkt.

### Abdeckung einer Fläche
`SearchGrid` sucht vom Mittelpunkt einer Zelle aus; ist die Antwort eine volle Seite (100) und liegt die entfernteste Station näher als die Zellecken, wird die Zelle geviertelt. Dichte Gebiete (Wiener Innenstadt: 100 Stationen in 1,2 km) bekommen so feine, ländliche grobe Zellen. Unter 50 m halber Diagonale wird nicht mehr geteilt; ein Lauf endet nach 1.000 Requests (Warnung im Log). Ohne Bounding Box gilt ganz Österreich.

## Status-Mapping

Datei `src/ChargeGlobe.Providers.EControl/Mapping/status-mapping.json`. Beobachtet wurden nur OCPI-Werte (`AVAILABLE`, `CHARGING`, `OUTOFORDER`, `UNKNOWN`, `BLOCKED`, `INOPERATIVE`).

| Rohwert | OCPI-Status |
|---|---|
| `AVAILABLE`, `BLOCKED`, `CHARGING`, `INOPERATIVE`, `OUTOFORDER`, `PLANNED`, `REMOVED`, `RESERVED`, `UNKNOWN` | gleichnamig |
| `OCCUPIED` | `CHARGING` |
| alles andere | `UNKNOWN` (Rohwert bleibt in `StatusRaw`) |

## Stecker-Mapping

Datei `src/ChargeGlobe.Providers.EControl/Mapping/connector-mapping.json`; zwei Vokabulare, Legacy-Schlüssel (`legacy: true`) nennen auch Steckdose/Kabel.

| Rohwert | ConnectorStandard | Format |
|---|---|---|
| `STYPE2` | `IEC_62196_T2` | `SOCKET` |
| `CTYPE2` | `IEC_62196_T2` | `CABLE` |
| `CTYPE1` | `IEC_62196_T1` | `CABLE` |
| `CCCS2`, `IEC_62196_T2_COMBO` | `IEC_62196_T2_COMBO` | `CABLE` |
| `IEC_62196_T1_COMBO` | `IEC_62196_T1_COMBO` | `CABLE` |
| `CG105`, `CHADEMO` | `CHADEMO` | `CABLE` |
| `DOMESTIC_F` | `DOMESTIC_F` | `SOCKET` |
| übriger OCPI-Name (z. B. `IEC_62196_T2`) | gleichnamig | `null` |
| alles andere | `UNKNOWN` (Rohwert in `RawType`) | `null` |

## Preisdarstellung
Je Ladepunkt der Ad-hoc-Preis des Betreibers: kWh-Preis und/oder Minutenpreis, Startgebühr, Blockiergebühr ab Minute X, Kennzeichen „kostenlos“. EMP-Tarife liefert die Quelle nicht.

## Offene Punkte / Risiken
- Wortlaut der Nutzungsbedingungen archivieren; Weiterverbreitung (API-Ausgabe von chargeglobe) schriftlich mit support@ladestellen.at klären; Go-Live-Meldung, Logo-Attribution und Quartalsstatistik umsetzen.
- Die registrierte Domain ist derzeit ein Platzhalter (`ogb.net`); bei Wechsel nur `provider.referer` ändern.
- Requests laufen durch zwei Budgets: 40/min je Prozess im Adapter und clusterweit in `provider_rate_budget` (ebenfalls 40/min, Burst 10). Die tatsächliche Anzahl Suchen je Durchgang ist erst mit Live-Daten messbar (`poll_plan.last_result`, Crawler-Log).
- Ob der Key die DATEX-II-Publikationen und `/search/stations` nutzen darf, ist offen (401/403 laut Spezifikation möglich); ebenso Antwortgröße, Aktualisierungsrate des Status und ob die Publikationen dieselben EVSE-IDs und Stationen wie `/search` liefern.
