# Endpunkt-Notiz: Tesla – Supercharger-Standortseiten („findus“, Web)

| | |
|---|---|
| Analysiert am | 2026-10-17 |
| Analysiert von | chargeglobe |
| Methode | Öffentliche Webseiten im Browser (DevTools/DOM, `__NEXT_DATA__`), 7 Standortseiten mit ≥ 10 s Abstand; Abgleich mit der Live-DB (nur lesend, SQL) |
| Tier | 1 (öffentliche Web-Karte, inoffiziell) |
| Zugang | anonym, aber hinter Akamai-Bot-Schutz (curl/Server-Clients: 403) |
| ToS geprüft | `tesla.com/robots.txt`, `tesla.com/de_AT/legal/terms`, `tesla.com/de_at/legal/additional-resources` (Abschnitt „Geistiges Eigentum“), `tesla.com/legal/terms` (en-US), alle am 2026-10-17 |
| Lizenz / Attribution | keine Lizenz; „Alle Rechte vorbehalten“ |
| Rate-Limit beobachtet | keins ausgelöst; robots.txt verlangt `Crawl-delay: 10`; `Cache-Control: must-revalidate, max-age=300` |
| Capabilities | StaticInventory (Standort-Ebene) · AdHocPrice (je Standort, mit Zeitfenstern und Leistungsstufen) · EmpTariff (Tesla-/Mitgliedspreis) – **keine** EvseIds, **kein** Live-Status |

## Zweck

Klären, ob die öffentlichen Tesla-Supercharger-Seiten als Preisquelle für chargeglobe taugen: Wo stehen die Preise, wie sind sie aufgebaut (Fremdmarke vs. Tesla/Mitglied, kWh vs. Minute, Tageszeitfenster, Überlastungs-/Blockiergebühr), wie lassen sich Standorte auf unsere EVSEs aus der E-Control (`AT*TSL*…`) abbilden, ist automatisiertes Lesen erlaubt, und gibt es eine bessere amtliche Quelle.

## Endpunkt/URL

| Seite | URL | Inhalt |
|---|---|---|
| Liste Österreich | `GET https://www.tesla.com/de_AT/findus/list/superchargers/Austria` | alle Standorte mit Slug, Koordinaten, Adresse, IDs |
| Standortseite | `GET https://www.tesla.com/de_AT/findus/location/supercharger/<slug>` | Standortdetails und **alle Preisbücher** |
| robots.txt | `GET https://www.tesla.com/robots.txt` | siehe Rechtliches |

- Beide Seiten sind **Next.js-Seiten mit Server-Rendering**: Die Daten stehen vollständig im eingebetteten `<script id="__NEXT_DATA__" type="application/json">` (Build-ID am Prüftag `yMUFt7-0g9I8ShDlWQM0D`, Routen `/[locale]/findus/list/[functionType]/[country]` bzw. `/[locale]/findus/location/[functionType]/[locationSlug]`). Es gibt **keinen separaten JSON-/XHR-Aufruf** für Preise; `/findus/_next/data/<buildId>/….json` liefert ebenfalls nur HTML.
- **Verlinkung in der Liste:** `<a href="/de_AT/findus/location/supercharger/<slug>">`. Slugs sind uneinheitlich:
  - sprechend: `innsbrucksupercharger`, `stpoltensupercharger`, `WienDonaustadtatsupercharger`, `Parndorf422`, `EberstalzellSelfServeDemoTestDrive`, `SpittalanderDrau-SelfServeDemoDrive`
  - numerisch: `20812`, `30071`, `453335`, `449655` … – der numerische Slug ist meist die **trtId** (20812, 30071, 453335 geprüft), aber nicht immer (`Parndorf422` → trtId 29158, `stpoltensupercharger` → trtId 1680).
- **Anzahl:** `pageProps.data[]` enthält **55 Standorte**, im HTML sind **54 verlinkt** (ohne Link: trtId/Slug `440655`, 48.3942 / 13.4262, Innviertel). Die Liste ordnet `Seefeld in Tirol` fälschlich unter der Überschrift „Deutschland“ ein.
- Der in `docs/07` dokumentierte Endpunkt `https://www.tesla.com/cua-api/tesla-locations?…` antwortet am 2026-10-17 mit **404** und ist damit nicht mehr verfügbar. Die Guest-GraphQL-API (`/de_DE/charging/guest/api/graphql`) wurde **nicht** geprüft (interne POST-API).

