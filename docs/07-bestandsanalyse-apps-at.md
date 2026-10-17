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
| **EVN Drive** | CPO (> 3.850 LP) + EMP | e-vo-Portal `emobilportal.at` [V] | `com.evn.autoladen` [V] | autoladen.at (lt. AGB) [U] | ja, je EVSE, anonym [V] | Tarif-PDFs | [Endpunkt-Notiz](endpunkte/evn-drive.md) | **P1** (Status aller Betreiber im Portal) |
| **Wien Energie Tanke** | CPO (> 3.000 LP) + EMP | unbekannt (Next.js-Frontend) | `com.b2c.wienenergie.tanke` [V] | Finder auf wienenergie.at (Google-Maps-Embed) | App „freie Stationen“ [U] | Tarifseite | keine | P2 (Tarife) |
| **ÖAMTC ePower** | CPO (> 700) + EMP (~30.000 Roaming) | **e-vo CPMS** seit 2025 [V]; davor vermutl. has·to·be [U] | `at.oeamtc.epower` [V] | oeamtc.at/routenplaner/poi/laden | Stationspreise ja, Live-Status nur mit Konto [V] | **vollständige Tarife** [V] | [Endpunkt-Notiz](endpunkte/oeamtc-epower.md) | **P1** (wichtigster EMP-Tarif) |
| **Verbund V-Go** | EMP („powered by SMATRICS“) | SMATRICS charVIS | `at.verbund.charge` [V] | – | ja, je EVSE, mit App-Key [V] | nur App | [Endpunkt-Notiz](endpunkte/vgo-smatrics.md) | **P1** (Inventar und Status, 91.600 EVSEs, Zugang klären) |
| **SMATRICS / SMATRICS EnBW** | CPO, EMP, White-Label (ÖBB, Verbund) | eigenes **charVIS**; Roaming Hubject + e-clearing [V] | `at.eprovider` [V] | ladenetz.smatrics.com (Google Maps) | nicht verifiziert | Tarifseite [V] | keine | P1 |
| **Octopus Electroverse** | Roaming-EMP ohne eigene LP | eigen | `energy.octopus.electricjuice.android` [V] | electroverse.com/map | App ja [V], nur mit Konto | Betreiber-Roamingpreise | [Endpunkt-Notiz](endpunkte/electroverse.md) | P1 (Tarife vieler CPOs an einem Ort) |
| **Tesla** | CPO Supercharger | eigen | `com.teslamotors.tesla` [V] | tesla.com/findus | ja | App / GraphQL | evmap `TeslaGuestApi.kt` [V] | **P1** |
| **Linz AG StromMOBIL** | CPO (~1.755 LP / 680 Standorte) + EMP | unbekannt | `com.linzag.strommobil` [V] | „LadeAtlas“ nur hinter Login | unbekannt | Tarif-PDF [V] | keine | P2 |
| **Energie AG OÖ** | CPO + EMP (70 % da emobil) | Karte „energized by CIRRANTiC“ [V] | keine Lade-App gefunden | eag.viewer.cit-fusion.com | unbekannt | Preisblatt 2024 | keine | P2 |
| **Burgenland Energie** | CPO + EMP (TANKE-Karte) | unbekannt | keine gefunden | burgenlandenergie.at/ladestationen | unbekannt | kaum öffentlich | keine | P3 |
| **MOON Power** (Porsche Holding) | CPO (2.903 LP in AT) | **has·to·be / ChargePoint be.ENERGISED** [V] | `com.hastobe.moon` [V] | – | App | App | keine | P2 |
| **IONITY** | CPO HPC | eigen; Hubject + Gireve | `com.cleevio.ionity.android.app` [V] | ionity.eu [U] | Ad-hoc-Preis und Status je Stecker, anonym [V] | Passport-Tarife | [Endpunkt-Notiz](endpunkte/ionity.md) | P2 |
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
| – | Tesla (findus-Standortseiten) | Ad-hoc- und Mitgliedspreise je Standort mit Tageszeitfenstern | – | **nicht genutzt**: Bot-Schutz, Website-Bedingungen ([Endpunkt-Notiz](endpunkte/tesla-findus.md)); Preise aus der E-Control |
| **P1** | ÖAMTC ePower | wichtigster EMP-Tarif in AT (Mitglied/Nichtmitglied, Partnernetz, Blockiergebühren) – Tarif ist **netzweit**, nicht je Ladepunkt → Katalogdatei mit Partnerliste als Zone; Roamingpartner-Liste als PDF | gering (Katalog) bis mittel (Web-Karte analysieren) | Tarif: öffentlich; Status: Web-Karte |
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

