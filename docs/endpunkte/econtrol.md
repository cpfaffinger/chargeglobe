# Endpunkt-Notiz: E-Control – Ladestellenverzeichnis (offizielle API)

| | |
|---|---|
| Analysiert am | 2026-10-14 |
| Analysiert von | Claude (Agent) mit dem API-Key des Betreibers, 8 lesende Requests |
| Methode | Export-Doku nicht lesbar (Swagger antwortet auch mit Key 401); Antworten von `GET /search` ausgewertet, Felder gegen die HA-Integration ([docs/07](../07-bestandsanalyse-apps-at.md)) abgeglichen |
| Tier | 0 |
| Zugang | eigener, kostenloser API-Key (Registrierung unter admin.ladestellen.at), an eine Domain gebunden: Header `apiKey: <key>` und `Referer: https://<registrierte Domain>/` |
| ToS geprüft | Nutzungsbedingungen wurden vom Betreiber bei der Registrierung akzeptiert (Click-Wrap); ihr Wortlaut ist noch nicht im Projekt archiviert. Bekannte Punkte laut Wiedergabe Dritter ([docs/08](../08-datenquellen-und-recht.md)): 2.500 Requests/h, max. 30 parallel, Quellenangabe mit Logo, Quartalsstatistik, Go-Live-Meldung, Weiterverbreitung nur nach schriftlicher Freigabe |
| Lizenz / Attribution | „Datenquelle: Ladestellenverzeichnis der E-Control (ladestellen.at)“ (Descriptor `AttributionText`, Tabelle `source`) |
| Rate-Limit beobachtet | keines (nur 8 Requests); erzwungen werden 40 Requests/min (2.400/h) mit Burst 10 je Prozess |
| Capabilities | StaticInventory · EvseIds · LiveStatusPerEvse · AdHocPrice · BulkByBoundingBox (per adaptivem Suchraster) |

Konfiguration (Config-Service, Scope `provider:econtrol`, Admin-UI `/admin/settings`): `provider.enabled`, `provider.api_key` (Secret, verschlüsselt), `provider.referer` (registrierte Domain; ein bloßer Hostname wird zu `https://<host>/` ergänzt). Der Key steht nie in Repo, `.env` oder Logs.

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

### Nicht erreichbar
`/stations`, `/countries/AT/stations` und die Swagger-Ressourcen (`/v3/api-docs/…`, `/swagger-resources`) antworten auch mit Key `401`. Ein Bulk-Endpunkt ist nicht belegt; die in [docs/07](../07-bestandsanalyse-apps-at.md) genannten Einzelabfragen (`/search/stations?evseId=`, `/countries/{cc}/operators/{op}/stations/{id}`) wurden nicht geprüft.

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
- Das Rate-Limit gilt je Prozess; das clusterweite Budget (`provider_rate_budget`) kommt mit dem Lease-Scheduler.
- Aktualisierungsrate des Live-Status und ein Bulk-Endpunkt sind unbekannt (Swagger nicht lesbar).