## Zugang & Auth

- Kein Login, kein Key, keine Cookies nötig. Im Browser liefern die Seiten 200.
- **Akamai Bot Manager:** `curl` (auch mit Browser-User-Agent) und WebFetch bekommen auf `www.tesla.com` (sogar auf `robots.txt`) **403 „Access Denied“** (edgesuite-Referenz). Die Seiten laden obfuskierte Sensor-Skripte (`/0COgbP/eoH__/…`, POST 201). Ein normaler .NET-`HttpClient` aus dem Crawler-Container würde mit hoher Wahrscheinlichkeit ebenfalls blockiert; um durchzukommen, bräuchte es Browser-Automatisierung oder TLS-Fingerprint-Imitation, also das Umgehen einer Bot-Erkennung. Für chargeglobe kommt das nicht in Frage (CLAUDE.md Regel 6, `docs/05`).

## Format & Felder

### Listenseite – `props.pageProps.data[]`

| Feld | Beispiel | Nutzen |
|---|---|---|
| `location_url_slug` | `innsbrucksupercharger` | Slug für die Standortseite |
| `latitude` / `longitude` | 47.264877 / 11.428412 | Matching (= `supercharger_function.actual_*`) |
| `trtId`, `uuid` | `"1669"`, `"1443460"` | Tesla-IDs (trtId auch in GraphQL/Standortseite) |
| `supercharger_function` | `access_type: "Public"`, `open_to_non_tesla: true`, `site_status: "open"`, `charging_accessibility: "All Vehicles (Production)"`, `ownership_type` | Zugang/Status |
| `_source.key_data.address` | `address_street`, `address_number`, `postal_code`, `city`, `country`, `geo_point` | Adresse (Google-geokodiert, teils abweichende Hausnummer) |
| `_source.functions[]` | `name: "Charging_Supercharger"`, `opening_date` | Eröffnungsdatum |
| `_source.marketing` | `display_name`, `location_url_slug`, `roadside_assistance_number` | |

### Standortseite – `props.pageProps.formattedData.chargerDetails`

| Feld | Beispiel (Innsbruck) | Bedeutung |
|---|---|---|
| `name` / `commonSiteName` | `Innsbruck, Austria` / `Alphotel` | Name; **E-Control-Name = „Tesla Supercharger “ + `name`** |
| `locationGUID` | `49585dba-…` | stabile Tesla-ID |
| `availabilityProfile.siteId.id` / `.trtId.id` | 921 / 1669 | Tesla-Site-ID / trtId |
| `centroid` / `entryPoint` | 47.264877, 11.428412 / 47.264638, 11.428841 | Koordinaten (Zentrum / Zufahrt) |
| `address` | `street`, `streetNumber`, `postalCode`, `city`, `countryCode` | Adresse |
| `publicStallCount`, `maxPowerKw` | 4, 400 | öffentliche Stalls, Höchstleistung |
| `openToNonTeslas`, `openToPublic`, `accessHours.twentyFourSeven` | true, true, true | Zugang |
| `timeZone` | `Europe/Vienna` | Bezug der Zeitfenster |
| `amenities[]`, `isTrailerFriendly` | `AMENITIES_WIFI`, … | Ausstattung |
| `availabilityProfile.availabilityProfile.<wochentag>.congestionValue[24]` | 0.0–1.0, `createdTimestamp: 2026-10-15T00:00:00Z` | **historisches** Auslastungsprofil je Stunde, kein Live-Status |
| **`effectivePricebooks[]`** | siehe unten | **Maschinenlesbare Preise** |

Zusätzlich `locationData.supercharger_function` mit `num_charger_stalls`, `installed_full_power`, `project_status`.

### Preisbücher – `chargerDetails.effectivePricebooks[]`

Ein Eintrag je Kombination aus Fahrzeuggruppe, Gebührenart und Zeitfenster:

| Feld | Werte | Bedeutung |
|---|---|---|
| `vehicleMakeType` | `TSLA` · `NTSLA` · `MSP` | Tesla-Fahrzeug · **Fremdmarke ad hoc** · vermutlich „Mobility Service Provider“ (Roaming/B2B), Bedeutung nicht belegt |
| `isMemberPricebook` | `true` nur bei `TSLA` | Tesla-/Mitgliedspreis |
| `feeType` | `CHARGING` · `CONGESTION` (in AT kein `IDLE` gesehen) | Ladepreis · Überlastungsgebühr |
| `rateBase` | 0.49 | Preis je `uom` |
| `uom` | `kwh` · `min` | Abrechnungseinheit |
| `rateMinTier1..4` | 0.17 / 0.35 / 0.54 / 0.90 | Minutenpreise je Leistungsstufe (bei `uom: min`, `rateBase: 0`) |
| `rateTier1`, `rateTier2` | 0 oder = `rateMinTier1/2` | ältere Zweistufen-Felder |
| `currencyCode` | `EUR` | Währung |
| `isTou` | true/false | Tageszeitpreis („time of use“) |
| `startTime` / `endTime` | `"08:00"` / `"23:00"`, leer = ganztägig | Zeitfenster in `timeZone` (lokal) |
| `days` | `"0,1,2,3,4,5,6"`, leer = alle | Wochentage (Zählweise 0 = So? nicht belegt) |
| `minSiteOccupancy` / `maxSiteOccupancy` | null | auslastungsabhängige Preise (in AT ungenutzt) |

Regeln, die aus den Daten folgen:
- Je Fahrzeuggruppe gibt es einen **Basiseintrag ohne Zeitfenster** (`isTou: false`) und bei Tageszeitpreisen zusätzlich **TOU-Einträge**, die den Tag lückenlos abdecken. Für Fremdmarken zeigt die Seite nur die TOU-Fenster an, der Basiswert ist offenbar nur Rückfallwert.
- `CONGESTION` ist in allen geprüften Standorten 0,50 €/min, von Tesla als Höchstwert („bis zu“) angegeben; sie fällt nur an, wenn der Standort stark belegt ist **und** ein Ladestand-Schwellwert oder das Ladeende erreicht ist (Zahlungsbedingungen). Schwellwerte stehen nicht in den Daten.
- Minutenpreise mit Leistungsstufen: die Grenzen (0–60 / 60–100 / 100–180 / 180+ kW) stehen **nur** in den Anzeige-Labels, nicht in den Preisbuch-Feldern.

### Anzeigefelder (für Menschen, nicht zum Parsen)

- `formattedData.chargerPricing[]`: nur Fremdmarken (`chargingLabel: "Ladegebühren für Besitzer anderer Elektrofahrzeuge"`), `pricingDetails[] = {label, rate}` mit `label` = `"Base Rate"`, `"08:00 - 23:00"` oder `"0-60 kW"`, `rate` als lokalisierter Text in wechselndem Format (`"€0.49/kWh"` neben `"€ 0,39/kWh"`), `idleFee`.
- `formattedData.chargerHistogramData[]`: `chargingUom`, `isTou`, `currentRate`, `regionLabels[]` (Stundenindex ab 4 Uhr → Preis), `congestionFees` (Text), `congestionRate`, `parkingRate`, `showChargingMembershipUpsell`.

## Beispiele

Werte vom 2026-10-17 (Fremdmarke = `NTSLA`, Zeiten lokal Europe/Vienna, alle Tage). Spalte „E-Control“ = aktueller Ad-hoc-Preis derselben EVSEs in unserer DB (Quelle `econtrol`, letzter Register-Lauf 2026-10-16 23:26 Ortszeit).