Die Angaben stammen aus den genannten Repositories bzw. aus eigener App-Analyse (letzter Abschnitt). Nutzung inoffizieller Endpunkte nur nach ToS-Prüfung (siehe 08).

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

### Tesla – öffentliche Standortseiten ([Endpunkt-Notiz](endpunkte/tesla-findus.md))
```
GET  https://www.tesla.com/de_AT/findus/list/superchargers/Austria                  # 55 Standorte mit Koordinaten (__NEXT_DATA__)
GET  https://www.tesla.com/de_AT/findus/location/supercharger/<slug>                # effectivePricebooks: Fremdmarke/Tesla, Tageszeitfenster, Minutenstufen
```
Der frühere Endpunkt `cua-api/tesla-locations` antwortet nicht mehr (404). Die Seiten liegen hinter einem Bot-Schutz (Server-Clients bekommen 403), die Website-Bedingungen verbieten das Kopieren zur Weitergabe; chargeglobe liest sie deshalb **nicht** automatisiert. Die Owner-API und die Guest-GraphQL-API werden ebenfalls nicht verwendet.

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

### Aus der App-Analyse (Tier 2, 17.10.2026) – Details in den Endpunkt-Notizen
Aus dekompilierten APKs belegt und mit curl für Graz nachgestellt (Replay-Skripte unter [`endpunkte/replay`](endpunkte/replay/README.md)). Alle Quellen: ToS noch nicht geprüft.

| App | Endpunkt (Karte) | Zugang | Ergebnis Graz (47.05–47.09 N, 15.40–15.47 E) |
|---|---|---|---|
| [EVN Drive](endpunkte/evn-drive.md) | `GET emobilportal.at/api/v1/chargeStations/chargeAtlas?topLeftLat&topLeftLong&bottomRightLat&bottomRightLong&zoom` | anonym | 146 Stationen, 315 Ladepunkte mit Status |
| [Verbund V-Go](endpunkte/vgo-smatrics.md) | `POST poi-service.smatrics-api.com/b2c/locations/reduced/bounding-box` | statischer App-Key (Bearer) | 101 Standorte, 256 EVSEs mit Status; Gesamtbestand 29.527 Standorte / 91.617 EVSEs in 2,6 MB |
| [ÖAMTC ePower](endpunkte/oeamtc-epower.md) | `POST www.oeamtc.at/api/mob/oeamtc/gis-efuel/efuel/search?count&start` | statischer Basic-Client der App | 133 Stationen mit Preis je kWh, Status `UNKNOWN` (Live nur mit Konto) |
| [IONITY](endpunkte/ionity.md) | `GET adhoc-bff.ionity.cloud/api/v1/location/static`, `GET …/v3/location/{uuid}` | anonym + Header `x-adhoc-platform`, `x-adhoc-app-feature-version` | 902 Standorte; Ad-hoc-Preis und Status je Stecker, keine EVSE-IDs |
| [Octopus Electroverse](endpunkte/electroverse.md) | MVT-Kacheln `api.electroverse.com/rest/locations/tiles/elastic/{z}/{x}/{y}?operator_groups[]=` und GraphQL | Konto nötig | nicht nachstellbar |

### Schweiz (für Phase 3, offiziell) – siehe 08: BFE OCPI-API ab 11/2026.

## Detailprofile

