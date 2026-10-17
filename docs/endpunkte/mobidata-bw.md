# Endpunkt-Notiz: MobiData BW – DATEX II EnergyInfrastructure (static/realtime)

| | |
|---|---|
| Analysiert am | 2026-10-17 |
| Analysiert von | Claude (Agent); rund 30 lesende Requests, vollständige Auswertung je einer Seite (5.000 bzw. 20.000 Standorte) beider Publikationen, Quellcode von OCPDB ([github.com/mobidata-bw/ocpdb](https://github.com/mobidata-bw/ocpdb)) |
| Methode | Export-Doku ([Datensatz](https://mobidata-bw.de/dataset/e-ladesaulen)), Antworten ausgewertet, OCPDB-Mapping-Doku (`docs/output_mapping/datex2_3.5_static.md`) |
| Tier | 0 (`OPEN_DATA`) |
| Zugang | anonym, ohne Key; keine Registrierung |
| ToS geprüft | Datensatzseite, 2026-10-17: Lizenz **DL-DE-BY 2.0**; Nutzungsbedingungen und Rate-Limits der API sind nicht dokumentiert ([08](../08-datenquellen-und-recht.md) §4; der Betreiber klärt sie per Mail an `mobidata-bw@nvbw.de`, [10 §3.2](../10-afir-umsetzung-eu.md)) |
| Lizenz / Attribution | `Datenquelle: MobiData BW / NVBW, dl-de/by-2-0` (Descriptor `AttributionText`, Tabelle `source`, `meta.attribution`). Die Quellen hinter MobiData BW haben eigene Lizenzen (BNetzA CC BY 4.0, EnBW CC BY 4.0, die meisten Mobilithek-Feeds CC0; [Quellenliste](https://api.mobidata-bw.de/ocpdb/api/public/v1/sources)) |
| Rate-Limit beobachtet | keines dokumentiert; Seiten über 5.000 Standorte beantwortete die API zeitweise mit `500`/`502`. Erzwungen: 12 Requests/min je Prozess (Descriptor) und clusterweit `provider.request_budget_per_hour` |
| Capabilities | StaticInventory · EvseIds · AdHocPrice · LiveStatusPerEvse · BulkAll (beide Publikationen liefern ganz Deutschland, seitenweise) |

Adapter: `ChargeGlobe.Providers.DatexII` (generisch, [04](../04-provider-interface-acl.md) „Generische Adapter“), Instanz `DatexProfiles.MobiDataBw` mit Key `mobidata-bw`. Konfiguration (Scope `provider:mobidata-bw`): `provider.enabled` (Default aus), `provider.regions` (leer = alle 16 Bundesländer), `provider.interval.inventory_minutes` (Default 1 Tag), `provider.interval.status_minutes` (Default 5 min), `provider.request_budget_per_hour`, `provider.cooldown_*`. Kein Key.

## Welcher Endpunkt wofür

| Daten | Endpunkt | Warum |
|---|---|---|
| Inventar (Standorte, Ladepunkte, Stecker, Zahlungsarten, Betreiber-ID) und Ad-hoc-Preise | `GET …/datex/v3.5/json/static` seitenweise | ein Plan `INVENTORY_FULL` für alle Regionen, täglich (etwa 25 Requests) |
| Status je Ladepunkt | `GET …/datex/v3.5/json/realtime` seitenweise | ein Plan `STATUS_POLL` für alle Regionen (Bulk-Status, [05](../05-crawling-konzept.md) Abschnitt 1), alle 5 min (etwa 10 Requests); die Statusmeldungen tragen keine Adresse, deshalb kein Plan je Region |
| Health-Probe | `GET …/static?offset=0&limit=1` | ein Standort, prüft Erreichbarkeit und Dokumentform |

Daneben bietet die API OCPI 3.0 (`…/ocpdb/api/public/ocpi/…`) und eine eigene REST-API (`…/ocpdb/api/public/v1/…`); nicht genutzt.

## Endpunkte

### `GET https://api.mobidata-bw.de/ocpdb/api/public/datex/v3.5/json/static?offset={n}&limit={m}` – Tabellenpublikation
- `200`, `application/json`, Server `gunicorn`, keine Kompression, kein `ETag`/`Last-Modified`. Ohne Parameter 100 Standorte (234 KB); `limit` und `offset` paginieren nach Standort-ID (`limit=5000` ≈ 11 MB, `limit=20000` ≈ 22 MB und zeitweise `5xx`). Am 17.10.2026 zwischen 100.000 und 120.000 Standorte (`offset=100000` liefert noch, `offset=120000` eine leere Publikation); darunter alle 116.000 Ladeeinrichtungen der Bundesnetzagentur und Schweizer Standorte (`countryCode: CH`, EVSE-IDs `CH*…`), die der Adapter überspringt.
- Struktur (Profil „Afir Energy Infrastructure“ 01-00-00, DATEX II 3.5 im JSON-Dialekt der Mobilithek-Schemas): `payload.aegiEnergyInfrastructureTablePublication.energyInfrastructureTable[].energyInfrastructureSite[]`; je Standort `idG` (OCPDB-interne Nummer, bei Betreiber-Feeds auch Texte wie `Mer Germany-3914` oder die EVSE-ID eines Punkts), `locationReference.locPointLocation.locLocationExtensionG.FacilityLocation.address` (`postcode`, `city.values[]`, `countryCode`, `addressLine[]` mit `type.value` `street`/`houseNumber`), `locationReference.locAreaLocation.coordinatesForDisplay`, `operator.afacAnOrganisation.name` und bei manchen Betreibern `externalIdentifier[]` (`typeOfIdentifier.extendedValueG: operatorId`, `identifier: DE*EUL`), `operatingHours.afacOpenAllHours`, `helpdesk` (Hotline), `energyInfrastructureStation[]` (genau eine je Standort: `authenticationAndIdentificationMethods[]`, `refillPoint[].aegiElectricChargingPoint`: `idG`, `currentType`, `availableVoltage[]`, `availableChargingPower[]` in W, `connector[]` mit `connectorType`, `connectorFormat`, `maxPowerAtSocket` in W, `voltage`, `maximumCurrent`, `electricEnergy[].energyRate[]`). Gekürzte echte Seite mit sieben Standorten: `fixtures/mobidata-bw/static-page.json`.
- Ladepunkt-IDs (`refillPoint.aegiElectricChargingPoint.idG`): EVSE-ID bei Betreiber-Feeds (`DE*SHN*E0030119`, `DE*EBW*E914213*1`), synthetisch `BNETZA*<Ladeeinrichtungs-ID>*<n>` bei Standorten aus dem Register der Bundesnetzagentur, bloße Nummern (`1347`) bei einzelnen Betreibern (Mer). Standorte, deren Ladepunkte alle `BNETZA*…` heißen, überspringt der Adapter samt ihren Statusmeldungen (`MirroredIdPrefixes` des Profils): dieselben Ladeeinrichtungen liest der Adapter `bnetza` direkt, und ihre Statusmeldungen hier sind nur Platzhalter (`inoperative`/`unknown`).
- Die API antwortet auf einzelne Seiten zeitweise mit `500`, auch bei 5.000 Standorten; eine solche Seite fordert der Adapter nach 15 s erneut an, höchstens zweimal (`PageRetryDelay`), damit ein Lauf von 25 Seiten nicht nach jeder Pause von vorn beginnt. Erst der dritte Fehler pausiert die Quelle ([05](../05-crawling-konzept.md) Abschnitt 4).
- Beobachtete Werte (5.000 Standorte ab `offset=0`): `connectorType` `iec62196T2` 1.989, `iec62196T2COMBO` 337, `teslaS` 34, `domesticF` 2; `connectorFormat` `socket` 1.693, `cableMode3` 468, fehlt 201; `currentType` `ac` 1.986, `dc` 369; `authenticationAndIdentificationMethods` `rfid`, `overTheAir`, `debitCard`, `nfc`, `creditCard`, `calypso`.
- Preise: `electricEnergy[].energyRate[]` mit `ratePolicy` (`adHoc`/`contract`), `applicableCurrency[]`, `energyPrice[]` (`priceType` `pricePerKWh`/`pricePerMinute`/`basePrice`/`flatRate`/`free`, `value`, `taxIncluded`, `taxRate`); nur bei Betreiber-Feeds (EnBW, Mer), nie bei Standorten aus dem Register.

### `GET https://api.mobidata-bw.de/ocpdb/api/public/datex/v3.5/json/realtime?offset={n}&limit={m}` – Statuspublikation
- Dieselbe Paginierung; am 17.10.2026 rund 48.000 Standorte mit Status (nur Quellen mit Echtzeit; `offset=60000` leer). Struktur: `payload.aegiEnergyInfrastructureStatusPublication.energyInfrastructureSiteStatus[]` mit `reference.idG` (Standort), `lastUpdated`, `energyInfrastructureStationStatus[].refillPointStatus[].aegiRefillPointStatus` (`reference.idG` = Ladepunkt-ID wie oben, `lastUpdated` ISO 8601, `status.value`). Gekürzte echte Seite: `fixtures/mobidata-bw/realtime-page.json`.
- Beobachtete Status (20.000 Standorte): `available` 6.457, `charging` 1.148, `outOfOrder` 711, `unknown` 527, `inoperative` 39, `reserved` 2. Standorte aus dem Register (`BNETZA*…`) melden `inoperative`/`unknown` ohne echte Aktualität.
- Frische: `publicationTime` ist die Abrufzeit; `lastUpdated` je Ladepunkt lag bei Betreiber-Feeds Minuten bis Stunden zurück, bei Tesla und EnBW (`realtime_data_updated_at` in der Quellenliste) stündlich.

### Personenbezogene Daten
`helpdesk` (Hotline-Nummern der Betreiber) und `contactInformation` entfernt der Parser aus dem Rohdatensatz; Betreibernamen bleiben.

### Mapping auf das Ingest-Modell (`DatexParser` + `DatexMapper`)
Der Parser bringt beide JSON-Dialekte des Modells (MobiData: `idG`/`versionG`, `values[]`, ISO-Zeiten, camelCase-Enums; E-Control: `id`/`version`, `values.value[]`, Unix-Millisekunden, GROSSBUCHSTABEN) in dieselben Records (`DatexSite`, `DatexStation`, `DatexRefillPoint`, `DatexSiteStatus`); Vokabulare werden ohne Groß-/Kleinschreibung und Unterstriche nachgeschlagen (`iec62196T2` = `IEC_62196_T_2`).
- `energyInfrastructureSite.idG` → `ProviderLocationSnapshot.ExternalId`; `name` → `Name`; `addressLine` `street` + `houseNumber`, `postcode`, `city`, `countryCode` → `Address` (Standorte anderer Länder als `Countries` des Profils werden übersprungen, ohne `countryCode` mit Issue); `coordinatesForDisplay` → `Coordinates` (ungültig/0,0 → Issue); `operator…name` → `OperatorName`; `OperatorId`/`OperatorCountry` aus `externalIdentifier` vom Typ `operatorId` (`DE*EUL`), sonst aus der ersten EVSE-ID; `Access` = `PUBLIC` (AFIR-Art.-20-Daten betreffen öffentlich zugängliche Ladepunkte); `operatingHours.afacOpenAllHours` → `TwentyFourSeven`, Zeiträume (`overallPeriod.validPeriod[]`) → Text, sonst `null`.
- Je `refillPoint`: `idG` → `ExternalId`; `EvseId` aus `externalIdentifier` vom Typ `evseId`, sonst aus der ID (ganz oder eingebettet wie `AT-006-EPI001-AT*006*E01-rp` → `AT*006*E01`), bei synthetischen IDs `null`; `connector[]` → `Connectors` (Tabelle unten, `maxPowerAtSocket` W → kW, `voltage`, `maximumCurrent`), `MaxPowerKw` = stärkster Stecker, sonst `availableChargingPower`; `PowerType` aus `currentType` (`dc` → `DC`, `ac` mit 400 V → `AC_3_PHASE`, mit 230 V → `AC_1_PHASE`, sonst `null`); `authenticationAndIdentificationMethods` der Station → `AuthMethods` (Tabelle unten; ohne Liste `null`); keine Ladestation (`StationExternalId` `null`: OCPDB führt je Standort genau eine Station).
- Preise: der erste `energyRate` mit `ratePolicy` `adHoc` (oder ohne) → `AdHocPrice` (`pricePerKWh` → `PricePerKwh`, `pricePerMinute` → `PricePerMinute`, `basePrice`/`flatRate` → `SessionFee`, `free` → `FreeOfCharge`); Nettopreise (`taxIncluded: false`) werden mit `taxRate` brutto gerechnet (0,6639 € netto, 19 % → 0,79 €); Währung aus `applicableCurrency` (Default `EUR`). `contract`-Tarife werden nicht übernommen.
- Status: `refillPointStatus` → `ProviderEvseStatus` (`ExternalId` = Ladepunkt-ID, `EvseId` daraus, `Status` nach Tabelle unten, `SourceReportedAt` = `lastUpdated`); Ladepunkte, deren EVSE-ID ein anderes Land nennt, werden übersprungen (Statusmeldungen tragen keine Adresse); IDs ohne EVSE-ID bleiben und werden über `source_record` zugeordnet, unbekannte verwirft der Worker.
- `raw_snapshot`: der Standort wie geliefert ohne `helpdesk`/`contactInformation`.

### Abdeckung einer Fläche
Inventar: ein Plan für alle Regionen (Box = umschließendes Rechteck); jede Seite wird gelesen, behalten werden Standorte der Länder des Profils in der Box, die nicht aus dem Register gespiegelt sind („DATEX II mobidata-bw static publication read: 110000 sites, 2000 in other countries, 100000 mirrored from another source, … outside the box, … skipped“); höchstens 100 Seiten je Lauf. Status: ein Plan für alle Regionen, der Lauf liest alle Seiten der Statuspublikation.

## Status-Mapping

Datei `src/ChargeGlobe.Providers.DatexII/Mapping/status-mapping.json` (`RefillPointStatusEnum` → OCPI, [08 §7](../08-datenquellen-und-recht.md)); beide Schreibweisen (`outOfOrder`, `OUT_OF_ORDER`).

| Rohwert | OCPI-Status |
|---|---|
| `available`, `blocked`, `charging`, `inoperative`, `planned`, `removed`, `reserved`, `unknown` | gleichnamig |
| `occupied` | `CHARGING` |
| `outOfOrder`, `faulted`, `outOfStock` | `OUTOFORDER` |
| `unavailable` | `INOPERATIVE` |
| alles andere | `UNKNOWN` (Rohwert in `StatusRaw`) |

## Stecker-Mapping

Datei `src/ChargeGlobe.Providers.DatexII/Mapping/connector-mapping.json` (`ConnectorTypeEnum`); `connectorFormat` `socket` → `SOCKET`, `cableMode3`/`cable` → `CABLE`, sonst `null`.

| Rohwert | ConnectorStandard |
|---|---|
| `chademo`, `yazaki` | `CHADEMO` |
| `iec62196T1`, `iec62196T1COMBO`, `iec62196T2`, `iec62196T2COMBO`, `iec62196T3A`, `iec62196T3C` | `IEC_62196_T1`, `IEC_62196_T1_COMBO`, `IEC_62196_T2`, `IEC_62196_T2_COMBO`, `IEC_62196_T3A`, `IEC_62196_T3C` |
| `iec60309x2single16`, `iec60309x2three16`, `iec60309x2three32`, `iec60309x2three64` | `IEC_60309_2_single_16`, `IEC_60309_2_three_16`, `IEC_60309_2_three_32`, `IEC_60309_2_three_64` |
| `domesticA` … `domesticO` | `DOMESTIC_A` … `DOMESTIC_O` |
| `pantographBottomUp`, `pantographTopDown` | `PANTOGRAPH_BOTTOM_UP`, `PANTOGRAPH_TOP_DOWN` |
| `teslaConnectorEurope`, `teslaConnectorAmerica`, `teslaS` | `TESLA_S` |
| `teslaR` | `TESLA_R` |
| `cee3`, `cee5`, `domestic`, `mcs`, `other` | `UNKNOWN` |
| alles andere | `UNKNOWN` (Rohwert in `RawType`) |

## Zahlungs- und Authentifizierungsarten

Datei `src/ChargeGlobe.Providers.DatexII/Mapping/auth-mapping.json` (`AuthenticationAndIdentificationEnum`) → `AuthMethod`; unbekannte Werte fallen weg.

| Rohwert | AuthMethod |
|---|---|
| `rfid`, `activeRFIDChip`, `mifareClassic`, `mifareDesfire` | `RFID_READER` |
| `apps`, `overTheAir` | `APP` |
| `website` | `WEBSITE` |
| `creditCard` | `CREDIT_CARD_PAYABLE` |
| `debitCard` | `DEBIT_CARD_PAYABLE` |
| `nfc` | `CONTACTLESS_CARD_SUPPORT` |
| `calypso` | `CHIP_CARD_SUPPORT` |
| `pinpad` | `PED_TERMINAL` |
| `plc` | `CHARGING_CONTRACT` |
| `cashPayment` | `CASH` |
| `phoneDialog`, `phoneSMS`, `prepaidCard`, `unlimitedAccess`, `noAccess` | keine |

## Preisdarstellung
Ad-hoc-Preise je Ladepunkt als `energyRate` in der Tabellenpublikation (siehe Mapping), nur bei Betreiber-Feeds; Blockiergebühren und Zeitfenster (`timeBasedApplicability`) liefert das Profil, werden aber noch nicht übernommen. Die E-Control-Publikationen (zweiter Testfall des Parsers, `fixtures/econtrol/datex-*.json`) tragen die Preise als `newRates` in der Statuspublikation (`rateLineCollection[].rateLine[]`: `PER_UNIT` je kWh, `INCREMENTING_RATE` mit `PT1M` je Minute, `FLAT_RATE` je Sitzung; `pricingPolicy` `FREE`), die der Adapter über `IPriceSource` liefert.

## Offene Punkte / Risiken
- Nutzungsbedingungen und Rate-Limits der API sind nicht dokumentiert; Antwort der NVBW (Mail des Betreibers) abwarten und ggf. `provider.request_budget_per_hour` oder die Intervalle anpassen.
- Standorte der Betreiber-Feeds überschneiden sich mit dem Register der Bundesnetzagentur (Adapter `bnetza`): mit EVSE-ID führt Schritt 1 der Matching-Kaskade sie zusammen, sonst die Geo-Kaskade über Betreibername und Abstand; mehrdeutige Fälle landen unter `/admin/conflicts` ([03](../03-datenmodell.md)).
- Ein voller Inventarlauf schreibt rund 110.000 Rohdatensätze (etwa 2 KB) nach `raw_snapshot`; bei täglichem Lauf und 30 Tagen Aufbewahrung etwa 7 GB. Bei Platznot `provider.interval.inventory_minutes` oder `ingest.snapshot_retention_days` senken.
- Mobilithek-Angebote, die MobiData BW nicht spiegelt, kommen erst mit dem Mobilithek-Profil (Client-Zertifikat, `If-Modified-Since`, [10 §3.3](../10-afir-umsetzung-eu.md)).