| Standort | Slug (trtId / siteId) | Stalls / kW | Fremdmarke (`NTSLA`) | Tesla (`TSLA`) | `MSP` | E-Control |
|---|---|---|---|---|---|---|
| **Wien – Wienerberg**, Triester Str. 64, 1100 Wien („PhilsPlace“) | `20812` (20812 / 11009278) | 8 / 250 | **00–08 und 23–24: 0,39 €/kWh · 08–23: 0,56 €/kWh** (Basis 0,52) | 0,27 / 0,39 (Basis 0,36) | 0,33 / 0,47 (0,44) | 0,36 €/kWh, 8 EVSEs, Abstand 0 m |
| **Wiener Neudorf**, Blaue Lagune, 2351 („SCS Multiplex“) | `30071` (30071 / 11009957) | 12 / 250 | **00–08 und 23–24: 0,37 €/kWh · 08–23: 0,55 €/kWh** (Basis 0,50) | 0,27 / 0,40 (0,36) | 0,31 / 0,46 (0,42) | 0,50 €/kWh, 12 EVSEs, 47 m |
| **Parndorf**, Shoppingstraße 1, 7111 („Frunpark“) | `Parndorf422` (29158 / 6259) | 20 / 250 | **00–10 und 21–24: 0,49 €/kWh · 10–21: 0,55 €/kWh** (Basis 0,50) | 0,36 / 0,40 (0,37) | 0,41 / 0,45 (0,42) | 0,50 €/kWh, 20 EVSEs, 14 m |
| Eisenstadt, Ruster Str. 146, 7000 („Stop Shop“) | `453335` (453335 / 11029151) | 8 / 250 | 0,48 €/kWh ganztägig | 0,35 | 0,40 | 0,48 €/kWh, 8 EVSEs, 0 m |
| Innsbruck, Bernhard-Höfel-Str. 16 („Alphotel“) | `innsbrucksupercharger` (1669 / 921) | 4 / 400 | 0,49 €/kWh ganztägig | 0,36 | 0,41 | 4 EVSEs 0,49 €/kWh + 12 EVSEs (125 kW) 2,17/2,28 €/min |
| St. Pölten, Völklplatz 1 („Cityhotel“) | `stpoltensupercharger` (1680 / 1225) | 12 / 125 | **Minutenpreis nach Leistung: 0–60 kW 0,17 · 60–100 kW 0,35 · 100–180 kW 0,54 · 180+ kW 0,90 €/min** (für alle Gruppen gleich) | gleich | gleich | 12 EVSEs 0,90 €/min + 8 EVSEs 2,18 €/min |

Überall: Überlastungsgebühr bis zu 0,50 €/min, keine Blockiergebühr, Parken 0 €.

Gekürzter Rohauszug (Wienerberg, Fremdmarke):
```json
[
  {"feeType":"CHARGING","vehicleMakeType":"NTSLA","isMemberPricebook":false,"rateBase":0.52,"uom":"kwh","currencyCode":"EUR","isTou":false,"days":"","startTime":"","endTime":""},
  {"feeType":"CHARGING","vehicleMakeType":"NTSLA","isMemberPricebook":false,"rateBase":0.56,"uom":"kwh","currencyCode":"EUR","isTou":true,"days":"0,1,2,3,4,5,6","startTime":"08:00","endTime":"23:00"},
  {"feeType":"CHARGING","vehicleMakeType":"NTSLA","isMemberPricebook":false,"rateBase":0.39,"uom":"kwh","currencyCode":"EUR","isTou":true,"days":"0,1,2,3,4,5,6","startTime":"23:00","endTime":"00:00"},
  {"feeType":"CONGESTION","vehicleMakeType":"NTSLA","isMemberPricebook":false,"rateBase":0.5,"uom":"min","currencyCode":"EUR","isTou":false,"days":"","startTime":"","endTime":""}
]
```

## Aktualisierung/Rate-Limit

- `Cache-Control: must-revalidate, max-age=300`; Preise ändert Tesla laut Zahlungsbedingungen „von Zeit zu Zeit“, ohne Ankündigung.
- Ein vollständiger Durchlauf wären 1 Listenseite + 55 Standortseiten à ~75–80 KB HTML. Mit `Crawl-delay: 10` dauert das rund 10 Minuten; einmal täglich würde reichen, weil die Tageszeitfenster in den Daten stehen und nicht im Tagesverlauf abgefragt werden müssen.
- Bei den 7 abgerufenen Seiten (≥ 10 s Abstand, im Browser) trat kein 429 auf. Server-Clients ohne Browser blockiert Akamai schon beim ersten Request (403).

## Rechtliches (robots.txt, Nutzungsbedingungen)

**robots.txt** (`User-agent: *`): `/findus` ist **nicht gesperrt**. Gesperrt sind u. a. `/content/*`, `/new/*`, `/used/*`, `/demo/*`, `/search/`, Drupal-Pfade (`/node/`, `/admin/` …) und `?referral=`. Für alle Crawler gilt `Crawl-delay: 10`. Einen `Sitemap`-Eintrag gibt es nicht. Nach robots.txt wäre ein höflicher Crawler mit 10 s Abstand also zulässig.

