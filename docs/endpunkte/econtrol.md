# Endpunkt-Notiz: E-Control – Ladestellenverzeichnis (offizielle API)

| | |
|---|---|
| Analysiert am | 2026-10-14 |
| Analysiert von | Claude (Agent) mit dem API-Key des Betreibers; Live-Probe auf srv-dev2 (Key nur im Speicher, entschlüsselt über den Config-Service, nie ausgegeben), rund 60 lesende Requests |
| Methode | Antworten aller Endpunkte unten ausgewertet (Pfad-Statistik über die vollständigen Antworten, Wertelisten, Abgleich `/search` ↔ DATEX-Status für 535 Ladepunkte in Wien); Spezifikation „Ladestellenverzeichnis“ v2.0 (Swagger 2.0, `host` `api.e-control.at`, `basePath` `/charge/1.0`, vom Betreiber als Datei bereitgestellt, nicht im Repo; die Beispielwerte einzelner Felder sind kein gültiges JSON) |
| Tier | 0 |
| Zugang | eigener, kostenloser API-Key (Registrierung unter admin.ladestellen.at), an eine Domain gebunden: Header `apiKey: <key>` und `Referer: https://<registrierte Domain>/`. Die Spezifikation deklariert kein Security-Schema; welche Endpunkte ein Key nutzen darf, zeigt erst der Aufruf (`401`) |
| ToS geprüft | Nutzungsbedingungen wurden vom Betreiber bei der Registrierung akzeptiert (Click-Wrap); ihr Wortlaut ist noch nicht im Projekt archiviert. Bekannte Punkte laut Wiedergabe Dritter ([docs/08](../08-datenquellen-und-recht.md)): 2.500 Requests/h, max. 30 parallel, Quellenangabe mit Logo, Quartalsstatistik, Go-Live-Meldung, Weiterverbreitung nur nach schriftlicher Freigabe |
| Lizenz / Attribution | „Datenquelle: Ladestellenverzeichnis der E-Control (ladestellen.at)“ (Descriptor `AttributionText`, Tabelle `source`) |
| Rate-Limit beobachtet | keines; erzwungen werden höchstens 40 Requests/min je Prozess (Descriptor) und clusterweit das Stundenbudget `provider.request_budget_per_hour` (Default 600) |
| Capabilities | StaticInventory · EvseIds · LiveStatusPerEvse · AdHocPrice · BulkAll (Register seitenweise) · NearestToPoint (Live-Status um Abfragepunkte, 100 Stationen je Request) |

Konfiguration (Config-Service, Scope `provider:econtrol`, Admin-UI `/admin/settings`): `provider.enabled`, `provider.api_key` (Secret, verschlüsselt), `provider.referer` (registrierte Domain; ein bloßer Hostname wird zu `https://<host>/` ergänzt), `provider.regions` (Default `AT-9,AT-3,AT-4,AT-1`), `provider.interval.inventory_minutes` (Default 1 Tag), `provider.request_budget_per_hour` (600), `provider.tile_interval_hot_minutes` / `…_normal_minutes` / `…_quiet_minutes` (5 / 15 / 60) und `provider.cooldown_step_minutes` / `…_max_minutes` (5 / 120). Der Key steht nie in Repo, `.env` oder Logs.

## Welcher Endpunkt wofür

| Daten | Endpunkt | Warum |
|---|---|---|
| Inventar (Standorte, Ladepunkte, Stecker, Zahlungsarten, Roaming, Ökostrom, Parken, Preis-URL) | `GET /search/stations` seitenweise | ganz Österreich in 17 Requests (≈ 37 MB), reichste Felder |
| Ad-hoc-Preise | `GET /search/stations` seitenweise | dieselben Seiten, Preise je Ladepunkt |
| Live-Status | `GET /search` je Abfragepunkt (`poll_tile`, [05](../05-crawling-konzept.md)) | einziger Endpunkt mit aktuellem Status je Ladepunkt; `/search/stations` hat keinen Status, die DATEX-Status-Publikation ist Stunden alt (siehe unten) |
| Health-Probe | `GET /search` (Wien, Stephansplatz), stündlich, nicht während einer Pause | billig und prüft den Status-Pfad |