### EVN Drive (EVN AG, NÖ)
- Größter CPO Österreichs: > 3.850 eigene Ladepunkte, + 600 bei XXXLutz; Schnelllader bei Spar/Hofer/ASFINAG Roggendorf; Roamingpartner von ÖAMTC, Salzburg AG, DKV. [V]
- App „EVN Drive“ (vormals „Autoladen 2.0“), AGB nennen autoladen.at als Stationsliste [U]. Backend ist das e-mobil-Portal `emobilportal.at` (Operator-Token `AT*EVN`); die App-Karte zeigt alle Ladepunkte des Portals (Details in der [Endpunkt-Notiz](endpunkte/evn-drive.md)).
- Tarife 07/2026 [V, PDF]: Drive Easy 0 €/Monat, 0,54 €/kWh AC/DC/HPC im EVN-Netz; Drive Smart 4,95 €/Monat, 0,45 €/kWh AC; Standgebühr ab 30 min nach Ladeende 0,50 €/min (Deckel 30/60 €). Ad-hoc „Charge & Pay“ (03/2025: 0,5417 AC / 0,6250 DC).
- **Für chargeglobe:** Tarif netzweit → Tarifkatalog (`catalog/at/evn.json`); Status via E-Control, als Zweitquelle der anonyme Karten-Endpunkt des Portals.

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
- 10/2026: 55 Supercharger-Standorte in Österreich, im E-Control-Verzeichnis 776 EVSEs (`AT*TSL*…`) an denselben 55 Standorten; 53 davon liegen innerhalb von 100 m der findus-Koordinaten, alle innerhalb von 250 m. [V]
- Preise je Standort und Tageszeit für Fremdmarken (z. B. Wien-Wienerberg 0,39 €/kWh nachts, 0,56 €/kWh 8–23 Uhr), an älteren Standorten je Minute nach Leistungsstufe; Überlastungsgebühr bis 0,50 €/min; Mitgliedschaft für Fremdmarken nur in der App. [V]
- **Entscheidung:** chargeglobe nutzt für Tesla die Ad-hoc-Preise der E-Control (Meldepflicht, für alle 776 EVSEs vorhanden). Sie sind ein fester Wert ohne Zeitfenster und weichen an einzelnen Standorten vom Fremdmarkenpreis ab (Wienerberg meldet den Tesla-Preis, St. Pölten nur die höchste Minutenstufe). Keine Ladekarte eines österreichischen Anbieters listet Supercharger als Roaming-Partner; im Tarifkatalog sind sie in den offenen Roamingzonen ausgeschlossen.

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

## Tarife der Anbieter (Katalog 10/2026)

Erhoben am 17.10.2026 aus Preisblättern und Tarifseiten, abgelegt im kuratierten Tarifkatalog (`src/ChargeGlobe.Providers.TariffCatalog/catalog/at/`, Modell [ADR-0013](adr/0013-ladetarife-netze-zonen-katalog.md)); je Tarif stehen dort Quelle, Stand und Vertrauensstufe.