**Nutzungsbedingungen Österreich** (`/de_AT/legal/terms`): Die Seite enthält nur Zahlungsbedingungen, Supercharger-Regeln und die Fair-Use-Policy, aber **keine allgemeinen Website-Nutzungsbedingungen** und keine ausdrückliche Klausel zu Crawlern oder Scraping.
- Die Zahlungsbedingungen verbieten Kunden, aus der „Tesla-Technologie“ (Hardware, Software, Systeme, Daten) Daten auszulesen oder zu extrahieren und daraus Konkurrenzprodukte zu entwickeln. Die englische Fassung sagt ausdrücklich „scrape“. Die Klausel zielt auf Fahrzeuge, Produkte und Kundenkonten. Ob die öffentliche Website darunterfällt, ist unklar.
- Unter „Geistiges Eigentum“ (`/de_at/legal/additional-resources`) stehen Texte, Bilder und deren Anordnung auf den Tesla-Websites unter Urheberrechtsschutz und dürfen „nicht für kommerzielle Zwecke oder zur Weitergabe kopiert“ werden; Lizenzen werden nicht eingeräumt. Eine offene Datenbank, die die Preise veröffentlicht, ist eine **Weitergabe**.
- Einzelne Preise sind als Tatsachen kaum urheberrechtlich geschützt. Eine systematische Übernahme **aller** Standortpreise kann aber das Schutzrecht des Datenbankherstellers berühren (§§ 76c ff. UrhG AT, EU-Datenbankrichtlinie). Das ist eine Einschätzung, keine Rechtsberatung.
- Der Akamai-Bot-Schutz ist eine technische Schutzmaßnahme. Ihn zu umgehen schließen wir unabhängig von der Rechtslage aus.

**Ergebnis: unklar, mit Tendenz zu unzulässig.** robots.txt erlaubt das Crawlen von `/findus`. Die IP-Klausel verbietet aber das Kopieren zur Weitergabe, die Daten-Extraktionsklausel ist mehrdeutig, und der Bot-Schutz signalisiert, dass Tesla automatisierte Zugriffe nicht will. Ohne schriftliche Erlaubnis von Tesla sollte chargeglobe die findus-Seiten **nicht automatisiert auslesen und nicht weiterveröffentlichen**.

## Abbildung auf chargeglobe

**Identität / Matching** (geprüft gegen die Live-DB am 2026-10-17, nur lesend):
- E-Control liefert **776 Tesla-EVSEs** (`AT*TSL*E002JKC` … `AT*TSL*ESGK63P`, Betreiber „Tesla Motors Austria GmbH“) an **55 Standorten**, alle `ACTIVE`. Das entspricht den 55 findus-Standorten.
- Koordinaten-Abgleich (`centroid` bzw. Listen-`latitude/longitude` gegen `location.geom` der Standorte mit `AT*TSL`-EVSEs): **53 von 55 innerhalb 100 m**, die übrigen 2 bei 107 m (`stgeorgensupercharger`) und 113 m (`Veldensupercharger`), **alle 55 innerhalb 250 m** und eindeutig. Zusätzlich passt der Name: E-Control `Tesla Supercharger Wien, Austria - Wienerberg` entspricht findus `name` = `Wien, Austria - Wienerberg`. Empfohlen: Betreiber `AT*TSL` + Radius 150 m + Namensvergleich, Rest über die Review-Queue (`docs/03`, Schritt 3).
- findus kennt **keine EVSE-IDs**, sondern nur Standort-IDs (`locationGUID`, `trtId`, `siteId`, Slug). Diese würden als `source_record` der Standort-Ebene abgelegt.
- **Die Zahl der Ladepunkte weicht ab:** Innsbruck hat bei findus 4 öffentliche Stalls (400 kW), in der E-Control 16 EVSEs (4 × 400 kW mit 0,49 €/kWh, 12 × 125 kW mit 2,17/2,28 €/min). St. Pölten hat 12 Stalls bei findus und 20 EVSEs in der E-Control. Ein Standortpreis darf deshalb nicht pauschal auf alle EVSEs eines Standorts geschrieben werden; zuordnen müsste man ihn über Leistung oder Zugänglichkeit.

