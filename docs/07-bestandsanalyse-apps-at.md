# 07 – Bestandsanalyse Apps & Anbieter Österreich

Stand: 13. Oktober 2026. Fokus Wien, Niederösterreich, Oberösterreich, Burgenland. Grundlage: öffentliche Quellen und Open-Source-Repositories; **es wurden keine APKs heruntergeladen oder dekompiliert** – die Methodik dafür steht in [05](05-crawling-konzept.md#app-analyse-methodik-tier-2), die rechtliche Einordnung in [08](08-datenquellen-und-recht.md#8-rechtsrahmen-sachlich-keine-rechtsberatung). Kennzeichnung: **[V]** verifiziert, **[V-sek]** über Sekundärquelle/Open-Source-Code, **[U]** unsicher.

## Kernbefund

**Das E-Control-Ladestellenverzeichnis liefert bereits Inventar, EVSE-IDs, Live-Status je Ladepunkt und Ad-hoc-Preise für ganz Österreich** (Meldepflicht, 36.991 Ladepunkte, 99 % mit Ad-hoc-Preis; Details in 08). Betreiber-Apps und Web-Karten sind daher **nicht** für Inventar und Belegung nötig, sondern für:

1. **EMP-/App-Tarife** je Ladepunkt (das Verzeichnis kennt nur den Ad-hoc-Preis; ladetarif.at kennt Tarife, aber nicht je Ladepunkt),
2. **Zweitmeinung zum Status** (Latenz-/Qualitätsvergleich, Lücken bei Betreibern, die schlecht melden),
3. **Tesla**, dessen Stall-Status und Preise (Ad-hoc vs. Mitgliedschaft) am besten direkt von Tesla kommen.

Damit verschiebt sich die Priorität: statt „alle Apps crawlen“ → **E-Control als Backbone, dazu wenige, gut erreichbare Zweitquellen**.

## Übersichtstabelle

| Anbieter | Rolle | Backend-Plattform | Android-Package | Web-Karte | Live-Status | Preise sichtbar | Belegte Endpunkte / Repos | Priorität |
|---|---|---|---|---|---|---|---|---|
| **E-Control ladestellen.at** | amtl. Verzeichnis | eigene API | – | ladestellen.at/#/electric | ja, je Ladepunkt | Ad-hoc | `api.e-control.at/charge/1.0` [V-sek: HA-Integration] | **P0** |
| **EVN Drive** | CPO (> 3.850 LP) + EMP | unbekannt [U] | `com.evn.autoladen` [V] | autoladen.at (lt. AGB) [U] | App [U] | Tarif-PDFs | keine | P2 (Tarife) |
| **Wien Energie Tanke** | CPO (> 3.000 LP) + EMP | unbekannt (Next.js-Frontend) | `com.b2c.wienenergie.tanke` [V] | Finder auf wienenergie.at (Google-Maps-Embed) | App „freie Stationen“ [U] | Tarifseite | keine | P2 (Tarife) |
| **ÖAMTC ePower** | CPO (> 700) + EMP (~30.000 Roaming) | **e-vo CPMS** seit 2025 [V]; davor vermutl. has·to·be [U] | `com.htb.oeamtc` [U] | oeamtc.at/routenplaner/poi/laden | ja (App) | **vollständige Tarife** [V] | keine | **P1** (wichtigster EMP-Tarif) |
| **Verbund V-Go** | EMP („powered by SMATRICS“) | SMATRICS charVIS | `at.verbund.charge` [V] | – | App [U] | nur App | keine | P2 |
| **SMATRICS / SMATRICS EnBW** | CPO, EMP, White-Label (ÖBB, Verbund) | eigenes **charVIS**; Roaming Hubject + e-clearing [V] | `at.eprovider` [V] | ladenetz.smatrics.com (Google Maps) | nicht verifiziert | Tarifseite [V] | keine | P1 |
| **Octopus Electroverse** | Roaming-EMP ohne eigene LP | eigen | `energy.octopus.electricjuice.android` [V] | electroverse.com/map | App ja [V] | Betreiber-Roamingpreise | keine | P1 (Tarife vieler CPOs an einem Ort) |
| **Tesla** | CPO Supercharger | eigen | `com.teslamotors.tesla` [V] | tesla.com/findus | ja | App / GraphQL | evmap `TeslaGuestApi.kt` [V] | **P1** |
| **Linz AG StromMOBIL** | CPO (~1.755 LP / 680 Standorte) + EMP | unbekannt | `com.linzag.strommobil` [V] | „LadeAtlas“ nur hinter Login | unbekannt | Tarif-PDF [V] | keine | P2 |
| **Energie AG OÖ** | CPO + EMP (70 % da emobil) | Karte „energized by CIRRANTiC“ [V] | keine Lade-App gefunden | eag.viewer.cit-fusion.com | unbekannt | Preisblatt 2024 | keine | P2 |
| **Burgenland Energie** | CPO + EMP (TANKE-Karte) | unbekannt | keine gefunden | burgenlandenergie.at/ladestationen | unbekannt | kaum öffentlich | keine | P3 |
| **MOON Power** (Porsche Holding) | CPO (2.903 LP in AT) | **has·to·be / ChargePoint be.ENERGISED** [V] | `com.hastobe.moon` [V] | – | App | App | keine | P2 |
| **IONITY** | CPO HPC | eigen; Hubject + Gireve | `com.cleevio.ionity.android.app` [V] | ionity.eu [U] | App | Passport-Tarife | keine | P2 |
| **EnBW mobility+** | EMP in AT, CPO via SMATRICS EnBW | eigen | `com.enbw.ev` [V] | enbw.com Ladekarte | ja, je EVSE | ja | evmap `EnBwAvailabilityDetector.kt` [V] | P2 (inoffizieller Key, siehe unten) |
| **Shell Recharge** | CPO (HPC bei BILLA) + EMP | eigen | unklar [U] | ui-map.shellrecharge.com | nur Shell-eigene | – | `python-shellrecharge` (archiviert) | P3 |
| **Plugsurfing** | EMP | eigen | `com.xitaso.plugsurfing` [V] | – | App | App | keine | P3 |
| **Chargemap** | EMP + Community | eigen | `com.chargemap_beta.android` [V] | chargemap.com | ja | ja | keine | P3 |
| **Maingau** | EMP | chargecloud [V-sek] | `de.chargecloud.maingau` [V] | – | App | ja | evmap Chargecloud-Detector (Maingau-ID auskommentiert) | P3 |
| **Mer Austria** | CPO (100 Standorte) | Driivz [U] | `com.driivz.ewald` [V] | – | App | App/Ad-hoc | keine | P3 |
| **Lidl** | CPO (eigene Stationen) | – | Lidl Plus | – | – | 0,29 DC / 0,19 AC €/kWh (04/2026) [V] | – | P2 (Tarif manuell) |
| **Hofer** | Konsortium (Energie Steiermark, Energie Graz, EVN, Kelag, IKB, VKW, Linz AG) | je Betreiber | – | – | via Betreiber | via Betreiber | – | – |
| **Chargeprice** (Graz) | Tarif-Aggregator | eigen | Web-App | ja | nein | ja, kommerzielle API | `chargeprice-api-docs` [V] | Referenz, keine Datenquelle |
| **Ladefuchs** | Tarif-Aggregator (nur DE) | ← Chargeprice | `app.ladefuchs.android` [V] | – | nein | nur DE | Codeberg (Apache-2.0) | – |
| **GoingElectric** | Community-DB | eigen | – | goingelectric.de | nein | Community | `api.goingelectric.de` (Key, „nur Apps“) | – |
| **PlugShare** | Community-Karte | eigen | `com.xatori.Plugshare` [V] | plugshare.com | teils | Community | nur lizenzierte API | – |
| **evmap** | OSS-Client (Referenz) | – | `net.vonforst.evmap` [V] | – | ja (Detectors) | via Chargeprice | github.com/ev-map/EVMap | Referenz |

Außerhalb Scope, aber relevant für Phase 2: **da emobil** (Tirol, Operator-ID `AT*DAE` [V], 70 % Energie AG), **ella** (NÖ/OÖ/Bgld, ~500 Ladestellen, seit 02/2026 Teil von Wien Energie), Salzburg AG, KELAG, Energie Steiermark.

## Priorisierung der Anbindung

| Welle | Quelle | Was sie liefert | Aufwand | Zugangsweg |
|---|---|---|---|---|
| **P0** | E-Control-API | alles für AT: Inventar, EVSE-IDs, Status, Ad-hoc-Preis | gering (dokumentierte JSON-API) | API-Key, Terms |
| **P1** | Tesla (`cua-api` + Guest-GraphQL) | Stall-Status, Ad-hoc- und Mitgliedspreise je Standort | gering–mittel (Endpunkte in evmap dokumentiert, anonym) | anonym; ToS prüfen |
| **P1** | ÖAMTC ePower | wichtigster EMP-Tarif in AT (Mitglied/Nichtmitglied, Partnernetz, Blockiergebühren) – Tarif ist **netzweit**, nicht je Ladepunkt → manuell pflegbar; Roamingpartner-Liste als PDF | gering (Tarif-Editor) bis mittel (Web-Karte analysieren) | Tarif: öffentlich; Status: Web-Karte |
| **P1** | Electroverse | Roamingpreise vieler AT-CPOs an einem Ort → beste Quelle für „Preis je Ladepunkt je EMP“ | mittel (Web-Karte electroverse.com/map per DevTools analysieren) | anonym? [U] |
| **P1** | SMATRICS-Web-Karte | Status/Preise für SMATRICS, SMATRICS EnBW, ÖBB P&R, Verbund-Heimnetz | mittel | anonym? [U] |
| **P2** | EVN, Wien Energie, Linz AG, Energie AG, MOON, IONITY, Lidl | EMP-Tarife (netzweit, manuell oder Preisblatt-Scrape), Status als Zweitquelle | gering (Tarif) / mittel (Karten) | Web-Karten, PDFs |
| **P2** | EnBW Public API | Status je EVSE auch für AT – aber Zugang nur über den in der EnBW-Web-Karte eingebetteten Subscription-Key (inoffiziell) | gering technisch, **rechtlich heikel** | nicht ohne Freigabe |
| **P3** | Shell, Plugsurfing, Chargemap, Maingau, Mer, Burgenland Energie | Rand-EMPs/kleine CPOs | – | später |
| – | Chargeprice, GoingElectric, PlugShare | nur mit Lizenz/kommerziellem Key | – | nicht als Quelle; Chargeprice ggf. als Partner |

## Backend-Plattformen (ein Adapter deckt mehrere Anbieter)

| Plattform | Anbieter in AT | Hinweis |
|---|---|---|
| **has·to·be be.ENERGISED** (ChargePoint Austria, Radstadt) | MOON Power, Salzburg AG „Stromladen“ (`com.beenergised.chargemobile.salzburgag`), früher ÖAMTC (`com.htb.*`) | White-Label-Apps mit gleichem Backend → ein parametrisierter Adapter, falls Endpunkte anonym sind [U] |
| **e-vo eMobility CPMS** | ÖAMTC ePower (seit 2025) | – |
| **SMATRICS charVIS** | SMATRICS, SMATRICS EnBW, ÖBB (oebb.smatrics.com), Verbund V-Go | Web-Karten sind Whitelabel von instant.at (Google Maps), Assets auf `mi-static.smatrics-api.com` |
| **chargecloud** (Köln) | Maingau; evmap nutzt `app.chargecloud.de/emobility:ocpi/{operatorId}/app/2.0/locations` (OCPI-ähnlich, anonym) | derselbe Endpunkt-Typ für alle chargecloud-Kunden |
| **Driivz** | Mer (`com.driivz.ewald`), Shell „EV Driver“ (`com.driivz.shell`) [U] | – |
| **CIRRANTiC** | Energie AG Karte (`eag.viewer.cit-fusion.com`) | – |
| **Hubject intercharge** | Roaming-Hub der BEÖ-Versorger („ÖHUB“ seit 2017), SMATRICS, IONITY, has·to·be, EnBW | kein öffentlicher Datenzugang; EVSE-ID als gemeinsamer Schlüssel |

## Bekannte, öffentlich dokumentierte Endpunkte (aus Open-Source-Code)

Alle Angaben stammen aus den genannten Repositories, nicht aus eigener App-Analyse. Nutzung inoffizieller Endpunkte nur nach ToS-Prüfung (siehe 08).

### E-Control (offiziell, Key) – [prusuino/ha_austrian_charging_stations](https://github.com/prusuino/ha_austrian_charging_stations)
```
GET https://api.e-control.at/charge/1.0/search?latitude={lat}&longitude={lon}          # ~100 nächste Stationen inkl. points[] mit status
GET https://api.e-control.at/charge/1.0/search/stations?evseId={AT*XXX*E...}&maxResults=2
GET https://api.e-control.at/charge/1.0/countries/{cc}/operators/{op}/stations/{stationId}
GET https://api.e-control.at/charge/1.0/countries/{cc}/operators/{op}/stations/{stationId}/points
Header: apiKey: <key>   Referer: https://<registrierte-domain>/
Status-Rohwerte: AVAILABLE | CHARGING | RESERVED | BLOCKED | INOPERATIVE | OUTOFORDER
Preis: priceCentKwh / priceInCentPerKwh, freeOfCharge
```

### Tesla (anonym, inoffiziell) – [ev-map/EVMap `api/availability/tesla`](https://github.com/ev-map/EVMap/tree/master/app/src/main/java/net/vonforst/evmap/api/availability)
```
GET  https://www.tesla.com/cua-api/tesla-locations?translate=en_US&usetrt=true     # alle Standorte weltweit (cachen!)
GET  https://www.tesla.com/cua-api/tesla-location?id={locationId}                   # liefert trtId
POST https://www.tesla.com/de_DE/charging/guest/api/graphql                         # Operation GetSiteDetails
     variables: siteId.byTrtId, chargingExperience: ADHOC|GUEST, programType: PTSCH, locale: de-DE
     → Stall-Verfügbarkeit, publicStallCount, pricing.userRates (Ad-hoc / Mitglied)
```
Die Owner-API (OAuth `auth.tesla.com`, GraphQL `akamai-apigateway-charging-ownership.tesla.com`) braucht einen Tesla-Account und wird **nicht** verwendet.

### EnBW (anonym, aber mit eingebettetem Web-Key – inoffiziell) – evmap `EnBwAvailabilityDetector.kt`
```
GET https://api.emp.emob-enbw.com/emobility-public-api/api/v1/chargestations?grouping=false&fromLat=&toLat=&fromLon=&toLon=
GET https://api.emp.emob-enbw.com/emobility-public-api/api/v1/chargestations/{id}
Header: Ocp-Apim-Subscription-Key (aus enbw.com-Web-Karte), Origin/Referer https://www.enbw.com
Status: AVAILABLE | OCCUPIED | UNAVAILABLE | OUT_OF_SERVICE | UNSPECIFIED, inkl. EVSE-ID; Österreich ist abgedeckt
```
→ Wegen des Keys nur mit Freigabe von EnBW einsetzen.

### chargecloud (anonym, OCPI-ähnlich) – evmap `ChargecloudAvailabilityDetector.kt`
```
GET https://app.chargecloud.de/emobility:ocpi/{operatorId}/app/2.0/locations?latitude=&longitude=&radius=
```

### Schweiz (für Phase 3, offiziell) – siehe 08: BFE OCPI-API ab 11/2026.

## Detailprofile

### EVN Drive (EVN AG, NÖ)
- Größter CPO Österreichs: > 3.850 eigene Ladepunkte, + 600 bei XXXLutz; Schnelllader bei Spar/Hofer/ASFINAG Roggendorf; Roamingpartner von ÖAMTC, Salzburg AG, DKV. [V]
- App „EVN Drive“ (vormals „Autoladen 2.0“), AGB nennen autoladen.at als Stationsliste [U]. Backend und Operator-ID nicht belegt (vermutlich `AT*EVN`, über E-Control-Daten verifizierbar).
- Tarife 07/2026 [V, PDF]: Drive Easy 0 €/Monat, 0,54 €/kWh AC/DC/HPC im EVN-Netz; Drive Smart 4,95 €/Monat, 0,45 €/kWh AC; Standgebühr ab 30 min nach Ladeende 0,50 €/min (Deckel 30/60 €). Ad-hoc „Charge & Pay“ (03/2025: 0,5417 AC / 0,6250 DC).
- **Für chargeglobe:** Tarif netzweit → Tarif-Editor; Status via E-Control; Web-Karte später per DevTools prüfen.

### Wien Energie Tanke
- > 3.000 öffentliche Ladestellen in/um Wien; Stadt Wien ~1.200 22-kW-Punkte im Straßenraum. Übernahme von ella (02/2026). Partner: SMATRICS, IONITY, ENIO. [V]
- App `com.b2c.wienenergie.tanke`; Web-Finder ist Google-Maps-Embed hinter Consent (Next.js) – keine eigenen JSON-Endpunkte im HTML sichtbar, Netzwerkanalyse nötig.
- Tarife ab 04/2026: GO/SMART/PRO (kWh), ZEIT-Tarif 4,90 €/Monat minutenbasiert, Direct Payment 0,56 €/kWh, Standzeitzuschlag 0,05 €/min (City/Spot ab 5 h, DC ab 3 h, Deckel 25 €, nachts frei). [V]
- Operator-ID laut E-Control-Beispiel `VIE` [V].

### ÖAMTC ePower
- > 700 eigene Ladepunkte, ~30.000 per Roaming; Backend-Wechsel auf e-vo CPMS 2025 [V].
- Tarife ab 01.07.2026 [V]: eigenes Netz 0,44/0,54/0,64 €/kWh (AC / DC ≤ 149 kW / HPC), Partnernetz AT 0,54/0,64/0,74, Direct Payment 0,49/0,59/0,69, Blockiergebühr 0,10 €/min nach 240 (AC) / 120 (DC) min. Mitglieder-/Nichtmitgliederpreise beachten.
- Roamingpartner-PDF (07/2026) listet u. a. ALDI Süd, Burgenland Energie, Compleo, da emobil, ELLA, Energie AG, EVN, Kaufland, Lidl, Linz AG, MAINGAU, MER, Monta (`AT*LNK`), OMV, Salzburg AG, Shell, SMATRICS, Tulln Energie, Wien Energie → die Liste definiert, an welchen EVSEs der ÖAMTC-Partnertarif gilt (Assignment über Operator-ID, nicht je EVSE).
- Web-Karte oeamtc.at/routenplaner/poi/laden – Kandidat für Statusanalyse.

### Verbund V-Go / SMATRICS / SMATRICS EnBW
- V-Go (`at.verbund.charge`) ist eine EMP-App „powered by SMATRICS“; Tarife BASIC (0 €) / PREMIUM (11,99 €/Monat ab 02/2027) [U]. kWh-Preise nur in der App.
- SMATRICS (JV Verbund/OMV, EnBW 25,1 %): Backend charVIS, White-Label für ÖBB (233 P&R-Ladepunkte, 0,30 €/kWh ab 05/2026) und Verbund; HPC-JV SMATRICS EnBW (> 300 DC-Punkte, Standortpartner REWE/BILLA). [V]
- Tarife 10/2024 [V]: Charge Simple 0 €: 0,55 AC / 0,65 DC; Charge & Roam 4,90 €; Roam+ 8,90 €: 0,50/0,55, Roaming 0,69/0,79; Blockiergebühr AC 0,10 €/min ab 481 min, DC 0,20 €/min ab 61/181 min.
- Web-Karten: ladenetz.smatrics.com, oebb.smatrics.com (Google-Maps-Whitelabel).

### Octopus Electroverse
- Roaming-EMP ohne eigene Infrastruktur; AT-Partner u. a. EVN, Wien Energie, Energie AG, Burgenland Energie, SMATRICS, IONITY, Shell, OMV, ÖAMTC, Lidl, MOON. Gibt Betreiber-Roamingpreise ohne Aufschlag weiter; App zeigt Live-Verfügbarkeit [V]. Web-Karte electroverse.com/map (Next.js) – Live/Preise im Web nicht verifiziert.
- **Für chargeglobe:** wenn die Web-Karte Preise je Ladepunkt liefert, ist das die effizienteste Quelle für EMP-Preise über viele CPOs.

### Tesla
- 2022: 24–28 Standorte / ~260 Stalls in AT, Teil davon für Fremdmarken; aktuelle Zahl [U]. Fremdmarken-Mitgliedschaft 9,99 €/Monat bzw. 100 €/Jahr; kWh-Preise standort- und zeitabhängig (Ad-hoc vs. Mitglied) nur per App/GraphQL. Stall-Verfügbarkeit auch in Google Maps. [V]
- Tesla-EVSE-IDs sind im E-Control-Verzeichnis zu erwarten (Meldepflicht), in Teslas eigenen Daten aber nicht → Matching per Operator + Geo + Stallanzahl (siehe 03).

### Linz AG StromMOBIL
- ~1.755 Ladepunkte an 680 Standorten (2025). Karte „LadeAtlas“ nur hinter Login. Tarife 02/2025 [V]: EASY POWER 0 €: 0,54 AC / 0,64 DC / 0,89 DC > 200 kW; MORE POWER 7,50 €: 0,39/0,49/0,74; Infrastrukturbelegung 0,12 €/min ab 241 (AC) / 91 (DC) min. Operator-ID `AT*LAG` [U].

### Energie AG Oberösterreich
- CPO + EMP, 70 % an da emobil. Karte auf CIRRANTiC-Plattform (`eag.viewer.cit-fusion.com`). Preisblatt 08/2024 [U, evtl. veraltet]: Energie Mobil 0 €: 0,54 AC / 0,65 DC; Mobil+ 6 €: 0,49/0,60. Operator-ID `AT*ENA` [U].

### Burgenland Energie
- TANKE-E-Mobilitätskarte; eigene Stationen nach kWh, Roaming minutenbasiert [V]. Preise kaum öffentlich. Operator-ID `AT*EBM` [U]. Keine Lade-App gefunden. → Tarife manuell, Status via E-Control.

### MOON Power, IONITY, EnBW, Shell, Mer, Maingau, Plugsurfing, Chargemap
- MOON: 2.903 LP in AT, be.ENERGISED-App (Entwickler Chargepoint Austria GmbH) [V].
- IONITY: Operator-ID seit 09/2023 `IOY` (vorher `ION`) → `AT*IOY*E…` [V]; Tarife DE 11/2025: Ad-hoc 0,75, Passport Motion 5,99 €/0,49, Power 11,99 €/0,39 [U].
- EnBW mobility+: EMP in AT; Public-API-Status siehe oben.
- Shell Recharge: HPC bei BILLA (≥ 100 Standorte); Drittbetreiber aus Shell-Karte entfernt, API geändert (python-shellrecharge archiviert) [V].
- Mer Austria: 100 Standorte (05/2026), McDonald's/MPreis, Driivz-App [U].
- Maingau: chargecloud-Backend; Dreistufenmodell ab 07/2026 [V]. Plugsurfing/Chargemap: AT-Preise nur teilweise öffentlich.

### Handel & Verkehr
- **Lidl** betreibt selbst (Lidl-Plus-App), 0,29 DC / 0,19 AC €/kWh (04/2026) [V] – günstigster AT-Tarif, manuell pflegen.
- **Hofer**: Konsortium je Bundesland (NÖ/Wien: EVN bzw. Wien Energie [U]).
- **Spar**: lokaler Versorger (Wien: Wien Energie, NÖ: EVN).
- **BILLA/REWE**: SMATRICS EnBW + Shell HPC.
- **ÖBB P&R**: SMATRICS; **ASFINAG**: Konzessionsvergabe für 21 Rastplätze läuft (Betrieb ab 2027); Roggendorf A1 mit EVN.

## Tarifaggregatoren (Referenz, nicht als Datenquelle)

- **Chargeprice** (Graz): kommerzielle API (`/v1/charge_prices`, `/v1/tariffs`, `/v1/tariff_details`, `/v1/charging_stations`, `/v1/companies`, Header `API-Key`); Datenbasis nicht dokumentiert, Indizien auf Community-/manuelle Pflege; nur für CH ein Open-Data-Endpunkt. Potenzieller Partner, kein Scraping-Ziel.
- **Ladefuchs**: nur DE, Daten von Chargeprice.
- **ladetarif.at** (E-Control): einzige offizielle AT-Tarifdatenbank, von Anbietern selbst gepflegt (22 Anbieter, 44 Produkte); keine dokumentierte API; Nutzung mit E-Control klären – wäre die beste Quelle für netzweite EMP-Tarife.

## Nächste Schritte aus der Bestandsanalyse

1. E-Control-API-Key beantragen, Swagger lesen, Bulk-Endpunkt und Status-Aktualisierungsrate klären, Operator-IDs aller Scope-Betreiber aus den Daten ziehen.
2. Tarif-Editor mit den oben belegten Tarifen befüllen (EVN, Wien Energie, ÖAMTC, SMATRICS, Linz AG, Energie AG, Lidl, Tesla-Mitgliedschaft); Zuordnung über Operator-ID + Leistungsklasse.
3. Tesla-Adapter auf Basis der evmap-Endpunkte (cua-api + Guest-GraphQL) mit Fixtures.
4. Netzwerkanalyse (DevTools) der Web-Karten: Electroverse, ÖAMTC, SMATRICS/ÖBB, Energie AG (CIRRANTiC), EnBW – jeweils Endpunkt-Notiz nach Vorlage in 05, Capabilities, ToS.
5. Erst danach entscheiden, ob App-Analysen (Tier 2) überhaupt nötig sind; Kandidaten wären EVN Drive und Wien Energie Tanke, falls deren Web-Karten keine Preise je Ladepunkt liefern.