## Endpunkte

### `GET https://api.e-control.at/charge/1.0/search/stations?fromIndex={n}&maxResults={m}` – Register
- Live geprüft am 2026-10-14: `200`, `totalResults` 16.070 Stationen, 41.136 Ladepunkte. `maxResults` wird bei **1.000** gekappt (20.000 liefert ebenfalls 1.000); eine Seite sind 2–4 MB, keine gzip-Kompression, kein `ETag`/`Last-Modified`.
- Antwort: `{ "totalResults", "fromIndex", "endIndex" (inklusiv), "stations": [...] }`. Die Stationen tragen dieselben Inhalte wie bei `/search`, aber mit anderen Namen (`evseCountryId`/`evseOperatorId`/`evseStationId`, `status` statt `stationStatus`, `location.latitude/longitude`; je Ladepunkt `energyInKw`, `connectorTypes`, `electricityTypes`, `authenticationModes`, `priceInCentPerKwh`, `priceInCentPerMin`) und **ohne Status der Ladepunkte**. Sanitisierter Auszug mit sechs echten Stationen (Kontaktfelder durch Platzhalter ersetzt): `fixtures/econtrol/stations-register.json`.
- Weitere Filter laut Spezifikation (nicht genutzt): `stationStatuses`, `evseId` (Wildcard `%`), `connectorTypes`, `minEnergyInKw`, `maxEnergyInKw`, `providers`, `roaming`, `freeOfCharge`, `latitude`/`longitude`/`maxDistanceInKm`.
- Beobachtete Werte (gesamtes Register):
  - `status`: `ACTIVE` 15.954, `INACTIVE` 116 – inaktive Stationen überspringt der Adapter.
  - `evseCountryId`: `AT` 15.122, `CH` 843 (eCarUp), `DK` 81, `DE` 19, `NL` 4, `IT` 1 – alle Stationen liegen in Österreich, das Land der EVSE-ID ist das des Betreibers.
  - 52 Stationen haben die Koordinaten 0/0 (Platzhalter) und werden als „ohne gültige Koordinaten“ übersprungen.
  - `authenticationModes`: `APP` 35.019, `RFID_READER` 34.644, `CREDIT_CARD` 20.689, `CHARGING_CONTRACT` 12.250, `QR` 10.572, `WEBSITE` 7.131, `DEBIT_CARD` 5.282, `CONTACTLESS_CARD_SUPPORT` 2.526, `CHIP_CARD_SUPPORT` 432, `CASH` 224, `PED_TERMINAL` 7.
  - `roaming` („roaming contracts accepted“): `true` 36.766, `false` 3.194, `null` 1.176.
  - `greenEnergy` `true` bei 9.482 Stationen; `freeParking` 8.066; `roofedParking` 261; `barrierFreeParkingPlaces` > 0 bei 843; `priceUrl` bei 1.948.
  - `providers` ist nur bei 315 Stationen befüllt, mit Werten wie „MGAtech GmbH“, „Best in Parking Garagen GmbH & Co KG“ – das sind Dienstleister des Betreibers, **keine akzeptierten Ladekarten**; nicht übernommen. Welche Ladekarten gehen, sagt nur `roaming`.
  - `electronicPaymentProviders` (Zahlungsdienstleister wie „card complete Service Bank AG“, „TeleCash from Fiserv“) bei 11 % der Ladepunkte; nicht übernommen, die Zahlungsart steht in `authenticationModes`.