**Preismodell:**
- `evse_price_current` / `price_observation` und `ProviderPrice` kennen je EVSE und Quelle nur **einen** Satz Preisbestandteile (kWh, Minute, Startgebühr, Blockiergebühr), aber **keine Zeitfenster und keine Leistungsstufen**. `ProviderPrice.TariffElements` (OCPI `TariffElement[]` mit `start_time`/`end_time`/`day_of_week`/`min_power`) existiert im Vertrag, wird aber nirgends gespeichert.
- `ITariffSource` (`src/ChargeGlobe.Providers.Abstractions/Tariffs.cs`): Zonen (`ZoneRecord`) wählen Netze oder Betreiber aus, **nicht einzelne Standorte**, und `PriceRecord` kennt nur `MinKw`, keine Zeitfenster. Eine OPERATOR-Zone je Tesla-Standort ist damit nicht abbildbar, weil alle 55 Standorte denselben Betreiber `AT*TSL` haben.
- E-Control-Ad-hoc-Preise der Tesla-EVSEs: für **alle 776 EVSEs vorhanden**. Sie sind aber ein **einzelner statischer Wert**: Seit der ersten Beobachtung (2026-10-14) gibt es keine Änderung, und beim Lauf um 23:26 Ortszeit standen Wiener Neudorf und Parndorf mit dem Basiswert 0,50 €/kWh im Register statt dem TOU-Preis von 0,37 bzw. 0,49 €/kWh. Wienerberg meldet 0,36 €/kWh, was dem **Tesla-Basispreis** entspricht und nicht dem Fremdmarkenpreis (0,39–0,56). St. Pölten meldet nur die höchste Minutenstufe (0,90 €/min), die Überlastungsgebühr erscheint gar nicht (Blockiergebühr 0).

## Empfehlung

**(c) Vorerst nur die E-Control-Ad-hoc-Preise verwenden und um einen Hinweis und einen Link ergänzen. Kein Tesla-Adapter, solange Tesla keine Erlaubnis gibt.**

Begründung:
1. **Recht und Compliance:** Die E-Control ist eine Tier-0-Quelle mit gesetzlicher Meldepflicht der Betreiber (Ladepunkt-Daten-Verordnung, BGBl. II Nr. 257/2024, und AFIR). findus ist bestenfalls unklar: Die IP-Klausel verbietet Kopieren zur Weitergabe, und der Bot-Schutz blockiert Server-Clients. Für (a) und (b) müsste der Crawler den Bot-Schutz umgehen, was gegen Regel 6 verstößt.
2. **Aufwand:** Bei (c) fällt kein neuer Code an, die Preise sind für alle 776 Tesla-EVSEs bereits da. (a) bräuchte Standort-Selektoren und Zeitfenster in `ZoneRecord`/`PriceRecord`. (b) bräuchte Zeitfenster und Leistungsstufen im Preismodell (z. B. `TariffElements` speichern) und eine Zuordnung Stall ↔ EVSE.
3. **Datenqualität:** findus ist fachlich klar besser (Tageszeitfenster, Leistungsstufen, Tesla-/Mitgliedspreise, Überlastungsgebühr). Die E-Control-Werte sind für TOU-Standorte nur ein Basiswert und an einzelnen Standorten falsch (Wienerberg, St. Pölten). Das rechtfertigt aber kein rechtlich unsicheres Scraping. Wir kennzeichnen es transparent: Bei `AT*TSL`-EVSEs zeigt chargeglobe in API und UI den Hinweis „Preis laut E-Control; Tesla verlangt je nach Tageszeit und Leistung abweichende Preise“ und verlinkt die passende findus-Seite. Ein Link ist keine Weitergabe der Daten. Das Mapping Standort → Slug (55 Einträge) pflegen wir einmalig von Hand oder halbautomatisch über Koordinaten.
4. **Später (b) statt (a):** Erlaubt Tesla die Nutzung schriftlich oder veröffentlicht es die Preise mit Zeitfenstern über einen offiziellen Kanal (NAP/DATEX II, OCPI), ist der passende Weg ein Ad-hoc-Preis je EVSE mit Quelle `tesla` und vollständigen `TariffElements`. Tesla ist dabei CPO-Preis, nicht EMP-Tarif. Den Tesla-/Mitgliedspreis könnte man dann als `emp`-Preis (`EmpKey = tesla`) daneben ablegen. (a) passt fachlich nicht: Tesla ist kein Roaming-Anbieter, und die Preise hängen am Standort, nicht am Netz.
5. **Begleitend:** E-Control (und Tesla) auf die Abweichungen hinweisen: Basiswert statt TOU-Preis, Tesla-Preis statt Fremdmarkenpreis in Wienerberg, nur die höchste Minutenstufe in St. Pölten, auffällige 2,17–2,28 €/min an 125-kW-EVSEs. Nach AFIR Art. 5 und 20 muss der Ad-hoc-Preis vor dem Ladevorgang transparent sein und als dynamisches Datum über den nationalen Zugangspunkt bereitstehen; die genauen Absätze sind nur über Sekundärquellen belegt, siehe Offene Fragen.