| Muster | Anbieter | Abbildung |
|---|---|---|
| Heimnetz + ein Preis für alle anderen Stationen | EVN #Drive Smart („Partnerladenetz“ = Roaming in AT, DE, IT, HR, SI), Wien Energie Tanke, Energie Steiermark, SMATRICS Charge & Roam, VERBUND V-Go, OMV eMotion, Elli Premium, GENOL G+ Card (Roaming nur bei 17 Partnernetzen) | `HOME` + `ROAMING`-Zone, die Länder des Roamings als `countries` |
| ein Preis überall | LINZ AG StromMOBIL (eigene Stationen und alle gelisteten Roaming-Partner), EVN eMobil (Bestand) | `HOME` + `ROAMING` mit gleichem Preis, nicht gelistete Betreiber ausgeschlossen |
| benannte Partnerliste, kein weiteres Roaming | ÖAMTC ePower (27 Netze inkl. IONITY, Mer, Lidl), Kelag ePlus/eMax, IKB, Energie AG (Partner je Minute) | `HOME` + `PARTNER` mit Netzliste |
| Roamingpreis je Betreiber | da emobil, TIWAG mobil | je Preisgruppe eine `ROAMING`-Zone mit Netzen; nicht gelistete Betreiber nicht nutzbar |
| Roaming je Minute, Ausnahmen je kWh | Salzburg AG (PLUS-Partner IONITY, Shell, Mer, OMV je kWh), Burgenland Energie (benannte Partner je Minute) | benannte `ROAMING`-Zone vor der offenen |
| Preis des Betreibers weitergereicht | Abrechnungsplattformen von Standortbetreibern (Smart Fuel Pass inkl. Sonnenladen, AMPLICITY) im eigenen Netz, Octopus Electroverse, Plugsurfing, Chargemap (+10 %, mit Boost 0 %), Wels Strom (Monta-Roaming), MB.CHARGE Public S/M/L (−0/−10/−15 % neben festen Preisen für Mercedes-HPC, IONITY und Partnernetz), BMW Charging Flex/Active, Elli Base/IONITY Boost/Ultra (+0,39 € je Ladevorgang), Shell-Roaming (+0,35 €) | Zone mit `pricing` `OPERATOR_AD_HOC` und Aufschlag auf den Ad-hoc-Preis des Ladepunkts |
| Herstellerkarten (Digital Charging Solutions, Elli) | Kia Charge, Charge myHyundai, Volvo, Toyota, smart, CHARGE NOW (auch MG), MB.CHARGE, BMW (DCS: „flexible Preise“, Rabatte von 5–30 % und ein Preferred Partner Network aus IONITY, EVN, Electra, Mer, Kelag, Shell, da emobil, Salzburg AG); Audi, Škoda, CUPRA (Elli: flexible Preise, IONITY- und Premium-Tarife mit Festpreisen); Porsche, Polestar (Plugsurfing), Mobilize (Renault) | Betreiberpreis mit Rabatt, Festpreise für IONITY/Mer/Partner, Premium-Tarife als offene Roamingzone |
| Preis je Ladepunkt nicht veröffentlicht | EnBW mobility+ außerhalb von SMATRICS EnBW (0,56–0,89 €/kWh) | Zone mit `pricing` `UNKNOWN`: nutzbar, Preis unbekannt |
| nur eigenes Netz | EVN #Drive Easy, Kelag eStart, Energie Klagenfurt, Stadtwerke Kufstein, Wels Strom, IONITY, Shell, Mer, Electra, Allego, Lidl | nur `HOME` |