### `GET https://api.e-control.at/charge/1.0/search?latitude={lat}&longitude={lon}` – Umkreissuche mit Live-Status
- Parameter: `latitude`, `longitude` (WGS 84, Dezimalpunkt); laut Spezifikation zusätzlich `capacity` (Mindestleistung kW) und `types` (Steckertypen).
- Antwort: JSON-Array der 100 nächsten Stationen (`distance` in km, aufsteigend), jede mit `points[]` inkl. **Live-Status** und Ad-hoc-Preis; eine Antwort sind rund 0,5 MB. Vollständiges, sanitisiertes Beispiel mit erfundenen Stationen: `fixtures/econtrol/search-wien.json`.
```json
[{ "countryId": "AT", "operatorId": "TST", "stationId": "ETEST0001", "stationStatus": "ACTIVE",
   "label": "…", "street": "…", "postCode": "1010", "city": "Wien",
   "location": { "lat": 48.2085, "lon": 16.3731 }, "distance": 0.012, "operatorName": "…",
   "greenEnergy": true, "freeParking": false, "roofedParking": false, "barrierFreeParkingPlaces": 0, "priceUrl": null,
   "openingHours": [{ "fromWeekday": "MONDAY", "fromTime": "00:00", "toWeekday": "SUNDAY", "toTime": "24:00" }],
   "points": [{ "evseId": "AT*TST*ETEST0001*1", "capacityKw": 22, "freeOfCharge": false, "roaming": true,
                "priceCentKwh": 59, "priceCentMin": null, "blockingFeeCentMin": 10, "blockingFeeFromMinute": 240, "startFeeCent": 0,
                "authenticationMode": ["RFID_READER", "APP"],
                "connectorType": [{ "key": "STYPE2", "legacy": true, "consumerName": "TYPE_2_AC" }],
                "electricityType": ["AC_3_PHASE"], "status": "AVAILABLE" }] }]
```
- Ohne gültigen Key: `401` mit deutschem Erklärtext. Fehlerstatus gibt der Client als `ProviderHttpException` mit dem `Retry-After` der Antwort weiter; der Adapter wiederholt nichts, der Crawler pausiert die Quelle ([05](../05-crawling-konzept.md), Abschnitt 4).

### Personenbezogene Daten
`contactName`, `phoneCountryCode`, `regionCode` (= Telefonvorwahl), `phoneNumber`, `extensionNumber`, `email` und `owner` entfernt der Client sofort bei beiden Endpunkten; sie landen weder im Ingest-Modell noch in `raw_snapshot` (DSGVO, [docs/08](../08-datenquellen-und-recht.md)). `operatorName` bleibt, obwohl einzelne Betreiber Privatpersonen sind (Pflichtangabe des Registers).

### Mapping auf das Ingest-Modell (`EControlMapper`)
`/search/stations` wird im Client auf die Form von `/search` (`StationDto`) gebracht; der Mapper kennt nur diese Form.
- `countryId*operatorId*stationId` → `ProviderLocationSnapshot.ExternalId`; `label`, `street`/`postCode`/`city`, `location`, `operatorName`, `operatorId`; Adressland immer `AT` (das Verzeichnis ist österreichisch), `countryId` → `OperatorCountry` (Land der EVSE-ID, z. B. `CH` für eCarUp); `Access = PUBLIC` (das Verzeichnis enthält nur öffentlich zugängliche Ladepunkte).
- `greenEnergy`, `freeParking`, `roofedParking`, `barrierFreeParkingPlaces`, `priceUrl` → `Facilities`; `priceUrl` wird eine absolute http(s)-Adresse (`www.example.at/tarife.pdf` → `https://www.example.at/tarife.pdf`), sonst `null`.
- `openingHours`: genau `MONDAY 00:00 – SUNDAY 24:00` → `TwentyFourSeven`, sonst Text.
- `points[].evseId` → `ProviderEvseSnapshot.EvseId` (roh) und `ExternalId`; ohne EVSE-ID `<Station>#<Position>`.
- Ladestation (`StationExternalId`): Die E-Control kennt keine Säulen-Ebene, ihre „Station“ ist mal eine Säule, mal ein ganzer Park. Hat die EVSE-ID die vierteilige Form `Land*Betreiber*E<Säule>*<Punkt>` mit kurzem letztem Teil (höchstens 3 Zeichen: `1`, `01`, `DC1`), ist der Teil davor die Ladestation (`EvseId.StationPart`, z. B. `AT*MER*E006566` für `AT*MER*E006566*01` und `*02`); teilen sich mehr als 4 Ladepunkte einer E-Control-Station denselben Teil, benennt er einen Standort und keine Säule (`EControlMapper.MaxPointsPerStation`). Keine Station bei `AT*GBC*E1` (dreiteilig), `AT*EST*E000*01994` (Nummer des Punkts am Ende) und z. B. `AT*VIE*E1150650007*1` … `*49` (Garage Wipark Möringgasse). Live am 16.10.2026 ergab die Regel rund 5.860 Ladestationen für 12.000 der 40.837 Ladepunkte.
- `capacityKw` → `MaxPowerKw` (EVSE und Stecker); `electricityType` mit genau einem OCPI-Wert → `PowerType`, sonst `null`.
- `authenticationMode(s)` → `AuthMethods` (Tabelle unten), `roaming` → `Roaming`.
- `status` → `Status` (Tabelle unten), Rohwert in `StatusRaw`; ohne Status (Register) bleibt beides `null`, es entsteht keine Statusmeldung.
- Preise: Cent → Euro (`priceCentKwh`, `priceCentMin`, `startFeeCent` → `SessionFee`, `blockingFeeCentMin`, `blockingFeeFromMinute`); `freeOfCharge: true` → 0 €/kWh und 0 € Sessiongebühr; immer Ad-hoc (`EmpKey = null`), Währung EUR, `TariffUrl` = Preis-URL der Station.