## Offene Fragen

- **Bedeutung von `MSP`:** Mobility-Service-Provider-Preis (Roaming/B2B) oder Mitgliedschaft für Fremdmarken? Der Wert liegt zwischen Tesla- und Fremdmarkenpreis, `isMemberPricebook` ist `false`. Den Fremdmarken-Mitgliedspreis zeigen die Daten nicht ausdrücklich (`showChargingMembershipUpsell: true`).
- **Zählweise von `days`** (0 = Sonntag?) und ob es in AT Standorte mit unterschiedlichen Wochentagsfenstern gibt. Geprüft wurden nur 6 Standorte, alle mit `0,1,2,3,4,5,6`.
- **E-Control-Datenmodell:** Kann die E-Control-API Zeitfenster und Leistungsstufen überhaupt transportieren? Unser `EControlMapper` kennt nur `priceCentKwh`, `priceCentMin`, `startFeeCent`, `blockingFeeCentMin` und `blockingFeeFromMinute`. Gibt es ab 14.04.2026 ein DATEX-II-Profil mit TOU (in DE vom BMV bereitgestellt) auch für Österreich, und wer ist der österreichische NAP für Ladepunktdaten (E-Control oder mobilitydata.gv.at)? Nicht geprüft.
- **AFIR-Wortlaut:** Art. 5 (Ad-hoc-Preis, Preisbestandteile, ab 50 kW kWh-Basis) und Art. 20 (statische/dynamische Daten inkl. Ad-hoc-Preis über den NAP ab 14.04.2025) wurden nur über Sekundärquellen und den Verordnungstitel belegt, nicht am EUR-Lex-Text. Offen ist auch, ob Teslas reine Minutenpreise an 125-kW-Standorten (St. Pölten) mit AFIR vereinbar sind.
- **Unstimmigkeiten in der E-Control:** Woher kommen die 12 bzw. 8 zusätzlichen EVSEs mit 2,17–2,28 €/min in Innsbruck und St. Pölten (ältere V2-Stalls nur für Tesla?), und warum meldet Wienerberg den Tesla-Preis?
- **Erlaubnis anfragen:** Schriftliche Anfrage an Tesla (Charging/Legal Österreich), ob eine offene Datenbank die findus-Preise mit Attribution übernehmen darf, eventuell über einen offiziellen Feed. Die Antwort entscheidet über (b).
- **Guest-GraphQL** (`GetSiteDetails`, Live-Stall-Verfügbarkeit) wurde bewusst nicht geprüft: interne POST-API hinter demselben Bot-Schutz, gleiche Rechtsfrage.

Quellen: [tesla.com/robots.txt](https://www.tesla.com/robots.txt) · [Tesla AT Bedingungen](https://www.tesla.com/de_AT/legal/terms) · [Tesla AT Zusätzliche Ressourcen / Geistiges Eigentum](https://www.tesla.com/de_at/legal/additional-resources) · [Tesla Terms (en-US)](https://www.tesla.com/legal/terms) · [findus Liste AT](https://www.tesla.com/de_AT/findus/list/superchargers/Austria) · [BGBl. II Nr. 257/2024](https://ris.bka.gv.at/Dokumente/BgblAuth/BGBLA_2024_II_257/BGBLA_2024_II_257.pdf) · [EUR-Lex AFIR 2023/1804 (konsolidiert)](https://eur-lex.europa.eu/eli/reg/2023/1804/2025-04-14/eng) · [eco-movement: AFIR Art. 20](https://www.eco-movement.com/afir-in-effect-13th-april-2024-article-20-data-provisions/) · [Ampeco: AFIR April 2025](https://www.ampeco.com/blog/april-2025-ev-charging-policy-update/) · [electrive: DATEX-II-Datenprofil (DE)](https://www.electrive.net/2025/08/26/live-daten-von-ladestationen-bmv-stellt-datex-ii-datenprofil-bereit/) · [vienna.at: erweitertes E-Ladestellenverzeichnis](https://www.vienna.at/start-fuer-erweitertes-e-ladestellenverzeichnis/9554213)