Befunde:
- **BEÖ**: kein gemeinsamer Partnerpreis der Landesenergieversorger; der Zugang läuft über Hubject/intercharge, jeder Anbieter bepreist die anderen in seiner eigenen Partner- oder Roamingzone (immer über dem Heimnetz). Nur IKB hat einen Partnerpreis für fast alle BEÖ-Mitglieder.
- **Betreiber-weite Gültigkeit bestätigt**: Kein Anbieter unterscheidet innerhalb eines Partner- oder Roaming-Betreibers nach Standort; ein Betreiber in der Zone gilt für alle seine Ladepunkte. Ausnahmen sind Leistungsstufen (HPC ab 75/150/201 kW) und Sonderstandorte (da emobil „Alte Hauptpost“, Salzburg AG Parkgaragen-Partner) – als Hinweis vermerkt.
- **Tesla** (`AT*TSL`, [Endpunkt-Notiz](endpunkte/tesla-findus.md)): Preise je Standort und Tageszeit (0,35–0,65 €/kWh für Fremdmarken auf den öffentlichen Standortseiten), keine österreichische Ladekarte listet Supercharger als Roaming-Partner → in offenen Roamingzonen ausgeschlossen. Die Preise übernimmt chargeglobe nicht (Bot-Schutz der Standortseiten); stattdessen trägt das Netz `tesla` einen Preis-Hinweis mit der Standortseite des nächsten Superchargers (55 Standorte, kuratiert in `networks.json`).
- **MAINGAU Autostrom** (deutscher Anbieter, Preise „für ganz Europa“ inkl. deutscher USt) ist mit Standard- und Hochpreis im Katalog; ob in Österreich wohnende Kunden abschließen können, ist offen.
- **Energie Graz**: Das Preisblatt auf energie-graz.at liegt hinter einer Bot-Abfrage; der Tarifkalkulator der E-Control (ladetarif.at) hat es als Dokument (gültig ab 1.9.2024), daraus stammt der Katalogeintrag. Weitere Präfixe aus den Daten: Energie Graz auch `AT*6D6`, Tulln Energie auch `AT*E37`; ella (`AT*ELA`) gehört seit 1.10.2026 zum Netz von Wien Energie.
- **Abgleich mit ladetarif.at** (17.10.2026, alle 43 Produkte): übernommen wurden die Leistungsgrenze 50 kW bei OMV eMotion, die Stände (gültig ab) von Energie Steiermark, OMV, V-Go, illwerke vkw und Burgenland Energie, Grundgebühr 0 bei TIWAG, da emobil und Burgenland Energie, die Bruttopreise von SalzachSonne sowie die fehlenden Produkte StromMOBIL PARTNER und Burgenland Energie 1-phasig. Wo ladetarif.at ältere Daten hat (Salzburg AG, da emobil, IKB, Lidl) oder dem eigenen Preisblatt widerspricht (SMATRICS Charge & Roam+, Blockiergebühr DC von Burgenland Energie), gilt der Katalog. `AT*ELA` ist laut Betreiberliste ella, nicht Wien Energie. Nicht übernommen: Wels Strom „Mobil Strom“ (2023, auf der Website nicht mehr angeboten, die e.Ladekarte ist das aktuelle Produkt), der eingestellte ella-Tarif und elinx: Die App-Marke der Energie Steiermark wurde um den 7.9.2026 eingestellt, Kunden der Energie Steiermark laden mit der Mobilitätskarte S/M/L (Preisblatt 8.9.2026). Lidl: 0,29 €/kWh DC ist aktuell (ladetarif.at hat noch 0,35 €). Netiqs ist kein Ladepunktbetreiber in Österreich und bleibt außen vor.
- **ladetarif.at** (Tarifkalkulator der E-Control) liefert die Produkte der österreichischen Anbieter strukturiert (Liste über `POST /api/calculate`, Details `/api/products/<id>`, Preisblätter `/api/documents/<id>/download`, Betreiber `/api/operators`; Preisfelder netto, einige Anbieter haben brutto eingetragen); ohne Nutzungsbedingungen und ohne Lizenz für die Weitergabe; chargeglobe gleicht den Katalog nur ab (`scripts/tariffs/compare-ladetarif.py`), ein `ITariffSource`-Adapter folgt erst nach Freigabe der E-Control ([Endpunkt-Notiz](endpunkte/ladetarif.md)).
- **Nicht im Katalog**: MOON und ENIO (Preise je Standortbetreiber, Ad-hoc aus der E-Control), Tesla.
- **Präfixe ohne Bestätigung**: Burgenland Energie `AT*EBM` (BE Solution), Wels Strom `AT*LNK`/`AT*WSG`/`AT*ELN`; Leikermoser `AT*LME` ist noch in keinen Daten.

### Abdeckung der Ladepunkte (Stand 17.10.2026)

Abgleich der 40.868 aktiven Ladepunkte im Live-Bestand mit dem Katalog (50 Anbieter, 102 Tarife, vor dem Abgleich mit ladetarif.at):
- **37.909 Ladepunkte** sind mit mindestens einer Ladekarte nutzbar: je Betreiber 28–49 Tarife mit festem Preis und bis zu 42 mit Betreiberpreis oder unbekanntem Preis (z. B. EVN 42 + 38, Wien Energie 36 + 42, IONITY 49 + 30, ChargePoint 28 + 42).
- **2.183 Ladepunkte** nahmen laut E-Control keine Fremdkarten an (`roaming = false`) und gehörten zu Betreibern ohne eigene Ladekarte im Katalog; die 20 größten sind unten aufgeschlüsselt.
- **776 Tesla-Ladepunkte**: keine Karte im Katalog (siehe oben), Ad-hoc-Preis aus der E-Control.
- Ohne eigenes Netz im Katalog (nur über offene Roamingzonen erreichbar) sind die großen Betreiber ChargePoint (`AT*HTB`, `AT*CPI`), MOON (`AT*POW`), eCarUp (`CH*ECU`), e-sprit (`AT*EK6`), Wattif, Chargewell, Plenitude und E-Mobilitätszentrum: Sie haben keine eigene Ladekarte, ihre Apps rechnen den Ad-hoc-Preis ab.

