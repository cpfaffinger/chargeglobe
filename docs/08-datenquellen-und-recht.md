# 08 – Datenquellen & Rechtsrahmen

Stand: 13. Oktober 2026. Kennzeichnung: **[V]** verifiziert über Primärquelle, **[V-sek]** über belastbare Sekundärquelle (z. B. Quellcode einer Open-Source-Integration), **[U]** unsicher/nicht belegbar.

## 1. Österreich – das Rückgrat: E-Control Ladestellenverzeichnis

### Rechtsgrundlage [V]
- Bundesgesetz zur Festlegung einheitlicher Standards beim Infrastrukturaufbau für alternative Kraftstoffe (BGBl. I Nr. 38/2018 idF 150/2021): § 3 Abs. 5 Meldepflicht der Betreiber öffentlich zugänglicher Ladepunkte, „laufend aktuell“; § 4a E-Control führt das Verzeichnis und vergibt Betreiber-IDs; § 5 Verwaltungsstrafe bis 1.000 €. [RIS](https://www.ris.bka.gv.at/GeltendeFassung.wxe?Abfrage=Bundesnormen&Gesetzesnummer=20010261)
- **Ladepunkt-Daten-VO**, BGBl. II Nr. 257/2024 (24.09.2024; Erstmeldung nach 9 Monaten Übergangsfrist, also seit Ende Juni 2025 wirksam). [RIS](https://www.ris.bka.gv.at/eli/bgbl/II/2024/257)

### Was gemeldet werden muss (Ladepunkt-Daten-VO) [V]
| Ebene | Pflichtdaten |
|---|---|
| Betreiber (§ 3) | E-Control-Identifikationszeichen (3-stellige Betreiber-ID), Name, Kontakt |
| Standort (§ 4) | Adresse + GNSS-Koordinaten, Bezeichnung, Verbraucherkontakt, Öffnungszeiten, Parkgebühr ja/nein, Gesamtleistung kW, 100 % erneuerbar, barrierefreie Plätze |
| Ladepunkt statisch (§ 5 Abs. 1) | **EVSE-ID**, Stecker (Anzahl/Art), max. kW, AC/DC, Authentifizierung, Zahlungsarten, Roaming ja/nein, Fahrzeugklasse |
| Ladepunkt dynamisch (§ 5 Abs. 2) | Betriebsbereitschaft, **Echtzeit-Verfügbarkeit frei/besetzt je Ladepunkt**, **Ad-hoc-Preis** (kostenlose Ladestellen ausgenommen) |
| Format (§ 7) | DATEX II oder kompatibles maschinenlesbares Format; Frequenz „laufend aktuell“, keine feste Minutenangabe |

### Kennzahlen (E-Control-Präsentation 25.02.2026) [V]
447 aktive Betreiber · 14.793 Ladestellen · 36.991 Ladepunkte · 99 % mit Ad-hoc-Preis (93,5 % ct/kWh, 5,5 % ct/min) · 90 % Roaming · Median Ad-hoc 64 ct/kWh (AC 57, DC 70) · ca. 150 API-Nutzer (ÖAMTC, Datahubs, Umweltbundesamt, Navigationssysteme). E-Control wertet selbst Status-Zeitreihen aus (z. B. „Charging“-Minuten je Ladepunkt und Monat, Wien Ø 19 %). [PDF](https://www.e-control.at/documents/1785851/1811582/20260225-e-control-ladestellenverzeichnis-update.pdf/d56a399d-c6be-60c9-26fe-dc799f7b62f6?t=1772042668911)

**Konsequenz für chargeglobe:** Diese eine Quelle liefert für ganz Österreich Inventar, EVSE-IDs, Live-Status je Ladepunkt und Ad-hoc-Preise. Sie ist Tier 0 und der Anker, an den alle anderen Quellen über die EVSE-ID andocken.

### Öffentliche API [V] / [V-sek]
| | |
|---|---|
| Basis-URL | `https://api.e-control.at/charge/1.0` |
| Doku | Swagger-UI `…/swagger-ui.html?urls.primaryName=public-api`; die API-Doku-Ressourcen antworten ohne und mit Key (Header `apiKey`) mit 401 [V, 2026-10-14] |
| Registrierung | [admin.ladestellen.at/#/api/registrieren](https://admin.ladestellen.at/#/api/registrieren), kostenlos, Key per E-Mail, **an eine Domain gebunden** (Referer-Header wird geprüft) |
| Auth-Header | `apiKey: <key>` und `Referer: https://<registrierte-domain>/` [V, 2026-10-14]. chargeglobe hat einen Key; Key und Domain stehen im Config-Service (`provider.api_key`, `provider.referer`, Scope `provider:econtrol`) |
| Endpunkte | `GET /search?latitude=&longitude=` → die 100 nächsten Stationen (`distance` in km) inkl. `points[]` mit Live-Status und Ad-hoc-Preis [V, 2026-10-14; [Endpunkt-Notiz](endpunkte/econtrol.md)] · aus der HA-Integration [V-sek]: `GET /search/stations?evseId=&maxResults=` · `GET /countries/{cc}/operators/{op}/stations/{stationId}` · `…/stations/{stationId}/points` |
| Felder je Ladepunkt | `evseId`, `status` (`AVAILABLE`, `CHARGING`, `RESERVED`, `BLOCKED`, `INOPERATIVE`, `OUTOFORDER`), `connectorType(s)` (zwei Vokabulare: Legacy `STYPE2`/`CCCS2`/`CG105`… und OCPI-nahe `IEC_62196_T2`…), `capacityKw`/`energyInKw`, `priceCentKwh`/`priceInCentPerKwh`, `freeOfCharge`, `authenticationMode(s)` |
| Felder je Station | `stationStatus`, `operatorName`, `evseOperatorId`, `evseStationId`, `label`, `street`, `postCode`, `city`, `location{lat,lon}`, `greenEnergy`, `openingHours[]` |
| Bulk-Abruf | Laut E-Control-Webinar 2023 liefert die API „alles, was auf ladestellen.at angezeigt wird, auch für ganz AT auf einmal“ [V] – ein Bulk-Endpunkt ist nicht belegt (`/stations`, `/countries/AT/stations` → 401 [V, 2026-10-14]) [U]. 2023 gab es ein GeoJSON-Format mit `refillPoints[]` und `lastUpdate` [V-sek]. |
| Limits (aus Nutzungsbedingungen, Wiedergabe Dritter) [V-sek] | **2.500 Requests/h, max. 30 parallel**; Quellenangabe mit verlinktem Logo; Quartalsstatistik eindeutiger Besucher an E-Control; Go-Live-URL per E-Mail melden |
| Lizenz | **kein Open-Data-Standard (kein CC BY)**, sondern „Nutzungsvertrag, kostenlos“ (Click-Wrap bei Registrierung). OSM hat eine gesonderte schriftliche Einverständniserklärung erhalten (2023). Weiterverbreitung durch chargeglobe (z. B. Bulk-Dumps) → **vorher schriftlich bei support@ladestellen.at klären**. |

Referenzen: [HA-Integration (MIT)](https://github.com/prusuino/ha_austrian_charging_stations) · [HA-Forum](https://community.home-assistant.io/t/austrian-charging-stations-ladestellen-at-real-time-ev-charger-availability-for-austria/1017999) · [OSM-Thread 2023](https://community.openstreetmap.org/t/neue-datenfreigabe-das-ladestellenverzeichnis-der-e-control-austria/106181) · [OCM-Thread](https://community.openchargemap.org/t/open-data-government-sources-for-austria/119) · [E-Control-Webinar 2023](https://www.e-control.at/documents/1785851/0/20230222-Webinar-Ladestellenverzeichnis4Pros_FINAL.pdf/4174e124-b813-0cfb-a522-02159eb566be?t=1677136320513)

### Polling-Budget gegen das Rate-Limit
2.500 Requests/h sind bei 5-Minuten-Zyklen ≈ 200 Requests je Zyklus. Da `/search` die ~100 nächsten Stationen liefert, braucht Ost-Österreich (grob 5.000–6.000 Ladestellen) ein Raster von 60–120 Abfragepunkten, das aus den **bekannten Standortkoordinaten** berechnet wird (greedy: wähle Abfragepunkte so, dass jede Station unter den 100 nächsten mindestens eines Punktes liegt; Nachweis über die zurückgegebene `distance`). Umgesetzt ist ein adaptives Raster (`SearchGrid`): eine Zelle wird geviertelt, solange die Antwort eine volle Seite ist und nicht bis zu den Zellecken reicht. Ein Raster aus bekannten Standortkoordinaten und ein Bulk-Endpunkt (falls er sich findet) sind Optimierungen für den Scheduler.

### EVSE-ID in Österreich [V]
- Format `AT` + 3-stellige Betreiber-ID + `E` + bis zu 30 alphanumerische Zeichen; `*` ist nur Lesbarkeit, nicht Teil der ID. Beispiel der E-Control: `AT*ECA*RUDOLF13A*001`; Operator-IDs z. B. `VIE` (Wien Energie), `SMA` (Smatrics). [E-Control-Newsletter](https://www.e-control.at/en/branchen-newsletter/-/asset_publisher/0wTTT16KsQRv/content/e-mobilitat-was-ist-eigentlich-die-evse-i-1)
- Vergabestelle: **E-Control** (§ 4a Abs. 2, kostenlos über admin.ladestellen.at, dort öffentliches Betreiber-Register). Daneben private Vergabestelle [at.evseid.eu](https://www.at.evseid.eu/home.html?lang=de) (Smart Mobility Power GmbH); Alt-IDs bleiben gültig.
- Begriffe „Netzpunkt-ID“ oder „Zählpunkt“ kommen im Ladestellen-Kontext in keiner Quelle vor; „Zählpunktnummer“ (33-stellig, `AT00…`) ist der Netzanschluss nach ElWOG. → Primärschlüssel ist die EVSE-ID ([ADR-0001](adr/0001-evse-id-als-identitaet.md)).
- Die EVSE-ID ist auch der übergreifende Schlüssel im Roaming (Hubject intercharge, OCPI, OICP); das BEÖ-Netz roamt über Hubject.

## 2. AFIR und der nationale Zugangspunkt (NAP)

- **AFIR (EU) 2023/1804 Art. 20**: seit 14.04.2025 müssen Betreiber statische und dynamische Daten kostenlos und diskriminierungsfrei über den NAP bereitstellen. **Durchführungs-VO (EU) 2025/655**: ab 14.04.2026 im Format DATEX II (CEN/TS 16157-10); statisch ≤ 24 h, dynamisch (Status, Verfügbarkeit, **Ad-hoc-Preis inkl. aller Komponenten**) ≤ 1 min. [EUR-Lex 2025/655](https://eur-lex.europa.eu/eli/reg_impl/2025/655/oj/eng) · [Zusammenfassung NDW](https://docs.ndw.nu/en/data-uitwisseling/interface-beschrijvingen/dafne-api/VerplichtingConformUitvoeringsverordeningEU2025/) [V-sek]
- **NAP Österreich: [mobilitaetsdaten.gv.at](https://www.mobilitaetsdaten.gv.at/)** (AustriaTech). Datensatz [„Ladestellenverzeichnis der E-Control (nationales Ladepunkteregister)“](https://www.mobilitaetsdaten.gv.at/daten/ladestellenverzeichnis-der-e-control-nationales-ladepunkteregister): DATEX II v3.2, XML, HTTP-Pull, „bei Änderung“, Zugang über dieselbe E-Control-API-Registrierung. **Der NAP hostet keine eigene Kopie; E-Control ist die AFIR-Quelle und erzeugt DATEX II selbst.** [V]
- Für chargeglobe heißt das: die E-Control-API selbst bietet laut ihrer Spezifikation DATEX II v3.5 (Table- und Status-Publication) an; dieser Abruf ersetzt das Suchraster und ist der Kern des späteren generischen DATEX-II-Adapters ([Endpunkt-Notiz](endpunkte/econtrol.md)).

## 3. Weitere Quellen Österreich

| Quelle | Inhalt | Lizenz | Nutzen |
|---|---|---|---|
| **OpenStreetMap** (Overpass) | 6.961 `amenity=charging_station` in AT (Zählung 13.10.2026) – deutlich weniger als 14.793 Ladestellen bei E-Control; teils `ref:EU:EVSE` | ODbL (Share-Alike bei Vermischung!) | Plausibilisierung von Koordinaten, Zusatzattribute (Parkplatz, Überdachung) |
| **OpenStreetMap** (Overpass), Verwaltungsgrenzen | Grenzrelationen der neun Bundesländer (`admin_level=4`, `ISO3166-2` `AT-1` … `AT-9`), vereinfacht als `regions-at.geojson` im Repo | ODbL 1.0, Attribution „© OpenStreetMap contributors“ (steht in der Datei, in `docs/03` und im README) | genutzt: Tabelle `region`, daraus `location.region_code` und `region` in `GET /evses/{evseId}`; die Datei selbst ist eine abgeleitete Datenbank unter ODbL, Region-Codes in API-Antworten sind ein „Produced Work“ |
| **OpenChargeMap** | 28 Netzwerke für AT; keine E-Control-Importe (fehlende Freigabe); API-Key Pflicht, > 10.000 Req/Tag absprechen | CC BY 4.0 (Beitragsdaten), Importquellen je POI | Ergänzung, Community-Fotos/Kommentare |
| **Stadt Wien OGD** | WFS `ogdwien:ELADESTELLEOGD` (nur städtische 22-kW-Punkte, ~1.200) | CC BY 3.0 AT | gering, da in E-Control enthalten |
| Land NÖ/OÖ/Bgld, Wien Energie, Linz AG | keine Ladestations-Open-Data gefunden [U] | – | – |
| **BEÖ** | Karte nutzt selbst E-Control-Daten; Statistiken | – | Branchenkontext |
| **ladetarif.at** (E-Control Tarifkalkulator) | 22 Anbieter, 44 Produkte (02/2026) – EMP-Tarife! | eigene Schnittstellen-Nutzungsbedingungen (Weitergabe des Zugangs untersagt) | wichtigste Quelle für **EMP-Tarifmodelle** in AT; Nutzung vorab mit E-Control klären |
| BWB Branchenuntersuchung E-Ladeinfrastruktur | Markt-/Preisanalyse | – | Kontext für Tarifmodell |

## 4. Deutschland (Phase 3)

- **Bundesnetzagentur Ladesäulenregister**: CSV/XLSX-Download (tagesaktuell), **CC BY 4.0**; Felder je Ladeeinrichtung (Betreiber, Status, Art, Anzahl Ladepunkte, Nennleistung, Steckertypen je Punkt), **keine EVSE-IDs belegt** → Matching über Geo + Betreiber + Steckerkonfiguration. Webservice-Doku nur auf Anfrage. [BNetzA](https://www.bundesnetzagentur.de/DE/Fachthemen/ElektrizitaetundGas/E-Mobilitaet/DownloadundKontakt.html) [V]
- **Mobilithek (NAP)**: AFIR-Angebote **pro Betreiber** als DATEX-II-Pull (Subscription-ID + Client-Zertifikat); Profil v01-00-00 mit JSON-Schemas für `EnergyInfrastructureTablePublication`/`StatusPublication`. [GitHub MobilithekDE](https://github.com/MobilithekDE/AFIR-DATEX-II-Recharging-Profil/) [V]. Lizenz der einzelnen Angebote [U].
- Regionale Echtzeitquellen: MobiData BW (OCPI-Beispielformat), Bonn (`https://new-poi.chargecloud.de/bonn`, CC BY).

## 5. Schweiz (Phase 3)

- **ich-tanke-strom.ch (BFE)**: heute OICP-2.3-JSON (EVSEData + EVSEStatus, alle 30–60 s) unter `data.geo.admin.ch/ch.bfe.ladestellen-elektromobilitaet/…`; **ab November 2026 öffentliche OCPI-API ohne Auth**: `https://api.emobility-prod.prometheon.bfe.admin.ch/ocpi/` mit `/locations`, `/tariffs`, `/status?date_from=` (max 1000), ~60 req/min. Lizenz „Open use, Quellenangabe, kommerziell nur mit Erlaubnis“. [SFOE-Doku](https://github.com/SFOE/ichtankestrom_Documentation) [V] → erster Einsatz des generischen OCPI-Adapters.

## 6. Italien (Phase 4)

- **PUN (Piattaforma Unica Nazionale)**, MASE/GSE/RSE, Portal seit März 2024 mit Status; **keine Open-Data-API gefunden** [U]. NAP [nap.mit.gov.it](https://nap.mit.gov.it) (JS-only, AFIR-Inhalte nicht verifizierbar). Vorgehen: Anfrage an GSE/MIT; Südtirol-Pilot über OCM/OSM + CPO-Web-Karten.

## 7. Standards (Mapping-Grundlage)

| Standard | Relevanz | Mapping |
|---|---|---|
| **OCPI 2.2.1** ([Spec](https://github.com/ocpi/ocpi)) | kanonisches Vokabular (ADR-0004); CH-API, LT-NAP | 1:1 |
| **OICP 2.3** (Hubject, [Docs CC BY-SA](https://github.com/hubject/oicp)) | CH heute, Roaming-Plattform der meisten AT-CPOs | `EvseStatus` `Available/Reserved/Occupied/OutOfService/EvseNotFound/Unknown` → OCPI `AVAILABLE/RESERVED/CHARGING/OUTOFORDER/REMOVED/UNKNOWN` |
| **DATEX II EnergyInfrastructure** (CEN/TS 16157-10, [Doku](https://docs.datex2.eu/v3.2/energy/index.html)) | NAPs AT/DE/IT | `RefillPointStatusEnum` `available/blocked/charging/faulted/inoperative/occupied/outOfOrder/outOfStock/planned/removed/reserved/unavailable/unknown` → OCPI; `faulted/unavailable/outOfStock` → `OUTOFORDER`/`INOPERATIVE`; offizielle Mapping-Tabelle existiert ([DATEX II RSP](https://docs.datex2.eu/v3.4/profiles/rsp/alternativefuel/function2rsp.html)) |
| **EVSE-ID** (ISO 15118-2 Annex H, eMI3/IDACS; [OSM-Wiki](https://wiki.openstreetmap.org/wiki/Key:ref:EU:EVSE)) | Identität | Regex `^[A-Z]{2}\*?[A-Z0-9]{3}\*?E[A-Z0-9\*]{1,30}$`; optional Pool `*P…`/Station `*S…` |

**Bausteine für .NET:** [BitzArt/OCPI.Net](https://github.com/BitzArt/OCPI.Net) (MIT, OCPI 2.2.1-Contracts, Doku dünn – nur die Contracts übernehmen); DATEX II: kein .NET-Parser gefunden → Klassen aus den Mobilithek-JSON-Schemas generieren (NJsonSchema) bzw. XSD → `xsd.exe`/`XmlSerializer`.

## 8. Rechtsrahmen (sachlich, keine Rechtsberatung)

| Thema | Befund | Konsequenz für chargeglobe |
|---|---|---|
| **Datenbankherstellerrecht** §§ 76c/76d UrhG (sui generis) | Schutz bei wesentlicher Investition; auch wiederholte systematische Entnahme unwesentlicher Teile kann verletzen (EuGH Innoweb C-202/12: Echtzeit-Metasuche = Weiterverwendung). Ob E-Control-Daten (gesetzlich gemeldet, keine eigene Beschaffungsinvestition) überhaupt geschützt sind: fraglich [U]. Vertragliche Nutzungsbedingungen gelten unabhängig davon (EuGH Ryanair/PR Aviation). | Offizielle API mit Key und Terms nutzen; Redistribution schriftlich klären; bei Betreiber-Web-Karten ToS lesen |
| **Text-und-Data-Mining** § 42h UrhG | Abs. 6: TDM für jedermann bei rechtmäßigem Zugang, durch maschinenlesbaren Vorbehalt abdingbar; deckt Analyse, nicht dauerhafte Re-Publikation | Abruf zur Statusanalyse ist gut gedeckt; Re-Publikation braucht Lizenz/Terms |
| **Technische Schutzmaßnahmen** § 90c UrhG, UWG | Umgehung wirksamer Zugangskontrollen ist verboten, wenn ein Ausschließungsrecht betroffen ist. **Extraktion eingebetteter App-Keys oder Umgehung von Signaturen ist rechtlich riskant.** Abruf öffentlicher JSON ohne Umgehung ist unkritisch. | App-Analyse nur zum Verständnis; Adapter nur für anonym erreichbare Endpunkte oder mit eigenem, vom Anbieter ausgegebenem Key; Pinning-Bypass nur auf eigenem Gerät zur Analyse (siehe [05](05-crawling-konzept.md)) |
| **AFIR / § 4a Abs. 1** | Daten müssen „kostenlos, offen, diskriminierungsfrei“ über NAP zugänglich sein | politisches Argument gegenüber restriktiven Terms (Logo, Statistikpflicht); kein Freibrief |
| **Data Act Art. 43** | sui-generis-Ausschluss nur für Daten vernetzter Produkte im Hersteller/Nutzer-Verhältnis | als Scraping-Argument schwach |
| **DSGVO** | Betreiber-Kontaktdaten können Privatpersonen betreffen (im Datensatz enthalten) | Kontaktfelder natürlicher Personen nicht speichern/veröffentlichen; Statushistorien sind nicht personenbezogen |

### Konkrete Leitlinien
1. E-Control-API-Key auf die Projekt-Domain registrieren, Terms archivieren, Limits (2.500/h, 30 parallel) hart im Adapter verankern.
2. Attribution „Datenquelle: Ladestellenverzeichnis der E-Control (ladestellen.at)“ + Logo im Frontend und `meta.attribution` in der API; Go-Live-Mail; Quartalsstatistik automatisieren.
3. Vor Bulk-Exports schriftliche Freigabe einholen (OSM-Präzedenz zeigt Kooperationsbereitschaft).
4. OSM-Daten (ODbL) in eigener Quell-Schicht halten; keine Vermischung in Exporten ohne Share-Alike-Entscheidung. Die Region-Polygone bleiben eine getrennte Datei unter ODbL mit Quellenangabe; in die Ladepunktdaten fließt nur der Region-Code ein.
5. Tier-1/2-Quellen: nur anonym erreichbare Endpunkte, ToS prüfen und dokumentieren, Kill-Switch, Kontaktadresse im User-Agent; Takedown-Prozess.
6. Alle Preis-/Tarifdaten mit Zeitstempel und Quelle versionieren.
