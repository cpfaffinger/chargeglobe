# Endpunkt-Notiz: E-Control Tarifkalkulator (ladetarif.at)

| | |
|---|---|
| Analysiert am | 2026-10-17 |
| Analysiert von | chargeglobe |
| Methode | Öffentliche OpenAPI-Beschreibung des Backends (`GET /api/v3/api-docs`, OpenAPI 3, Spring Boot), Bundle der Angular-App (`main.*.js`), Abruf der öffentlichen Endpunkte mit curl (≥ 2 s Abstand, rund 75 Requests insgesamt, kein Login, kein Captcha) |
| Tier | 0 (offizielle Quelle der Regulierungsbehörde), aber **ohne dokumentierte Nutzungsfreigabe** |
| Zugang | anonym, kein Key, keine Spezial-Header; Schreib-/Konto-Endpunkte mit `basicAuth` |
| ToS geprüft | ja, 2026-10-17: es gibt **keine** Nutzungsbedingungen für ladetarif.at; robots.txt sperrt nur `/admin/`; Impressum/Haftungsausschluss in der App und auf [e-control.at/econtrol/links/impressum](https://www.e-control.at/econtrol/links/impressum); Methodik und Haftungsausschluss auf [e-control.at/ladetarife](https://www.e-control.at/ladetarife) |
| Lizenz / Attribution | **keine Lizenz angegeben** (kein CC BY, kein Eintrag auf data.gv.at); Weiterverbreitung ungeklärt. Vorschlag für die Quellenangabe: „Quelle: E-Control, Lade-Tarifkalkulator ladetarif.at (Angaben der Anbieter)“ |
| Rate-Limit beobachtet | keins (keine `RateLimit`-/`Retry-After`-Header, kein 429 bei ≥ 2 s Abstand); Antwortzeiten 0,05–0,6 s, `/api/operators` 2,2 s |
| Capabilities | EmpTariff (netzweite Tarife der Ladekarten/Ladeverträge AT) · Stationsliste je Produkt (E-Control-Stations-IDs) · Betreiberliste mit Betreiber-ID — kein Ladepunkt-Inventar, kein Status, kein Ad-hoc-Preis, kein Delta, keine Historie alter Produktversionen |

## Zweck

ladetarif.at ist der Lade-Tarifkalkulator der E-Control (im Auftrag und finanziert vom BMWET). Österreichische EMPs (Ladekarten-/App-Anbieter) pflegen dort ihre Produkte selbst über `admin.ladetarif.at`: Grund- und Aktivierungsgebühr, je Tarif Preise nach Stromart und Leistungsklasse (kWh/min, Tag/Nacht), Blockiergebühren und die Auswahl der Ladestellenbetreiber, an denen der Tarif gilt (aus dem Ladestellenverzeichnis). Stand 2026-10-17: **43 aktive Produkte von 22 Anbietern (24 Marken)**, 73 registrierte Anbieter, 913 Betreiber. Für chargeglobe ist es die einzige offizielle, strukturierte Quelle für EMP-Tarife in AT (ADR-0013). chargeglobe übernimmt die Daten nicht, sondern gleicht den kuratierten Katalog damit ab (`scripts/tariffs/compare-ladetarif.py`, siehe „Entscheidung“).

## Endpunkte

Basis: `https://www.ladetarif.at/api` (intern `http://backend.net49.dmz/lade-tk`). Alle Antworten `application/json` (vom Server eingerückt), `cache-control: no-store`, Fehler als `ApiErrorDto` `{uuid, exceptionType, errorMessages[]}` (Meldungen deutsch).

| Methode + Pfad | operationId | Zweck | Auth |
|---|---|---|---|
| `GET /v3/api-docs` | – | OpenAPI-Beschreibung aller Endpunkte (275 kB) | anonym |
| `POST /calculate?page=&size=` | `calculate` | Produktliste mit hochgerechneten Jahreskosten | anonym |
| `POST /calculate/{productId}` | `calculateProduct` | dieselbe Rechnung für ein Produkt | anonym |
| `GET /products/{productId}` | `getProductDetails` | vollständiges Produkt mit Tarifen und Gebühren | anonym |
| `GET /products/{productId}/stations` | `getStationsByProduct` | Ladestellen, an denen das Produkt gilt (`primary` = Haupttarif) | anonym |
| `GET /operators[?id=…&id=…]` | `getOperators` | Ladestellenbetreiber mit Betreiber-ID und Stationszahl | anonym |
| `GET /operators/{operatorId}/stations` | `getStationsByOperator` | Ladestellen eines Betreibers (`operatorId` = interne `id`) | anonym |
| `GET /providers?page=&size=` | `getProviders` | registrierte Anbieter (Footer „Registrierte Anbieter“), paginiert | anonym |
| `GET /providers/list` | `getProvidersList` | alle Anbieter mit interner `id` und `status` | anonym |
| `GET /documents/{uuid}/download` | `downloadDocument` | Preisblatt/Vertrag/AGB als Datei (`attachment`) | anonym |
| `GET /config/params` | `getConfigParams` | Leistungsklassen-Vorlagen, USt-Satz 20 % | anonym |
| `GET /data-lists/fixed-fee-types`, `/feature-types`, `/vehicles` | | Typ-Kataloge (`1` = `BASIC_FEE`, `2` = `ACTIVATION_FEE`; `1` = `GREEN_ENERGY`, `2` = `BINDING_PERIOD`, `3` = `ONLY_EXISTING_CUSTOMERS`) und Musterfahrzeuge | anonym |

Nicht öffentlich (basicAuth): `/import/products/*` (Import-Schnittstelle der Anbieter, mit Zertifikat), `/providers/{id}/brands/*`, `/products/{id}/versions`, `/products/{a}/compare/{b}`, `/my-data/*`, `/admin/*`. `/captcha/verify` (reCAPTCHA v3) wird nur bei Registrierung/Kontakt genutzt; die Lese-Endpunkte verlangen kein Captcha.

## Zugang & Auth

Keine Anmeldung, kein Key, kein Cookie, keine besonderen Header (`Content-Type: application/json` bei POST genügt). HSTS, CSP `connect-src 'self'` (betrifft nur Browser). Ein eigener User-Agent mit Kontakt-URL wurde akzeptiert.

## Format & Felder

### `POST /api/calculate?page=0&size=200`

Pflichtfelder laut Schema: `vehicle.consumptionKwh`, `annualDrivingDistanceKm`, `publicChargingPercentage` (sonst 400 `BAD_REQUEST` „publicChargingPercentage: must not be null“). Default `size` = 50, `page` ab 0; eine Seite mit `size=200` enthält alle Produkte.

**Minimaler Body mit echten Preisen** (`calculate.request.json`):

```json
{
  "vehicle": { "consumptionKwh": 19, "maxChargeKwAc": 11, "maxChargeKwDc": 150 },
  "annualDrivingDistanceKm": 15000,
  "publicChargingPercentage": 100,
  "areas": ["BURGENLAND","KAERNTEN","NIEDEROESTERREICH","OBEROESTERREICH","SALZBURG","STEIERMARK","TIROL","VORARLBERG","WIEN","OTHER"]
}
```

- Ohne `maxChargeKwAc`/`maxChargeKwDc` liefert der Server 200 mit **allen 43 Produkten, aber Preisen ≈ 0** (nur Pauschalen); die Fahrzeugleistung ist für die Rechnung nötig. Für eine reine Produktliste reicht der Body ohne Leistungen.
- `areas` (Bundesländer der Ladestellen) **filtert Produkte**: nur `WIEN` → 27 Produkte; alle Bundesländer bzw. ohne `areas` → 43.
- Weitere optionale Felder: `nightChargingPercentage`, `minChargingPowerKw`/`maxChargingPowerKw`, `chargeSpeeds` (Namen der Leistungsklassen `normal`/`beschleunigt`/`schnell`/`ultraschnell`, filtert ebenfalls), `features[]` (`GREEN_ENERGY`, `BINDING_PERIOD`, `ONLY_EXISTING_CUSTOMERS`), `filterProductsWith…`-Flags, `discountIds[]`.

Antwort `{ "request": <Echo des Bodys>, "products": [CalculatedProductDto…] }`, sortiert nach Jahreskosten; keine Gesamtzahl/Seiteninfo.

| Feld | Bedeutung |
|---|---|
| `id` | Produkt-ID (**je Version**, siehe unten) |
| `name`, `providerName`, `brandName` | Produkt, Anbieter (Firma), Marke |
| `mainTariffStationCount` | Ladestellen zum Haupttarif („Stationen zum Tarif“) |
| `otherTariffsStationCount` | Ladestellen zu Partnertarifen („Stationen zum Partnertarif“), bei `calculate` nach Filtern gezählt |
| `features[]` | `{id, typeId}` (Katalogtyp) oder `{id, name, description}` (Freitext) |
| `pricePerYear`, `pricePerMonth`, `pricePer100km`, `pricePerKwh` | je `{net, tax, gross}` (optional `discounts`), fiktive Hochrechnung – **nicht** als Tarifpreis verwenden |

### `GET /api/products/{id}` (`ProductDetailsDto`)

```
id, validFrom, validTo?            ISO-Datum ohne Zone (Ortszeit), validTo exklusiv-ähnlich "…T23:59:59.999"
name, website, description?
mainTariffStationCount, otherTariffsStationCount
provider { id, emaCountryCode, emaProviderId, name, commercialRegisterNumber, address, postCode, city, countryCode, website }
brand    { id, name, description?, commercialRegisterNumber, address, postCode, city, countryCode, phone?, mail?, website,
           documents { logo{…, content = Base64 des Logos}, agbs{id, uuid, title, name, byteSize, mimeType}, others[] } }
features[]   { id, typeId? | name, description }
fixedFees[]  { id, typeId? (1 BASIC_FEE | 2 ACTIVATION_FEE) | name (Freitext, nicht rechenrelevant), priceEuro,
               interval: ONCE | MONTH | QUARTER | YEAR | TWELVE_MONTHS | CHARGE }
tariffs {
  main[], others[]: {
    id, name,
    model: ZONE_POWER | ZONE_CONSUMPTION | ZONE_POWER_CONSUMPTION | SCALE_CONSUMPTION | SCALE_POWER_CONSUMPTION,
    unit:  KWH | MIN | KWH_AND_MIN,
    nightStart?, nightEnd?            "HH:mm:ss" (Beginn/Ende des Nachtfensters)
    fixedFees[]                       wie oben, praktisch nur interval CHARGE/ONCE/MONTH mit Freitext-Namen
    variableFees[] { id, current AC|DC, to? (kW-Obergrenze inkl.; fehlt = offen nach oben),
                     toPowerForConsumption?, priceDayConsumptionEuro? (€/kWh), priceDayTimeEuro? (€/min),
                     priceNightConsumptionEuro?, priceNightTimeEuro? }
    blockingFees[] { id, current AC|DC, to? (kW-Obergrenze), priceDayTimeEuro (€/min), priceNightTimeEuro?,
                     timeLimit (Minute, ab der die Gebühr gilt) }
  }
}
discounts[]  { id, name, description?, calculated, useAutomatically?, periodType?, periodValue?,
               unit? FLAT_RATE|PERCENTAGE|FREE_KWH, value?, usePercentageForFixedFees?, usePercentageForVariableFees? }
favorite     immer false (anonym)
uploads { priceSheet{id, uuid, title, name, byteSize, mimeType}, contract?, others[] }
```

**Kein Betreiberbezug im Tarif**: Die öffentliche Detailansicht nennt nicht, für welche Betreiber ein Tarif gilt. Intern existiert das (`TariffDto.operators[]` = `SelectedOperatorDto {evseOperatorId, stationCount, stations[]}` im Import-/Anbieter-Schema), öffentlich nur indirekt über `/products/{id}/stations`.

Ist-Zustand aller 43 Produkte (2026-10-17): `model` ausschließlich `ZONE_POWER` (89 Tarife); `unit` 72× `KWH`, 9× `MIN`, 8× `KWH_AND_MIN`; Nachtpreise nur bei Wien Energie Tanke ZEIT (22–8 Uhr) und STW eMobil Klagenfurt (19–6 Uhr); Produkt-`fixedFees` nur Typ 1 (`MONTH`, 19×) und Typ 2 (`ONCE`, 14×); Tarif-`fixedFees` 8× mit Freitext (Ansteck-/Zutrittsgebühr `CHARGE`, Ladekarte `ONCE`, „Transaktionsgebühr“ `MONTH`); Rabatte 12× Text ohne Rechenwerte, 2× `PERCENTAGE` (illwerke vkw); `timeLimit`-Werte 30–781 (häufig 61, 181, 241, 481, aber auch 60, 180, 240); Uploads: 43 Preisblätter, 9 Verträge. Aufteilung main/others: 20× (1,1), 12× (1,0), 4× (2,0), 4× (1,2), 2× (3,0), 1× (1,10).

### `GET /api/products/{id}/stations` (`ProductStationDto[]`)

`{id, evseStationId, name, address, postCode, city, latitude, longitude, primary, federalState, supportedChargeSpeeds[]}`. `evseStationId` ist die **Stations-ID des E-Control-Ladestellenverzeichnisses** (gleiches Format wie `evseStationId` in `fixtures/econtrol/stations-register.json`, z. B. `E6CBF3BB2F3E7DBB1DC7F62D45DAB`), **ohne Betreiber-ID**. `primary = true` ⇔ Haupttarif. Für #Drive Smart: 11.829 Stationen (4 MB), 1.900 `primary` – exakt die Stationen von `GET /operators/4964/stations` (EVN). Welche der Partnerstationen zu welchem von mehreren `others`-Tarifen gehört, steht nirgends.

### `GET /api/operators` (`OperatorDto[]`)

`{id, evseCountryCode, evseOperatorId, name, stationCount}` – **ja, liefert Betreiber-IDs**: `evseCountryCode + "*" + evseOperatorId` = Betreiberpräfix der EVSE-ID (`AT*EVN`, `AT*VIE`, `AT*EBM`, `AT*EST`, `AT*LAG`, `AT*SMA`, `AT*OMV`, `AT*ELA`). 913 Einträge (893 AT, 14 DE, je 1–2 CH/NL/IT/DK), Summe `stationCount` 15.999 (≈ Sites des Ladestellenverzeichnisses), Namen teils mit führendem Tab/Anführungszeichen (trimmen). `?id=4964&id=4953` filtert nach interner ID.

### `GET /api/providers` und `/api/providers/list`

`/providers?page=0&size=1000` (Parameter `filter`/`sort` laut Schema Pflicht, werden aber ohne Angabe akzeptiert): `{pageNo, pageSize, pageElements, totalPages, totalElements: 71, content[{emaProviderId, companyName, postCode, city}]}`. `/providers/list`: 73 × `{id, name, status: ACTIVE}`. `emaProviderId` ist der eMobility-Provider-Teil der Vertrags-ID (EMA-ID, z. B. `EVN`, `VNA` Wien Energie, `EBM` Burgenland Energie, `120` ÖAMTC); das Land steht nur am Produkt (`provider.emaCountryCode`). Anbieter ohne aktives Produkt (z. B. Arval, Carinsson) sind enthalten.

### `GET /api/documents/{uuid}/download`

200 `application/pdf`, `content-disposition: attachment; filename=Preisblatt_#Drive Smart.pdf`; ignoriert `Range`. Nicht als Fixture gespeichert.

## Beispiele

Antworten werden nicht ins Repository übernommen (Lizenz offen, siehe unten). Wer sie lokal braucht, ruft das Vergleichsskript mit `--cache <Verzeichnis>` auf; es legt `calculate.json` und je Produkt `product-<id>.json` ab.

EVN #Drive Smart, Haupttarif (netto):

```json
{ "name": "EVN Ladenetz", "model": "ZONE_POWER", "unit": "KWH",
  "variableFees": [ { "current": "AC", "to": 43,  "priceDayConsumptionEuro": 0.375 },
                    { "current": "DC", "to": 149, "priceDayConsumptionEuro": 0.392 },
                    { "current": "DC",            "priceDayConsumptionEuro": 0.408 } ],
  "blockingFees": [] }
```

→ brutto 0,45 / 0,47 / 0,49 €/kWh (= Preisblatt und kuratierter Katalog). Partnerladenetz: 0,45/0,533/0,575 netto → 0,54/0,64/0,69 brutto, Blockiergebühr 0,167 → 0,20 €/min ab Minute 241/121/61. Grundgebühr 4,13 → 4,96 (Preisblatt 4,95), Aktivierung 8,34 → 10,01 (Preisblatt 10).

Wien Energie Tanke ZEIT: `unit` `MIN`, `nightStart` 22:00, `nightEnd` 08:00, AC ≤ 11 kW 0,07 (Nacht 0,058) €/min netto → 0,084 (0,069 laut Preisblatt), AC > 11 kW 0,35 → 0,42, DC ≤ 50 0,35 → 0,42, DC ≤ 400 1,408 → 1,69. Grundgebühr 4,08 → 4,90, Aktivierung 8,25 → 9,90.

## Aktualisierung / Rate-Limit

- Kein Delta, kein `Last-Modified`/`ETag` auf der API. Änderungen erkennt man über neue Produkt-IDs bzw. geänderten Inhalt. **Jede neue Produktversion bekommt eine neue `id`** (IDs in 20er-Blöcken, z. B. 1746, 1766, 1786); alte IDs liefern 404 `PRODUCT_NOT_FOUND` – es gibt öffentlich keine Historie. Stabiler Schlüssel: `provider.emaCountryCode` + `provider.emaProviderId` + `brand.name` + `name`.
- `validFrom` reicht von 2023-09 bis 2026-10; Anbieter pflegen unterschiedlich aktuell (`docs/07`: ältere Daten bei Salzburg AG, da emobil, IKB, Lidl).
- Vollabruf: 1 × `calculate` + 43 × `products/{id}` (≈ 1,6 MB, davon die meisten Bytes Base64-Logos) + 1 × `operators` (125 kB); optional 43 × `products/{id}/stations` (bis 4 MB je Produkt). Empfehlung: Produkte täglich, Stationslisten wöchentlich, ≥ 2 s Abstand, ≤ 100 Requests je Lauf, User-Agent mit Kontakt.
- Kein Rate-Limit beobachtet; die Rechnung läuft serverseitig, also schonend bleiben.

## Rechtliches (robots.txt, Nutzungsbedingungen)

- **robots.txt** (`https://www.ladetarif.at/robots.txt`, Stand 17.3.2026): `User-agent: *` mit nur `Disallow: /admin/`; Sitemap mit `/`, `/home`, `/calculation/initial`. `/api/` ist nicht gesperrt. `admin.ladetarif.at/robots.txt` identisch.
- **Nutzungsbedingungen/AGB**: Weder ladetarif.at (App-Texte inkl. Impressum, Footer „Kontakt / Datenschutz / Impressum / Registrierte Anbieter“) noch admin.ladetarif.at noch die E-Control-Seiten [ladetarife](https://www.e-control.at/ladetarife), [Impressum](https://www.e-control.at/econtrol/links/impressum) und [Tarifkalkulator – Wichtige Hinweise](https://www.e-control.at/tarifkalkulator/wichtige-hinweise) enthalten Nutzungsbedingungen, eine Lizenz, ein Verbot automatisierter Abfragen oder Regeln zur Weiterverwendung. Die „AGB“-Felder der App sind die AGB der Anbieter (Uploads), nicht die der Website. Der Datenschutzlink `impressum#ladetarif` führt auf die allgemeinen Datenschutzhinweise der E-Control; einen Abschnitt zu ladetarif.at gibt es dort nicht.
- **Haftungsausschluss** ([e-control.at/ladetarife](https://www.e-control.at/ladetarife)): Inhalte nur zur Information; zur Richtigkeit: „Für die Eingabe der angezeigten Preisinformationen sind die jeweiligen Anbieter zuständig“. Betrieb im Auftrag des BMWET, aus Bundesmitteln finanziert.
- **Open Data**: kein Datensatz auf data.gv.at (Suche „ladetarif“, „tarifkalkulator“: 0 Treffer), keine CC-Lizenz. Das gesetzliche Offenheitsgebot des § 22a E-Control-Gesetz gilt dem Ladepunkteregister (ladestellen.at), nicht dem Tarifkalkulator.
- **Bewertung**:
  - *Automatisiertes Lesen der JSON-API*: **unklar, nicht verboten.** Keine Bedingungen, robots.txt erlaubt `/api/`, die API ist anonym und per OpenAPI öffentlich beschrieben, kein Captcha auf Lese-Endpunkten. Mit maßvoller Frequenz vertretbar (TDM nach § 42h Abs. 6 UrhG deckt die Analyse, `docs/08`).
  - *Weiterverbreitung der Tarifdaten (auch mit Quellenangabe)*: **unklar, nicht freigegeben.** Keine Lizenz; die E-Control kann als Datenbankherstellerin (§§ 76c ff. UrhG) Rechte an der Sammlung haben, wiederholte systematische Entnahme der ganzen Datenbank zur Re-Publikation ist das Risikoszenario. Einzelne Preise sind Fakten der Anbieter und selbst nicht geschützt. Als öffentliche Stelle unterliegt die E-Control dem Informationsweiterverwendungsgesetz (IWG 2022) – ein formeller Antrag auf Weiterverwendung (idealerweise CC BY 4.0) ist der saubere Weg.

## Abbildung auf chargeglobe

Heute bildet `scripts/tariffs/compare-ladetarif.py` die Produkte nur für den Abgleich ab: Haupttarif gegen Heimnetz-Zone, verglichen an festen Leistungspunkten (AC 3,7/11/22 kW, DC 50/100/150/300 kW), netto × 1,2 (außer bei Anbietern, die brutto eingetragen haben: SalzachSonne, IKB, da emobil), Blockiergebühr `timeLimit − 1` mit ±1 Minute Toleranz, Grundgebühr je Monat; geprüfte Abweichungen, bei denen der Katalog stimmt, stehen mit Grund im Skript (`EXPLAINED`). Ein späterer Adapter (nach Freigabe) als `ITariffSource` mit `Key = "ladetarif"` (`docs/04`, ADR-0013) liefert `TariffCatalogSnapshot` mit `EmpRecord` je Anbieter und `TariffRecord` je Produkt.

| ladetarif.at | chargeglobe |
|---|---|
| `provider.emaCountryCode`, `emaProviderId`, `brand.name` | `EmpRecord.Key` (Slug der Marke, z. B. `evn`, `wien-energie`; Zuordnung zu vorhandenen Katalog-EMPs über eine Tabelle im Adapter), `Name` = Marke, `Website` = `brand.website`, `Country` = `emaCountryCode` |
| Produkt `name` | `TariffRecord.Key` (Slug, ≥ 2 Zeichen) und `Name`; `Currency` `EUR`; `Markets` `["AT"]` |
| `validFrom` | `SourceDate` (Datum); `validTo` → nur Hinweis/Ende (der Import beendet fehlende Tarife ohnehin) |
| `uploads.priceSheet.uuid` | `SourceUrl` = `https://www.ladetarif.at/api/documents/{uuid}/download` (Fallback `website`) |
| – | `Confidence` = `MEDIUM` (von Anbietern netto eingetragen, umgerechnet; Rundung) |
| `fixedFees` Typ 1 (`BASIC_FEE`) | `MonthlyFee` brutto: `MONTH` ×1, `QUARTER` ÷3, `YEAR`/`TWELVE_MONTHS` ÷12; fehlt → `0` |
| `fixedFees` Typ 2 (`ACTIVATION_FEE`), Freitext-Gebühren am Produkt | `Prerequisites`/`Notes` („Aktivierung 10,01 € einmalig“) |
| `tariffs.main[]` | Zone `HOME` (bei Teilmenge eines Betreibers bzw. Sonderpreis einzelner Betreiber `OPERATOR`) |
| `tariffs.others[]` | Zone `PARTNER`, bzw. `ROAMING`, wenn der Name „Roaming“/„Fremdnetz“ lautet oder die Stationen nicht auf wenige Netze passen |
| Betreiber einer Zone | aus `/products/{id}/stations`: `evseStationId` über die E-Control-Inventardaten (`source_record` der Quelle `econtrol`, Location-ExternalId `Land*Betreiber*Station`) auf den Betreiber abbilden; `primary=true` → Haupttarif, `false` → Partnertarife. Betreiber, deren Stationen vollständig enthalten sind (Abgleich mit `OperatorDto.stationCount`), als `Operators` (`AT*EVN`); unvollständige als Hinweis. Alternativ `/operators/{id}/stations` (1 Request je Betreiber) |
| `variableFees[]` (`ZONE_POWER`) | je Eintrag ein `PriceRecord`: `Current` = `current`; nach `to` aufsteigend sortiert, `MinKw` = `to` des vorigen Eintrags derselben Stromart + 0,1 (Konvention `docs/04`, „über 11 kW → 11.1“), erster Eintrag `null`; `to` selbst ist die inklusive Obergrenze, fehlendes `to` = offen |
| `priceDayConsumptionEuro` | `PricePerKwh` = netto × 1,2 |
| `priceDayTimeEuro` (`MIN` = Minuten der Ladezeit) | `PricePerMinute` = netto × 1,2 (`KWH_AND_MIN`: beide Felder; `0` = kein Zeitpreis) |
| `nightStart`/`nightEnd` + `priceNight…` | zweiter `PriceRecord` derselben Stufe mit `StartTime`/`EndTime` (`"22:00"`/`"08:00"`); fehlt der Nachtpreis, gilt der Tagpreis |
| `blockingFees[]` | `BlockingFeePerMinute` = `priceDayTimeEuro` × 1,2; `BlockingAfterMinutes` = `timeLimit − 1` (EVN 241 = „ab der 241. Minute“ → 240, elinx 240 = „ab der 240. Minute“ → 239); zuordnen über die Leistungsklasse (`to` der Blockiergebühr kann von den Preisstufen abweichen, z. B. Burgenland DC Preis ≤ 50 kW, Blockierung ≤ 300 kW → Stufen an der Vereinigung der Grenzen teilen); `BlockingFrom` = `SESSION_START`; `MaxBlockingFee` gibt es nicht; Nachtwert `priceNightTimeEuro` → Zeitfenster |
| Tarif-`fixedFees` mit `interval: CHARGE` (Ansteck-/Zutrittsgebühr) | `SessionFee` × 1,2 auf allen Preisen der Zone |
| Tarif-`fixedFees` sonst, `discounts[]`, `features[]` mit Freitext | `Notes` (`BINDING_PERIOD`/`ONLY_EXISTING_CUSTOMERS` → `Prerequisites`) |
| Modelle `ZONE_CONSUMPTION`, `SCALE_*` (Preis nach Jahresverbrauch) | derzeit unbenutzt; Zone mit `Pricing = UNKNOWN` und Hinweis, bis ein Fall auftritt |
| Netto/Brutto | alle Beträge **netto**, `calculationTaxPercentage` = 20 → × 1,2; Rundung: €/kWh und Pauschalen auf 2 Stellen, €/min auf 3 Stellen; ±0,01 € Abweichung zum Preisblatt möglich (Netto auf 3 Stellen gerundet, z. B. 4,13 → 4,956 statt 4,95). Einige Anbieter haben brutto eingetragen (SalzachSonne, `docs/07`) – Plausibilitätsprüfung gegen Ad-hoc-Preise bzw. den kuratierten Katalog nötig |

Was ladetarif.at nicht abbildet (bleibt im kuratierten Katalog): Blockiergebühr ab Ladeende (EVN-Heimnetz), Deckel je Ladevorgang, Ausschlüsse (Tesla), Aufschläge auf den Betreiberpreis (`OPERATOR_AD_HOC`), Wochentage, Länder außerhalb AT, Zuordnung mehrerer Partnertarife zu Betreibern (da emobil: 10 Partnertarife, eine Stationsliste), Partnertarife ohne Betreiberauswahl (elinx, Wien Energie, Salzburg AG: `otherTariffsStationCount` 0).

## Entscheidung

- **Kein Adapter, keine Übernahme der Daten**, solange die E-Control das automatisierte Lesen und die Weiterverbreitung nicht schriftlich freigibt. Das Lesen ist nicht verboten, aber eine Lizenz für die Weitergabe fehlt; die API-Antworten von chargeglobe sind öffentlich, das wäre Weiterverbreitung.
- **Manueller Abgleich**: `python3 scripts/tariffs/compare-ladetarif.py` (≥ 2 s zwischen Requests, rund 45 Requests je Lauf, User-Agent mit Kontakt) zeigt Preisabweichungen, fehlende Produkte, neuere Stände auf ladetarif.at und Produkte ohne Zuordnung (Exitcode 1). Korrekturen trägt ein Mensch nach Prüfung des Preisblatts im Katalog ein (`docs/04`, Checkliste „Tarif oder Anbieter ergänzen“). Stand 17.10.2026: keine ungeklärte Abweichung.
- **Anfrage an die E-Control** (Betreiber, Kontakt laut Seite `support@ladestellen.at`, formell als Antrag nach IWG an `office@e-control.at`): (a) tägliches automatisiertes Lesen von `/api/calculate`, `/api/products/{id}`, `/api/products/{id}/stations`, `/api/operators`, (b) Weiterverbreitung in API und Web mit Quellenangabe, gewünschte Lizenz CC BY 4.0, (c) Veröffentlichung der Betreiberauswahl je Tarif (`TariffDto.operators`).
- **Nach Freigabe**: Adapter `ladetarif` als `ITariffSource` mit Fixtures und Replay-Test; der kuratierte Katalog hat Vorrang, wo er genauer ist (Blockiergebühr ab Ladeende, Deckel, Zeitfenster, Ausschlüsse); ladetarif.at ergänzt Produkte ohne Katalogeintrag und meldet Änderungen (neue Produkt-ID → Katalog prüfen).

## Offene Fragen

- Rechtsgrundlage und Nutzungsrechte: Erlaubt die E-Control automatisiertes Lesen und Weiterverbreitung, unter welcher Lizenz und mit welchem Attributionstext?
- Wie werden mehrere `main`-Tarife (Burgenland: kWh-Tarif, Min-Tarif, Roaming; SMATRICS Charge & Roam: eigenes Netz + Roaming; SalzachSonne mit/ohne Blockiergebühr) den Stationen zugeordnet? Öffentlich nicht ersichtlich; die Rechnung gewichtet laut Methodik mit der Ladepunktzahl je Tarifvariante.
- Bedeutet `timeLimit` einheitlich „ab Minute n“? Werte wie 60/180/240 (neben 61/181/241) deuten auf uneinheitliche Eingaben der Anbieter.
- Werden `validTo` und alte Versionen je öffentlich (für Preisverlauf)? Derzeit nicht.
- Stimmen die Partner-Stationslisten mit der Realität überein (EVN #Drive Smart: 9.929 Partnerstationen nur in AT, Preisblatt nennt auch DE/IT/HR)?
- Sind alle Werte netto? Für SalzachSonne nachweislich brutto eingetragen; eine Kennzeichnung fehlt im Schema.