### Abdeckung einer Fläche
- **Register:** ein Lauf liest alle Seiten (`fromIndex` += Seitengröße, bis `totalResults` erreicht oder eine Seite leer ist; höchstens 1.000 Requests) und behält die Stationen in der Bounding Box des Plans. Der Inventar-Plan gilt für alle Regionen aus `provider.regions` gemeinsam (Region `AT-9,AT-3,AT-4,AT-1`, Box = umschließendes Rechteck), weil jeder Lauf ohnehin das ganze Register liest.
- **Register:** höchstens 50 Seiten je Lauf; jede Seite steht im Log („E-Control register page 3/17: stations 2001–3000 of 16070“).
- **Live-Status:** genau eine `GET /search` je Abfragepunkt. Die Punkte berechnet der Crawler stündlich aus den bekannten Standorten so, dass jeder Standort unter den 90 nächsten eines Punkts liegt (10 % Reserve zu den 100 der Quelle; gierige Mengenüberdeckung, 146 Punkte für die vier Startregionen); Priorität und Intervall aus den Statuswechseln je Ladepunkt der letzten 24 h ([05](../05-crawling-konzept.md), Abschnitt 2).

### DATEX II v3.5 (geprüft, nicht genutzt)
| Endpunkt | Ergebnis am 2026-10-14 |
|---|---|
| `GET /datex2/versions` | `200`, `[{"name":"v3.5"}]` |
| `GET /datex2/v3.5/energy-infrastructure-table-publication` | `200`, **217 MB** JSON (5 s), 16.069 Sites, 41.134 Stationen mit je genau einem Ladepunkt; Profil `ladestellen.at` 5.0.3. Station-ID = `<Site-ID>-<EVSE-ID>`, Site-ID = `<Land>-<operatorId>-<stationId>`. Enthält Betreiber mit Kontaktdaten (Telefon, E-Mail), Adresse, Öffnungszeiten, Stecker (`maxPowerAtSocket` in W), `authenticationAndIdentificationMethods` (`APPS`, `RFID`, `CREDIT_CARD`, `WEBSITE`, `DEBIT_CARD`, `NFC`, `CASH_PAYMENT`, `PINPAD`), `isGreenEnergy`, Fahrzeugmaße – aber **kein** Roaming, keine Preis-URL und kein Parken; gegenüber `/search/stations` sechsmal größer und ärmer |
| `GET /datex2/v3.5/energy-infrastructure-status-publication` | `200`, **74 MB** JSON (1,7 s), Status aller 41.134 Ladepunkte mit `lastUpdated` (Unix-ms) je Ladepunkt, Station und Site, `openingStatus`, `newRates` (kWh- und Minutenpreise). Beobachtete Status: `AVAILABLE`, `CHARGING`, `UNKNOWN`, `INOPERATIVE`, `OUT_OF_ORDER`, `BLOCKED`, `PLANNED`, `RESERVED` (`UNKNOWN` fehlt in der Werteliste der Spezifikation) |