#### Betreiber ohne Karte (`roaming = false`, kein Netz in einer Zone; SQL auf dem Live-Bestand und Recherche vom 17.10.2026)

Das Flag `roaming` gilt je Ladepunkt: Bei KWMS, GENOL, Contipark, STROMBOX, vaylens und e-Flux nehmen die meisten Ladepunkte Fremdkarten an, nur einzelne Standorte nicht.

| Betreiber-ID | Betreiber | EVSEs ohne Karte | Zahlung / Ergebnis |
|---|---|---|---|
| `AT*BIP` | Best in Parking | 455 | Laden und Parken gemeinsam an der Garage abgerechnet, Preis nur als Aushang; keine Karte, kein Roaming |
| `AT*SFP` | Smart Fuel Pass | 283 | App-Plattform für Standortbetreiber (QR-Code, Karte, Prepaid), Preis je Standort → neu: Anbieter `smart-fuel-pass` zum Betreiberpreis (Netz `smart-fuel-pass`) |
| `AT*MGA` | MGAtech (AMPLICITY) | 256 | Abrechnungsplattform für Standortbetreiber (QR-Code, Kreditkarte, RFID-Abo), einzelne Stationen über Electroverse → neu: Anbieter `amplicity` zum Betreiberpreis |
| `AT*SNL` | Sonnenladen | 132 | über die Smart-Fuel-Pass-App, registriert günstiger → im Netz `smart-fuel-pass` |
| `AT*GBC` | Green Business Center Linz | 73 | RFID für Mieter des Bürogebäudes (vermutlich); keine öffentliche Karte |
| `AT*C4O` | vaylens | 46 | Plattform mit Roaming je Ladepunkt; die 46 gehören einzelnen Standortbetreibern ohne Freigabe |
| `AT*735` | Loxone Campus | 40 | nur kontaktlose Karten vor Ort, kein Preis im Register |
| `AT*OBE` | OBERGER Energie | 38 | Kartenterminal vor Ort |
| `AT*E61` | Schultz Gruppe | 34 | Kredit- oder Bankomatkarte am Terminal (Hochzillertal) |
| `AT*GNL` | GENOL (Lagerhaus) | 32 | GENOL G+ Card → neu: Anbieter `genol` (Heimnetz `genol`, Roaming bei 17 Partnernetzen) |
| `AT*FTR` | futuregrid.energy | 30 | im ePower-Standortverzeichnis des ÖAMTC → Heimnetz von ÖAMTC ePower |
| `AT*EHB` | Stadtwerke Hartberg | 29 | eigene Ladekarte, Abrechnung über AMPLICITY → neu: Anbieter `energie-hartberg` (Stromkunden 0,35, Direkt 0,45 €/kWh) |
| `AT*STB` | STROMBOX | 23 | die 23 sind „Direct Payment only“ |
| `AT*84N` | Steinplatte | 20 | Zahlung vor Ort (vermutlich) |
| `AT*CTP` | Servipark (Contipark) | 20 | Flag vermutlich falsch: die Garagenseite nennt Preise anderer Anbieter „im Roaming-Verband“ |
| `AT*HSH` | New Energie | 18 | Zahlung vor Ort (vermutlich) |
| `AT*EPP` | EPP | 17 | Bankomatzahlung vor Ort |
| `AT*EFL` | Road (e-Flux) | 17 | Plattform mit Roaming je Ladepunkt; Register meldet 0,005 €/kWh (vermutlich Fehler) |
| `AT*KWS` | KWMS | 16 | Plattform mit Roaming je Ladepunkt; die 16 ohne Freigabe |
| `AT*F9B` | CAS Immobilien | 16 | Register meldet 0,005 €/kWh (vermutlich Fehler) |

