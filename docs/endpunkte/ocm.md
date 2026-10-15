# Endpunkt-Notiz: Open Charge Map – öffentliche API v3

| | |
|---|---|
| Analysiert am | 2026-10-15 |
| Analysiert von | chargeglobe (Adapter `ChargeGlobe.Providers.OpenChargeMap`) |
| Methode | Export-Doku: OpenAPI-Beschreibung [`ocm-openapi-spec.yaml`](https://github.com/openchargemap/ocm-docs/blob/master/Model/schema/ocm-openapi-spec.yaml), Referenzdaten aus [`CoreReferenceData.json`](https://github.com/openchargemap/ocm-app/blob/master/src/assets/data/CoreReferenceData.json); ohne Key antwortet die API `403` („You must specify an API key …“) |
| Tier | 0 (Open Data, `SourceTier.COMMUNITY`) |
| Zugang | API-Key (kostenlos, Registrierung bei openchargemap.org), Header `X-API-Key` oder Parameter `key`; im Config-Service als `provider.api_key` mit Scope `provider:ocm` |
| ToS geprüft | [Terms](https://openchargemap.org/site/about/terms): Open Data, mehr als 10.000 Requests/Tag nur nach Absprache |
| Lizenz / Attribution | Beiträge der Community (Data Provider 1) unter CC BY 4.0; importierte Datensätze tragen die Lizenz ihres Data Providers. Der Adapter fragt nur `opendata=true` ab, also nur CC-BY-4.0-Daten. Attribution: „Datenquelle: Open Charge Map (openchargemap.org), CC BY 4.0“ |
| Rate-Limit beobachtet | – (Adapter: 10/min, Burst 3, Tagesgrenze 10.000; Stundenbudget `provider.request_budget_per_hour`) |
| Capabilities | StaticInventory · BulkByBoundingBox |

## Endpunkte

### `GET https://api.openchargemap.io/v3/poi`
- Parameter: `output=json`, `client=chargeglobe`, `countrycode=AT`, `opendata=true`, `compact=false` (Betreiber- und Nutzungsobjekte inline), `verbose=false` (leere Felder weggelassen), `includecomments=false`, `boundingbox=(Nord,West),(Süd,Ost)`, `sortby=id_asc`, `greaterthanid=<letzte ID>`, `maxresults=1000`. Ein Inventar-Lauf je Region (`AT-9`, `AT-3`, `AT-4`, `AT-1`) liest seitenweise nach ID, bis eine Seite weniger als 1.000 Einträge hat (höchstens 20 Seiten).
- Header: `X-API-Key`, `User-Agent: chargeglobe/1.0 (+https://github.com/cpfaffinger/chargeglobe)`.
- Antwort: Array von Standorten (POI). Beispiel im Format der Doku, mit erfundenen Standorten und ohne Personendaten: `fixtures/ocm/poi-wien.json`.
```json
{
  "ID": 101,
  "DataProviderID": 1,
  "OperatorInfo": { "ID": 9001, "Title": "Test Operator" },
  "UsageTypeID": 4,
  "AddressInfo": { "Title": "Parkhaus Stephansplatz", "AddressLine1": "Teststraße 1", "Town": "Wien", "Postcode": "1010",
                   "Country": { "ISOCode": "AT" }, "Latitude": 48.20865, "Longitude": 16.37325 },
  "Connections": [ { "ID": 5001, "ConnectionTypeID": 25, "StatusTypeID": 50, "Amps": 32, "Voltage": 400,
                     "PowerKW": 22.0, "CurrentTypeID": 20, "Quantity": 2 } ],
  "NumberOfPoints": 2,
  "StatusTypeID": 50
}
```
- Mapping auf das Ingest-Modell:
  - `ID` → `ProviderLocationSnapshot.ExternalId`; `AddressInfo.Title` → `Name`; `AddressLine1`, `Postcode`, `Town`, `Country.ISOCode` → `Address`; `Latitude`/`Longitude` → `Coordinates` (0/0 oder fehlend → übersprungen).
  - `OperatorInfo.Title` → `OperatorName`, Platzhalter in Klammern (`(Unknown Operator)`, `(Business Owner at Location)`) → kein Betreiber. Einen dreistelligen Operator-Code gibt es nur, wenn eine `Connections[].Reference` eine EVSE-ID ist.
  - `UsageTypeID` → `Access` (Tabelle `Mapping/usage-mapping.json`: 1, 4, 5, 7 → `PUBLIC`; 2, 3, 6 → `RESTRICTED`; 0 → unbekannt).
  - Jede Verbindung wird zu `Quantity` EVSEs (fehlt sie: 1) mit je einem Stecker: `ExternalId` = `<ID>#<Connection.ID>` bzw. `<ID>#<Connection.ID>#<n>`; `Reference` → `EvseId`, wenn sie eine EVSE-ID ist und `Quantity` 1; `PowerKW` → Leistung (0 → unbekannt), `CurrentTypeID` 10/20/30 → `AC_1_PHASE`/`AC_3_PHASE`/`DC`, `Voltage`, `Amps`.
  - Kein Status: Die Statustypen von OCM sind Nutzermeldungen und oft Tage alt; sie entscheiden nur, ob ein Standort oder eine Verbindung übersprungen wird.
  - Verworfen werden Kontaktdaten und Nutzerinhalte: `UserComments`, `MediaItems`, `AddressInfo.ContactTelephone1/2`, `AddressInfo.ContactEmail`, `OperatorInfo.PhonePrimaryContact`, `PhoneSecondaryContact`, `ContactEmail`, `FaultReportEmail`, `AddressInfo`.

## Status-Mapping

`Mapping/status-mapping.json`; nur `PLANNED` und `REMOVED` haben Folgen (Standort bzw. Verbindung übersprungen).

| Rohwert (`StatusTypeID`) | Bedeutung bei OCM | OCPI-Status |
|---|---|---|
| 0 | Unknown | UNKNOWN |
| 10 | Currently Available (Automated Status) | AVAILABLE |
| 20 | Currently In Use (Automated Status) | CHARGING |
| 30 | Temporarily Unavailable | INOPERATIVE |
| 50 | Operational | UNKNOWN (in Betrieb, aber kein Live-Status) |
| 75 | Partly Operational (Mixed) | UNKNOWN |
| 100 | Not Operational | OUTOFORDER |
| 150 | Planned For Future Date | PLANNED |
| 200 | Removed (Decommissioned) | REMOVED |
| 210 | Removed (Duplicate Listing) | REMOVED |

## Stecker-Mapping

`Mapping/connector-mapping.json` (`ConnectionTypeID` → Standard, Bauform); nicht gelistete IDs → `UNKNOWN`.

| Rohwert | Bezeichnung bei OCM | ConnectorStandard |
|---|---|---|
| 25 | Type 2 (Socket Only) | IEC_62196_T2, Buchse |
| 1036 | Type 2 (Tethered Connector) | IEC_62196_T2, Kabel |
| 33 | CCS (Type 2) | IEC_62196_T2_COMBO |
| 32 | CCS (Type 1) | IEC_62196_T1_COMBO |
| 2 | CHAdeMO | CHADEMO |
| 1 | Type 1 (J1772) | IEC_62196_T1 |
| 28 | CEE 7/4 – Schuko – Type F | DOMESTIC_F |
| 23 | CEE 7/5 | DOMESTIC_E |
| 13 | Europlug 2-Pin (CEE 7/16) | DOMESTIC_C |
| 3 | BS1363 3 Pin 13 Amp | DOMESTIC_G |
| 1037 | T13 (Type J) | DOMESTIC_J |
| 1043 | Type M | DOMESTIC_M |
| 26 / 36 | SCAME Type 3C / 3A | IEC_62196_T3C / IEC_62196_T3A |
| 27 / 30 | NACS / Tesla Supercharger, Tesla (Model S/X) | TESLA_S |
| 8 | Tesla (Roadster) | TESLA_R |
| 1038 / 1039 / 1040 | GB-T AC Buchse / Kabel, GB-T DC | GBT_AC / GBT_AC / GBT_DC |
| 16, 17, 34, 35 | CEE 3/5 Pin, IEC 60309 3/5-pin (Stromstärke unbekannt) | UNKNOWN (Buchse) |

## Preisdarstellung
`UsageCost` ist Freitext („laut App des Betreibers“) und wird nicht übernommen.

## Offene Punkte / Risiken
- API-Key beantragen und unter `/admin/settings` als `provider.api_key` (Scope `provider:ocm`) eintragen, danach `provider.enabled` für `provider:ocm` einschalten.
- Echte Antworten mit Key aufzeichnen und die synthetische Fixture durch eine sanitisierte Aufnahme ersetzen.
- OCM-Standorte liegen oft einige Meter neben dem E-Control-Punkt; die Radien des Geo-Matchings (50 m mit Betreiber, 25 m ohne) sind nach dem ersten Lauf anhand der Konflikte zu prüfen.