Der Status-Snapshot ist **nicht live**: `publicationTime` war beim Abruf um 16:49 UTC 14:42 UTC, die jüngste Änderung (`lastUpdated`) 13:40 UTC; bei 535 Ladepunkten in Wien wich er in 22 % der Fälle vom gleichzeitigen `/search` ab (z. B. 50 × `AVAILABLE` laut `/search`, `CHARGING` laut DATEX). Die Messreihe zur Erneuerungsrate steht unter „Offene Punkte“. Deshalb kommt der Status weiter aus `/search`. Für den generischen DATEX-II-Adapter (Mobilithek, NAP) bleibt die Publikation ein gutes Testobjekt.

### Weitere Endpunkte
| Endpunkt | Ergebnis am 2026-10-14 |
|---|---|
| `GET /countries/AT/operators/basic` | `200`, 1.124 Betreiber (`operatorId`, `type`, `organization`, `firstName`, `lastName`) – bei Privatpersonen Vor- und Nachname; noch nicht genutzt |
| `GET /enumerations/connectorTypes` | `200`, 63 Schlüssel (Fixture `fixtures/econtrol/enumerations-connectorTypes.json`; ein Schlüssel hat ein Leerzeichen am Ende: `DOMESTIC_B `) |
| `GET /enumerations/authenticationModes` | `200`, 11 Schlüssel (Fixture `fixtures/econtrol/enumerations-authenticationModes.json`) |
| `GET /enumerations/vehicleTypes` | `200`, `CAR`, `LIGHT_VEHICLE`, `TRUCK`, `BUS`, `VAN` |
| `GET /enumerations/providers`, `electronicPaymentProviders`, `electricityTypes`, `smartServices` | `401` mit diesem Key |
| `GET /countries/{cc}/operators/{op}/stations`, `…/points/{evseId}`, `GET /nutscodes`, `GET /point-data-export/*` | nicht geprüft (eigene Betreiberdaten bzw. Excel-Export) |

## Status-Mapping

Datei `src/ChargeGlobe.Providers.EControl/Mapping/status-mapping.json`. In `/search` beobachtet: `AVAILABLE`, `CHARGING`, `OUTOFORDER`, `UNKNOWN`, `BLOCKED`, `INOPERATIVE`.

| Rohwert | OCPI-Status |
|---|---|
| `AVAILABLE`, `BLOCKED`, `CHARGING`, `INOPERATIVE`, `OUTOFORDER`, `PLANNED`, `REMOVED`, `RESERVED`, `UNKNOWN` | gleichnamig |
| `OCCUPIED` | `CHARGING` |
| alles andere | `UNKNOWN` (Rohwert bleibt in `StatusRaw`) |

## Stecker-Mapping

Datei `src/ChargeGlobe.Providers.EControl/Mapping/connector-mapping.json`; zwei Vokabulare, Legacy-Schlüssel (`legacy: true`) nennen auch Steckdose/Kabel. Ein Test prüft alle 63 Schlüssel der Werteliste der Quelle.

| Rohwert | ConnectorStandard | Format |
|---|---|---|
| `STYPE2` | `IEC_62196_T2` | `SOCKET` |
| `CTYPE2` | `IEC_62196_T2` | `CABLE` |
| `STYPE3` | `IEC_62196_T3C` | `SOCKET` |
| `CTYPE1` | `IEC_62196_T1` | `CABLE` |
| `CCCS1`, `IEC_62196_T1_COMBO` | `IEC_62196_T1_COMBO` | `CABLE` |
| `CCCS2`, `IEC_62196_T2_COMBO` | `IEC_62196_T2_COMBO` | `CABLE` |
| `CG105`, `CHADEMO` | `CHADEMO` | `CABLE` |
| `CTESLA` | `TESLA_S` | `CABLE` |
| `S309-1P-16A`, `S309-3P-16A`, `S309-3P-32A` | `IEC_60309_2_single_16`, `IEC_60309_2_three_16`, `IEC_60309_2_three_32` | `SOCKET` |
| `SBS1361` | `DOMESTIC_G` | `SOCKET` |
| `SCEE-7-8`, `DOMESTIC_F` | `DOMESTIC_F` | `SOCKET` |
| `S309-1P-32A`, `OTHER1PHMAX16A`, `OTHER1PHOVER16A`, `OTHER3PH` | `UNKNOWN` (kein OCPI-Wert) | `SOCKET` |
| `MCS` | `UNKNOWN` (kein OCPI-2.2.1-Wert) | `CABLE` |
| `PAN`, `WINDUCTIVE`, `WRESONANT`, `UNDETERMINED`, `UNKNOWN` | `UNKNOWN` | `null` |
| übriger OCPI-Name, ohne Rücksicht auf Groß-/Kleinschreibung und Leerzeichen (z. B. `IEC_60309_2_THREE_64`, `DOMESTIC_B `) | gleichnamig | `null` |
| alles andere | `UNKNOWN` (Rohwert in `RawType`) | `null` |

