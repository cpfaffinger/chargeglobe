# Endpunkt-Notiz: BEV – Adressregister (Export „Adresse Relationale Tabellen – Stichtagsdaten“)

| | |
|---|---|
| Analysiert am | 2026-10-16 |
| Analysiert von | Claude (Agent) |
| Methode | Export-Doku (PDF im ZIP: `BEV_S_AD_Adresse_Relationale_Tabellen-Stichtagsdaten-CSV_V1.5.pdf`) und Auswertung des Stichtags 01.10.2026 |
| Tier | 0 (amtlich, offen) |
| Zugang | anonym, Download ohne Key |
| ToS geprüft | `Nutzungsbedingungen.txt` / `Licensing_Conditions.txt` im ZIP (2026-10-16): uneingeschränkte Nutzungsrechte unter der Bedingung, dass alle Abgabeformen und Folgeprodukte auf die Schutzrechte und den Stichtag hinweisen; Haftungsausschluss. Auf data.gv.at als CC BY 4.0 geführt. |
| Lizenz / Attribution | CC BY 4.0; Wortlaut „© Österreichisches Adressregister, Stichtagsdaten vom TT.MM.JJJJ“ mit dem Stichtag aus `Aktualitaetsstand.txt` (Quelle `bev-address`, `meta.attribution` von `GET /geocode`) |
| Rate-Limit beobachtet | keines; ein Download je Stichtag (zweimal im Jahr), Prüfung per `HEAD` höchstens alle `address.refresh_days` Tage |
| Capabilities | keine Ladepunkt-Capabilities; liefert nur Adressen für die Adresssuche (kein `IProvider`, eigener Adapter `BevAddressRegister`) |

## Endpunkte

### `GET https://data.bev.gv.at/download/Adressregister/Adresse_Relationale_Tabellen_Stichtagsdaten_{yyyyMMdd}.zip`
- Parameter: keiner; `{yyyyMMdd}` ist der Stichtag, veröffentlicht für den **1. April** und den **1. Oktober** (die Datei erscheint einige Tage danach). Ältere Stände wandern nach `…/download/Archiv_Adressregister/`. Die URL-Vorlage steht in der Einstellung `address.download_url`.
- Header: `User-Agent: chargeglobe/1.0 (+https://github.com/cpfaffinger/chargeglobe)`.
- Ermittlung des neuesten Stands: `HEAD` auf die erwarteten Stichtage, neuester zuerst (1.10. und 1.4. des laufenden und des Vorjahres, soweit nicht in der Zukunft); der erste mit `200` gewinnt, sonst `404`.
- Antwort: ZIP, ~119 MB (Stichtag 01.10.2026: 118.545.233 Bytes), entpackt ~680 MB. Der Adapter lädt es in eine temporäre Datei und liest die Tabellen direkt aus dem ZIP.
- Inhalt (relevant):

| Datei | Kodierung | Inhalt |
|---|---|---|
| `Aktualitaetsstand.txt` | ISO-8859-1 | „Aktualitätsstand (Stichtag) der enthaltenen Daten: 01.10.2026“ |
| `GEMEINDE.csv` | UTF-8 mit BOM | `GKZ;GEMEINDENAME` (2.092 Gemeinden) |
| `ORTSCHAFT.csv` | UTF-8 mit BOM | `GKZ;OKZ;ORTSNAME` (17.022 Ortschaften); in Wien je Bezirk „Wien,Innere Stadt“ |
| `STRASSE.csv` | UTF-8 mit BOM | `SKZ;STRASSENNAME;STRASSENNAMENZUSATZ;SZUSADRBEST;GKZ;ZUSTELLORT;ZUSTELLORT_ID` (138.112 Straßen) |
| `ADRESSE.csv` | UTF-8 mit BOM | 2.529.222 Adressen: `ADRCD;GKZ;OKZ;PLZ;SKZ;…;HNR_ADR_ZUSAMMEN;…;HOFNAME;RW;HW;EPSG;…` |

  Nicht verwendet: `GEBAEUDE*.csv`, `ADRESSE_GST.csv`, `ZAEHLSPRENGEL.csv`.
- Format: Trennzeichen `;`, Texte in `"…"` (Anführungszeichen darin verdoppelt), Zahlen ohne Anführungszeichen mit `.` als Dezimaltrenner. Ein `;` kommt in Feldern in Anführungszeichen vor (z. B. `HOFNAME` „Hof; Nr 4“, vier Zeilen am 01.10.2026), deshalb liest der Adapter die Felder mit Rücksicht auf Anführungszeichen.
- Beispielzeile (`ADRESSE.csv`):
```
"6793782";"90001";"17223";"1010";"904712";"90101013";"";1;"";"";;"";"keine Angabe";"1";0;"";3007.82;341148.29;31256;"G";"Q"
```
- Mapping auf das Modell (Tabelle `address`, docs/03):
  - `ADRCD` → `id`
  - `STRASSE.STRASSENNAME` über `SKZ` → `street`
  - `HNR_ADR_ZUSAMMEN` → `house_number` („1“, „3A“, „12/3“)
  - `PLZ` → `postal_code`
  - `ORTSCHAFT.ORTSNAME` über `GKZ`+`OKZ` → `city`; nennt die Ortschaft nur einen Teil ihrer Gemeinde („Wien,Innere Stadt“), ist `city` die Gemeinde („Wien“); unbekannte Ortschaft → Gemeinde
  - `GKZ` → `municipality_code`, `GEMEINDE.GEMEINDENAME` geht in den Suchtext
  - `RW`, `HW`, `EPSG` → `geom` per `ST_Transform` nach WGS 84; `EPSG` ist 31254, 31255 oder 31256 (MGI / Austria GK West, Central, East)
  - Straße, Hausnummer, PLZ, Ortschaft und Gemeinde → `search_text` (klein, „ß“ als „ss“, Satzzeichen als Leerzeichen)
- Übersprungen werden Zeilen mit falscher Feldzahl, ohne Code, Koordinaten oder bekannte Projektion, mit unbekannter Straße oder Gemeinde (am 01.10.2026: keine; jede Adresse hat eine Straße).

## Status-Mapping

Entfällt (keine Ladepunkte).

## Stecker-Mapping

Entfällt.

## Preisdarstellung

Entfällt.

## Offene Punkte / Risiken

- Der Dateiname folgt der Vorlage `…_Stichtagsdaten_{yyyyMMdd}.zip`. Ändert das BEV ihn, findet `HEAD` keinen Stand (Log „no release of the address register found“); dann `address.download_url` anpassen.
- Die Nutzungsbedingungen im ZIP vom 01.10.2026 nennen im Beispiel noch den Stichtag 01.04.2026; maßgeblich für die Attribution ist `Aktualitaetsstand.txt`.
- Die Koordinaten stammen aus MGI-Gauß-Krüger; die Transformation von PostGIS (`+towgs84`) ist auf rund 1 m genau, für Start und Ziel einer Route ausreichend.