Neu abgedeckt sind damit 762 Ladepunkte (SFP, MGA, SNL, GNL, FTR, EHB): Nach dem Deploy (17.10.2026) sind **38.671 von 40.868 Ladepunkten** mit mindestens einer Ladekarte nutzbar; ohne Karte bleiben 1.421 Ladepunkte (vor allem Best in Parking 455, Green Business Center 73, vaylens 46) und die 776 von Tesla, dort gibt es nur den Ad-hoc-Preis vor Ort.

#### Auslandspreise (Recherche 17.10.2026, Zonen mit `countries`)

- **EVN #Drive Smart**: Partnerladenetz in AT, DE, IT, HR und SI zum selben Preis; anderswo nicht freigeschaltet. #Drive Easy nur an EVN-Stationen.
- **SMATRICS Charge & Roam(+)** und **VERBUND V-GO**: ein Roamingpreis für AT, DE, IT, HR, SK, SI und CZ; bei SMATRICS EnBW-Stationen in Deutschland (`DE*EBW`, Netz `enbw-de`) zum Heimnetzpreis. Charge Simple ohne Roaming.
- **Wien Energie** (alle Tanke-Tarife): im Ausland nicht nutzbar (FAQ vom 10.11.2022, nichts Neueres).
- **ÖAMTC ePower**: „Partnernetz Ausland“ in DE, IT, HR und SI zu AC 0,64, DC 0,74, HPC 0,84 €/kWh; gilt nur bei den gelisteten Partnern, die Zone deckt alle Stationen dieser Länder ab.
- **IONITY**: Preise je Land der Station (DE, IT, SI, HR, SK in Euro; CZ, HU, CH nur in Landeswährung und nicht im Katalog); das Netz `ionity` hat die Betreiber-IDs dieser Länder. Offen: Die Abo-Seite zeigt für Österreich im Monatsabo 0,49 (Motion) bzw. 0,39 €/kWh (Power), der Katalog hat 0,51/0,41 (Listenpreis).
- **Elli**: Tarife je Vertragsland, jeder nennt nur die Preise im eigenen Land; für österreichische Verträge sind keine Auslandspreise veröffentlicht.
- **EnBW mobility+**: an EnBW-Stationen in Deutschland S 0,56, M 0,46, L 0,39 €/kWh, bei anderen Betreibern 0,56–0,89 €/kWh nur in der App; für Österreich keine eigene Preistabelle, die Zone bleibt `UNKNOWN`.

## Nächste Schritte aus der Bestandsanalyse

1. E-Control: Key liegt vor, Adapter `ChargeGlobe.Providers.EControl` existiert ([Endpunkt-Notiz](endpunkte/econtrol.md)). Offen: Swagger ist auch mit Key nicht lesbar, Bulk-Endpunkt und Status-Aktualisierungsrate unbekannt; Operator-IDs aller Scope-Betreiber aus den Daten ziehen.
2. Tarifkatalog aktuell halten (Stand je Tarif, Preisänderungen meist zum 1.1., 1.4., 1.7. und 1.10.); offene Lücken siehe „Tarife der Anbieter (Katalog 10/2026)“.
3. Tesla: kein Adapter (Bot-Schutz und Website-Bedingungen, [Endpunkt-Notiz](endpunkte/tesla-findus.md)); Preise nur als Ad-hoc-Preis der E-Control mit Preis-Hinweis des Netzes.
4. Netzwerkanalyse (DevTools) der Web-Karten: Electroverse, SMATRICS/ÖBB, Energie AG (CIRRANTiC), EnBW – jeweils Endpunkt-Notiz nach Vorlage in 05, Capabilities, ToS.
5. App-Endpunkte aus der Analyse vom 17.10.2026 (siehe „Aus der App-Analyse“): ToS bzw. Erlaubnis klären (EVN/e-vo für `emobilportal.at`, SMATRICS/Verbund für den POI-Dienst, ÖAMTC, IONITY), danach Adapter mit Fixtures bauen. Reihenfolge nach Nutzen: V-Go/SMATRICS (91.600 EVSEs mit Status), EVN-Portal (anonym, Status je EVSE), IONITY (Ad-hoc-Preis je Stecker), ÖAMTC (Stationspreise).
