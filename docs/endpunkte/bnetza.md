# Endpunkt-Notiz: Bundesnetzagentur – Ladesäulenregister (CSV-Export)

| | |
|---|---|
| Analysiert am | 2026-10-17 |
| Analysiert von | Claude (Agent); Download der CSV vom 01.09.2026 (55,6 MB) und Auswertung aller 116.443 Zeilen |
| Methode | Export-Doku (Seite „Ladesäulenkarte“) und Auswertung der Datei |
| Tier | 0 (`OFFICIAL`) |
| Zugang | anonym, ohne Key; die Datei liegt unter `data.bundesnetzagentur.de`, der Link mit dem Datum der Aktualisierung steht auf der Seite der Ladesäulenkarte |
| ToS geprüft | [Ladesäulenkarte](https://www.bundesnetzagentur.de/DE/Fachthemen/ElektrizitaetundGas/E-Mobilitaet/Ladesaeulenkarte/start.html), 2026-10-17: „Die Daten stehen unter der Lizenz CC BY 4.0 (Quellenvermerk: Bundesnetzagentur.de)“ |
| Lizenz / Attribution | CC BY 4.0; `Datenquelle: Bundesnetzagentur.de (CC BY 4.0)` (Descriptor `AttributionText`, Tabelle `source`, `meta.attribution`) |
| Rate-Limit beobachtet | keines; der Adapter sendet höchstens 2 Requests je Minute (ein Lauf = Seite + Datei, die Probe = Seite + Kopf der Datei) |
| Capabilities | StaticInventory · EvseIds · BulkAll (eine Datei für ganz Deutschland) |

Konfiguration (Config-Service, Scope `provider:bnetza`): `provider.enabled` (Default aus), `provider.regions` (leer = alle 16 Bundesländer `DE-BW … DE-TH`; der Lauf liest immer die ganze Datei und behält die Ladeeinrichtungen in der Box der Regionen), `provider.interval.inventory_minutes` (Default 7 Tage, weil die Datei monatlich erscheint und ein Lauf 116.000 Standorte merged), `provider.request_budget_per_hour`, `provider.cooldown_*`. Kein Key, kein Referer.

## Endpunkte

### `GET https://www.bundesnetzagentur.de/DE/Fachthemen/ElektrizitaetundGas/E-Mobilitaet/Ladesaeulenkarte/start.html` – Download-Links
- HTML-Seite mit den Links auf CSV und XLSX: `https://data.bundesnetzagentur.de/Bundesnetzagentur/DE/Fachthemen/ElektrizitaetundGas/E-Mobilitaet/Ladesaeulenregister_BNetzA_<JJJJ-MM-TT>.csv` (CSV vom 01.09.2026, XLSX mit Stand 30.09.2026). Das Datum im Dateinamen wechselt mit jeder Aktualisierung; der Client (`BNetzAClient.FindCsvAsync`) liest den ersten Link auf `data.bundesnetzagentur.de`, der `Ladesaeulenregister` enthält und auf `.csv` endet. Fehlt er, ist das ein Formatfehler (`InvalidDataException` → Pause der Quelle, [05](../05-crawling-konzept.md) Abschnitt 4).

### `GET https://data.bundesnetzagentur.de/…/Ladesaeulenregister_BNetzA_<Datum>.csv` – Register
- `Content-Type: application/octet-stream`, `Accept-Ranges: bytes`, `ETag`, `Last-Modified` (03.09.2026 für die Datei vom 01.09.); 55.559.083 Bytes, 116.455 Zeilen. Der Adapter streamt die Datei (`HttpCompletionOption.ResponseHeadersRead`), die Probe liest die ersten 4 KB per `Range: bytes=0-4095` (Antwort `206`) und prüft die Kopfzeile.
- Format: UTF-8 mit BOM, Semikolon, Felder mit Semikolon in doppelten Anführungszeichen (`"RFID-Karte;Onlinezahlungsverfahren"`), CRLF, Dezimalkomma; 10 Vorspannzeilen (Titel, Hinweis, „Letzte Aktualisierung vom: 01.09.2026“, Gruppenzeile „Allgemeine Informationen;…;1. Ladepunkt;…“), dann die Kopfzeile ab `Ladeeinrichtungs-ID`, 47 Spalten. Eine Zeile je Ladeeinrichtung mit bis zu 6 Ladepunkten. Gekürzter echter Auszug (12 Zeilen): `fixtures/bnetza/ladesaeulenregister-auszug.csv`.
- Spalten: `Ladeeinrichtungs-ID` (7-stellig, eindeutig, überdauert Betreiberwechsel), `Betreiber`, `Anzeigename (Karte)`, `Status`, `Art der Ladeeinrichtung`, `Anzahl Ladepunkte`, `Nennleistung Ladeeinrichtung [kW]`, `Inbetriebnahmedatum`, `Straße`, `Hausnummer`, `Adresszusatz`, `Postleitzahl`, `Ort`, `Kreis/kreisfreie Stadt`, `Bundesland`, `Breitengrad`, `Längengrad`, `Standortbezeichnung`, `Informationen zum Parkraum`, `Bezahlsysteme`, `Öffnungszeiten`, `Öffnungszeiten: Wochentage`, `Öffnungszeiten: Tageszeiten`, je Ladepunkt `Steckertypen{n}`, `Nennleistung Stecker{n}`, `EVSE-ID{n}`, `Public Key{n}`.
- Beobachtete Werte (Datei vom 01.09.2026, 116.443 Ladeeinrichtungen, 209.136 Ladepunkte):
  - `Status`: `In Betrieb` 116.423, `In Wartung` 20 – kein Belegungsstatus, sondern der Lebenszyklus.
  - `Art der Ladeeinrichtung`: `Normalladeeinrichtung` 85.288, `Schnellladeeinrichtung` 31.155.
  - `Bundesland`: alle 16; Bayern 22.838, Nordrhein-Westfalen 22.500, Baden-Württemberg 18.863, … Berlin 4.953, Saarland 796.
  - `EVSE-ID{n}`: bei 65.424 von 209.136 Ladepunkten befüllt (31 %), davon 61.257 gültig nach ISO 15118 (`DE*UFC*E01121*1`, `DEAEWE002501`, `DEALLEGO000636*1`); ungültig sind Seriennummern (`Seriennummer 23BZ3224B`), Kurzcodes (`E7JFZ`), Punkt-Schreibweisen (`DE.ARR.ESP147`) und Unterstriche (`DEALLEGO002900_1`).
  - `Steckertypen{n}` (ein Ladepunkt kann mehrere haben, `; `-getrennt): `AC Typ 2 Steckdose` 138.602, `DC Fahrzeugkupplung Typ Combo 2 (CCS)` 54.995, `AC Typ 2 Fahrzeugkupplung` 19.121, `AC Schuko` 3.871, `DC CHAdeMO` 3.191, `DC Megawatt Charging System (MCS)` 32, `AC Typ 1 Steckdose` 9, `AC CEE 5-polig` 3, `AC CEE 3-polig` 1, `DC Tesla Fahrzeugkupplung (Typ 2)` 1.
  - `Nennleistung Stecker{n}`: ein Wert oder einer je Steckertyp (`22; 3,7`); Dezimalkomma.
  - `Bezahlsysteme` (`;`-getrennt): `Onlinezahlungsverfahren` 92.272, `RFID-Karte` 86.770, `Kreditkarte (NFC)` 23.735, `Debitkarte (NFC)` 22.861, `Sonstige` 20.121, `Kreditkarte (Lesegerät)` 16.452, `Debitkarte (Lesegerät)` 13.339, leer 12.272, `Plug & Charge` 6.683, `Kostenlos` 5.157, `Bargeld` 2.582, `Kostenlos (Registrierung)` 1.070.
  - `Öffnungszeiten`: `Keine Angabe` 74.214, `247` 40.253, `Eingeschränkt` 1.976 (dann `Wochentage` wie `Montag; Dienstag; …` und `Tageszeiten` wie `05:30-22:00; 05:30-22:00; …`, ein Zeitraum je Tag).
  - `Informationen zum Parkraum`: leer 58.309, `Keine Beschränkung` 50.885 (+ 225 `keine Beschränkung`), `Nur für Kunden/Besucher` 6.913 (+ `Nur Kunden/Besucher` 70, `Kunden/Besucher` 41).
  - Koordinaten: alle Zeilen gültig (12 ohne Dezimalstellen, z. B. `53`).
  - `Betreiber` ist bei vielen kleinen Anlagen eine natürliche Person (Pflichtangabe des Registers, CC BY 4.0); der Adapter übernimmt den Namen wie die E-Control-Quelle. `Public Key{n}` (Eichrecht, öffentlich) wird nicht übernommen, auch nicht in `raw_snapshot`.

### Mapping auf das Ingest-Modell (`BNetzAMapper`)
- `Ladeeinrichtungs-ID` → `ProviderLocationSnapshot.ExternalId`; `Standortbezeichnung` → `Name` (meist leer); `Straße Hausnummer`, `Postleitzahl`, `Ort`, Land immer `DE` → `Address`; `Breitengrad`/`Längengrad` (Dezimalkomma) → `Coordinates`, ungültig oder 0/0 → Zeile wird mit `ProviderMappingIssue` übersprungen.
- `Betreiber` → `OperatorName`; `OperatorId` und `OperatorCountry` aus der ersten gültigen EVSE-ID der Zeile (`DE*UFC*E01121*1` → `UFC`, `DE`), sonst `null` (Matching dann über den Namen, [03](../03-datenmodell.md)).
- `Informationen zum Parkraum` → `Access` (Tabelle `parking-mapping.json`: `Keine Beschränkung` → `PUBLIC`, `Nur für Kunden/Besucher` → `RESTRICTED`, leer → `null`).
- `Öffnungszeiten`: `247` → `TwentyFourSeven`; `Eingeschränkt` → Text aus Wochentagen und Tageszeiten (`Montag 05:30-22:00; …; Samstag 06:00-14:00`); `Keine Angabe` → `null`.
- Je Ladepunkt `n` mit `Steckertypen{n}`: `EVSE-ID{n}` gültig → `ExternalId` = normalisierte EVSE-ID ohne `*` (`DEUFCE011211`), `EvseId` = roher Wert; sonst synthetischer Schlüssel `<Ladeeinrichtungs-ID>/LP<n>` ohne EVSE-ID (Matching über die Geo-Kaskade). Dieselbe EVSE-ID zweimal in einer Zeile → der zweite Ladepunkt bekommt den synthetischen Schlüssel (Issue im Log).
- `Steckertypen{n}` → je Steckertyp ein `Connector` (Tabelle unten), `Nennleistung Stecker{n}` je Stecker (ein Wert gilt für alle); `MaxPowerKw` des Ladepunkts = stärkster Stecker; `PowerType` = Stromart der Stecker, wenn eindeutig.
- `Bezahlsysteme` → `AuthMethods` (Tabelle unten; leer → `null`), `Kostenlos`/`Kostenlos (Registrierung)` außerdem → `AdHocPrice` „kostenlos“ (`FreeOfCharge`). `Roaming` bleibt `null`, `Facilities` bleibt `null` (die Quelle sagt nichts dazu).
- `Status` → Tabelle unten: `In Betrieb` ergibt keinen Status (Lebenszyklus `ACTIVE` folgt daraus, dass die Zeile geliefert wird), `In Wartung` → `INOPERATIVE` als Statusmeldung mit dem Rohwert.
- `raw_snapshot`: alle befüllten Spalten der Zeile als JSON-Objekt (ohne `Public Key{n}`).

### Abdeckung einer Fläche
Ein Inventar-Plan für alle Regionen aus `provider.regions` (Box = umschließendes Rechteck, [05](../05-crawling-konzept.md) Abschnitt 1): der Lauf liest die ganze Datei und behält die Zeilen in der Box; Zeilen außerhalb werden gezählt („Bundesnetzagentur register read: 116443 facilities, … outside the box, … skipped“). Mit allen 16 Bundesländern dauert der Merge eines Laufs im Worker etwa eine Stunde.

## Status-Mapping

Datei `src/ChargeGlobe.Providers.BNetzA/Mapping/status-mapping.json`.

| Rohwert | OCPI-Status |
|---|---|
| `In Betrieb` | `UNKNOWN` = keine Statusmeldung |
| `In Wartung` | `INOPERATIVE` |
| alles andere | `UNKNOWN` = keine Statusmeldung (Rohwert in `StatusRaw`) |

## Stecker-Mapping

Datei `src/ChargeGlobe.Providers.BNetzA/Mapping/connector-mapping.json` (Standard, Steckdose/Kabel, Stromart).

| Rohwert | ConnectorStandard | Format | PowerType |
|---|---|---|---|
| `AC Typ 2 Steckdose` | `IEC_62196_T2` | `SOCKET` | `AC_3_PHASE` |
| `AC Typ 2 Fahrzeugkupplung` | `IEC_62196_T2` | `CABLE` | `AC_3_PHASE` |
| `AC Typ 1 Steckdose` | `IEC_62196_T1` | `SOCKET` | `AC_1_PHASE` |
| `AC Schuko` | `DOMESTIC_F` | `SOCKET` | `AC_1_PHASE` |
| `AC CEE 3-polig` | `UNKNOWN` (Stromstärke unbekannt) | `SOCKET` | `AC_1_PHASE` |
| `AC CEE 5-polig` | `UNKNOWN` (Stromstärke unbekannt) | `SOCKET` | `AC_3_PHASE` |
| `DC Fahrzeugkupplung Typ Combo 2 (CCS)` | `IEC_62196_T2_COMBO` | `CABLE` | `DC` |
| `DC CHAdeMO` | `CHADEMO` | `CABLE` | `DC` |
| `DC Tesla Fahrzeugkupplung (Typ 2)` | `TESLA_S` | `CABLE` | `DC` |
| `DC Megawatt Charging System (MCS)` | `UNKNOWN` (kein OCPI-2.2.1-Wert) | `CABLE` | `DC` |
| alles andere | `UNKNOWN` (Rohwert in `RawType`) | `null` | `null` |

## Zahlungs- und Authentifizierungsarten

Datei `src/ChargeGlobe.Providers.BNetzA/Mapping/auth-mapping.json` → `AuthMethod` ([03](../03-datenmodell.md)); ein Rohwert kann mehrere Arten bedeuten, unbekannte Werte fallen weg.

| Rohwert | AuthMethod |
|---|---|
| `RFID-Karte` | `RFID_READER` |
| `Onlinezahlungsverfahren` | `WEBSITE` |
| `Kreditkarte (NFC)` | `CREDIT_CARD_PAYABLE`, `CONTACTLESS_CARD_SUPPORT` |
| `Kreditkarte (Lesegerät)` | `CREDIT_CARD_PAYABLE`, `CHIP_CARD_SUPPORT` |
| `Debitkarte (NFC)` | `DEBIT_CARD_PAYABLE`, `CONTACTLESS_CARD_SUPPORT` |
| `Debitkarte (Lesegerät)` | `DEBIT_CARD_PAYABLE`, `CHIP_CARD_SUPPORT` |
| `Plug & Charge` | `CHARGING_CONTRACT` |
| `Bargeld` | `CASH` |
| `Kostenlos`, `Kostenlos (Registrierung)`, `Sonstige` | keine (`Kostenlos…` setzt den Preis „kostenlos“) |

## Preisdarstellung
Keine Preise außer „kostenlos“ (`Bezahlsysteme`). Ad-hoc-Preise für Deutschland kommen aus den DATEX-II-Feeds ([mobidata-bw](mobidata-bw.md)).

## Offene Punkte / Risiken
- Tagesaktueller Webservice (OpenAPI-Beschreibung auf E-Mail-Anfrage an `ladesaeulenregister@bnetza.de`, [10 §3.2](../10-afir-umsetzung-eu.md)); solange gilt die monatliche Datei.
- Der Link auf der Seite der Ladesäulenkarte ist der einzige Weg zur aktuellen Datei; ändert die Bundesnetzagentur die Seite, meldet die Probe „Unexpected format“ und der Lauf pausiert.
- Nur 31 % der Ladepunkte tragen eine EVSE-ID; die übrigen werden mit den DATEX-II-Quellen über die Geo-Kaskade zusammengeführt (gleicher Betreibername im Umkreis von 50 m, [03](../03-datenmodell.md)); MobiData BW führt dieselben Ladeeinrichtungen unter `BNETZA*<Ladeeinrichtungs-ID>*<n>`.
- Ein voller Lauf schreibt 116.000 Rohdatensätze nach `raw_snapshot` (etwa 1 KB je Zeile); Aufbewahrung `ingest.snapshot_retention_days`.