## Zahlungs- und Authentifizierungsarten

Datei `src/ChargeGlobe.Providers.EControl/Mapping/auth-mapping.json` → kanonisch `AuthMethod` ([docs/03](../03-datenmodell.md)); unbekannte Werte fallen weg, Duplikate auch. Ein Test prüft alle 11 Schlüssel der Werteliste.

| Rohwert | AuthMethod |
|---|---|
| `RFID_READER`, `APP`, `WEBSITE`, `CHARGING_CONTRACT`, `CONTACTLESS_CARD_SUPPORT`, `CHIP_CARD_SUPPORT`, `PED_TERMINAL`, `CASH` | gleichnamig |
| `QR` | `QR_CODE` |
| `CREDIT_CARD` | `CREDIT_CARD_PAYABLE` |
| `DEBIT_CARD` | `DEBIT_CARD_PAYABLE` |

## Preisdarstellung
Je Ladepunkt der Ad-hoc-Preis des Betreibers, in beiden Endpunkten mit denselben Feldern (Register `/search/stations`: `priceInCentPerKwh`, `priceInCentPerMin`; Suche `/search`: `priceCentKwh`, `priceCentMin`; beide `startFeeCent`, `blockingFeeCentMin`, `blockingFeeFromMinute`, `freeOfCharge`), dazu die Preis-URL der Station. Alle Beträge sind Cent ohne Währungsangabe (immer Euro); eine Gültigkeit (`valid_from`) und einen Freitext liefert die Quelle nicht, ebenso keine EMP-Tarife.

Abbildung (`EControlMapper.AdHocPrice` → `ProviderEvseSnapshot.AdHocPrice` als `PriceComponents`): Cent → Euro (`/100`), `blockingFeeFromMinute` → `BlockingAfterMinutes` (Minute der Sitzung, ab der die Blockiergebühr gilt), `freeOfCharge: true` → `FreeOfCharge` ohne Beträge, ohne jede Preisangabe → kein Preis. Der Preis kommt mit den Register-Seiten des Inventar-Abrufs, kostet also keinen zusätzlichen Request; der Worker schreibt ihn nach `evse_price_current` und bei einer Änderung eine Zeile nach `price_observation`.

## Offene Punkte / Risiken
- Wortlaut der Nutzungsbedingungen archivieren; Weiterverbreitung (API-Ausgabe von chargeglobe) schriftlich mit support@ladestellen.at klären; Go-Live-Meldung, Logo-Attribution und Quartalsstatistik umsetzen.
- Die registrierte Domain ist derzeit ein Platzhalter (`ogb.net`); bei Wechsel nur `provider.referer` ändern.
- Requests laufen durch zwei Budgets: 40/min je Prozess im Adapter und clusterweit `provider.request_budget_per_hour` in `provider_rate_budget`. Wie viele Abfragepunkte die vier Startregionen brauchen und wie frisch der Status damit ist, zeigen `/admin/crawler` und `crawler_run`.
- Erneuerungsrate der DATEX-Status-Publikation: Abrufe um 16:49 und 17:00 UTC lieferten beide den Stand von 14:42 UTC.
- Inaktive Stationen werden übersprungen; ein früher aktiver Standort bleibt dadurch aktiv, bis es eine Lebenszyklus-Logik für Standorte gibt, die eine Quelle nicht mehr meldet.
