# 10 – AFIR Art. 20 in den EU-Ländern: Zugang zu Inventar, Status und Preisen

Stand: 17. Oktober 2026. Bericht für den Betreiber von chargeglobe: Was in jedem EU-Land nötig ist, um das zu tun, was in Österreich über die E-Control läuft (Inventar der öffentlichen Ladepunkte, Belegungsstatus je Ladepunkt, Ad-hoc-Preis). Kennzeichnung wie in [08](08-datenquellen-und-recht.md): **[V]** am Primärdokument geprüft, **[V-sek]** belastbare Sekundärquelle, **[U]** nicht belegbar. Deutschland steht am Anfang und ist am ausführlichsten, weil es der nächste Markt ist ([09](09-roadmap.md), Phase 3).

## 1. Kurzfassung

- **AFIR verpflichtet die Betreiber, nicht die Staaten, zur Datenlieferung.** Jeder Ladepunktbetreiber muss seit 14.04.2025 statische und dynamische Daten (Status, Verfügbarkeit, Ad-hoc-Preis) kostenlos über eine API bereitstellen und sie beim nationalen Zugangspunkt (NAP) anmelden; seit 14.04.2026 im Format DATEX II. Ob der NAP die Daten **selbst aggregiert** oder nur einen **Katalog der Betreiber-APIs** führt, entscheidet jedes Land selbst. Genau das bestimmt den Aufwand für chargeglobe.
- **Drei Ländertypen:**
  1. **Zentraler Feed, offen** (ohne Vertrag abrufbar): Finnland, Litauen, Niederlande, Frankreich (ohne Preise), Schweden (NOBIL, Key per Formular), Polen (EIPA, Registrierung per E-Mail), Slowenien (Registrierung mit Freigabe), Österreich (E-Control-Key).
     Dazu Portugal (DATEX II mit Status, Lizenz ungeklärt), Griechenland (JSON mit Status, CC BY 4.0), Spanien (DATEX II, nur Inventar, CC BY), Zypern (nur 171 Punkte).
  2. **Nur Betreiber-Feeds über den NAP** (je Betreiber ein Abonnement, meist Zertifikat oder Token): Deutschland, Belgien, Dänemark, Ungarn.
  3. **Noch kein nutzbarer Ladepunkt-Feed**: Italien (PUN nur als Webkarte), Tschechien, Slowakei, Rumänien, Bulgarien, Kroatien, Malta, Estland, Irland (im Aufbau), Luxemburg.
- **Deutschland:** Es gibt keinen zentralen amtlichen Feed mit Echtzeitstatus. Das Inventar liefert die Bundesnetzagentur (CSV/XLSX, monatlich, CC BY 4.0, seit 2026 **mit EVSE-IDs**); Status und Preise kommen nur über die **Mobilithek**, dort aber je Betreiber oder Dienstleister als DATEX-II-JSON-Angebot mit Client-Zertifikat. Für einen schnellen Start existiert **MobiData BW** als frei zugänglicher Aggregator der wichtigsten Mobilithek-Feeds (DATEX II und OCPI 3.0, ganz Deutschland, Lizenz DL-DE-BY 2.0). Was der Betreiber dafür tun muss, steht in Abschnitt 3.
- **Was der Betreiber überall selbst erledigen muss:** Konten und Zertifikate laufen auf eine natürliche oder juristische Person; die Entwicklung braucht danach nur Key, Zertifikat oder Subscription-ID und den Lizenztext für die Attribution. Alle Zugangsdaten werden ausschließlich im Admin-UI (`provider.api_key`, Scope `provider:<key>`) hinterlegt, nie in Dateien ([CLAUDE.md](../CLAUDE.md)).

## 2. Rechtsrahmen auf EU-Ebene

### AFIR, Verordnung (EU) 2023/1804, Art. 20 [V]
[EUR-Lex 2023/1804](https://eur-lex.europa.eu/legal-content/EN/TXT/HTML/?uri=CELEX:32023R1804)

| Absatz | Pflicht | Frist |
|---|---|---|
| (1) | Jeder Mitgliedstaat benennt eine **ID-Registrierungsorganisation (IDRO)**, die eindeutige Kennungen mindestens für Ladepunktbetreiber (CPO) und Mobilitätsdienstleister (MSP) vergibt | 14.04.2025 |
| (2) | Betreiber öffentlich zugänglicher Ladepunkte stellen **kostenlos** bereit: (a) statisch: Standort, Zahl der Stecker, Behindertenparkplätze, Kontakt, Öffnungszeiten; (b) ID-Codes (mind. Betreiber), Steckertyp, AC/DC, maximale Leistung je Station und Punkt, Fahrzeugkompatibilität; (c) dynamisch: Betriebsstatus, Verfügbarkeit (belegt/frei), **Ad-hoc-Preis**, 100 % erneuerbar. (c) entfällt für kostenlose Ladepunkte | 14.04.2025 |
| (3) | Jeder Betreiber richtet eine **API mit freiem, uneingeschränktem Zugang** ein und meldet sie beim NAP | 14.04.2025 |
| (4) | Die Mitgliedstaaten machen die Daten offen und diskriminierungsfrei über ihren NAP zugänglich (i. V. m. DelVO 2022/670); Aggregation im NAP ist erlaubt, nicht Pflicht | 31.12.2024 |
| (5) | Die Kommission errichtet einen **Common European Access Point (CEAP)** als Gateway zu den NAPs | 31.12.2026 |
| (6)–(8) | Delegierte und Durchführungsrechtsakte zu Datentypen, API-Anforderungen, Format, Frequenz, Qualität; Übergangsfristen | – |

### Durchführungsverordnung (EU) 2025/655 [V]
[EUR-Lex 2025/655](https://eur-lex.europa.eu/legal-content/EN/TXT/HTML/?uri=CELEX:32025R0655), anwendbar seit 14.04.2025.

- **Art. 1 Format:** Datentypen nach Anhang (7 Kategorien, 44 Typen); ab **14.04.2026** DATEX II für alternative Kraftstoffe, mindestens **CEN/TS 16157-10:2022**. Tabelle B.1: die Ladepunkt-ID enthält die IDRO-Betreiber-ID; Tabelle F.3: Ad-hoc-Preis mit allen Komponenten je kWh, Minute oder Sitzung in Landeswährung.
- **Art. 2 Frequenz:** statische Daten bei Änderung, spätestens nach **24 h**; dynamische Daten bei Änderung, spätestens nach **1 min**.
- **Art. 3 Qualität:** Vollständigkeit, Korrektheit, Konsistenz (keine Duplikate), Aktualität; Dritte dürfen die API im Auftrag betreiben, die Verantwortung bleibt beim Betreiber.
- **Art. 4:** Die Mitgliedstaaten überwachen die über den NAP zugänglichen Daten. **Es gibt keine Pflicht, dass der NAP selbst eine API anbietet** (ein Katalog genügt) und **keine Lizenzvorgabe** für die Weiterverwendung; AFIR verlangt nur „kostenlos, offen, diskriminierungsfrei“. Deshalb reichen die Lizenzen in der Praxis von CC0 bis „nur mit Erlaubnis“.
- Der delegierte Rechtsakt zu zusätzlichen Datentypen (Art. 20 Abs. 6 lit. a) wurde laut Branchenberichten am 03.04.2025 finalisiert [V-sek]; Nummer und Inhalt sind nicht geprüft [U].

### Europäische Ebene: CEAP, NAPCORE, IDRR
- **CEAP:** gesetzlich bis 31.12.2026; NAPCORE rechnet mit Betrieb „by 2027“ ([Qualitätsrahmen AF v1.1](https://napcore.eu/wp-content/uploads/2025/06/NAPCORE-Quality-Framework-for-AF-v1.1.pdf)) [V]. Ein nutzbarer CEAP existierte am 17.10.2026 nicht [U]. Die AFIR-Überprüfung (Konsultation seit 12.05.2026) nennt die stärkere Harmonisierung der NAPs als mögliche Maßnahme ([EAFO](https://alternative-fuels-observatory.ec.europa.eu/general-information/news/afir-review-shaping-europes-alternative-fuels-network)) [V]. Für chargeglobe heißt das: bis mindestens 2027 bleibt es bei einem Adapter je Land bzw. je Betreiber-Feed.
- **NAPCORE** ([NAP-Liste](https://napcore.eu/description-naps/national-access-point/), [Kommissionsliste 10/2025](https://transport.ec.europa.eu/document/download/963c997d-efd9-40ae-a38b-5d4b935bdfcf_en?filename=its-national-access-points.pdf)) [V]: keine Liste enthält eine AFIR-Spalte. Der Qualitätsrahmen (06/2025) stellt fest, dass fast die Hälfte der NAPs keine Daten zu alternativen Kraftstoffen führt und der Zugang „challenging“ sei [V]. Eine öffentliche Übersicht „wo liegen Ladedaten live“ gibt es nicht; dieser Bericht ersetzt sie für chargeglobe.
- **Profile:** CEN/TS 16157-11:2025 („Energy infrastructure“) ist seit 09.10.2025 veröffentlicht [V-sek]; Deutschland hat ein eigenes AFIR-Recharging-Profil v01-00-00 (DATEX II v3, JSON) ([GitHub MobilithekDE](https://github.com/MobilithekDE/AFIR-DATEX-II-Recharging-Profil)) [V]. Die Länder nutzen unterschiedliche DATEX-Versionen (3.2 bis 3.7) und Syntaxen (XML, JSON); der generische DATEX-II-Adapter ([09](09-roadmap.md), Phase 3) muss das Profil je Quelle konfigurierbar halten.
- **IDRR** ([EAFO](https://alternative-fuels-observatory.ec.europa.eu/markets-and-policy/policy-insights/identification-registration-repository)) [V]: Verzeichnis der nationalen IDROs. Benannt: AT (E-Control), Benelux (gemeinsam), DE (BDEW/Energie Codes und Services), DK, ES (RIPREE), FI (Traficom), FR (AFIREV/Avere), GR, HR, HU, IE (TII), LT, LV (LVC), PL (UDT/EIPA), PT (MOBI.E), SI, SK; „soon“: BG, CY, CZ, EE, IT, MT, RO, SE. Die IDRO-Betreiberlisten sind für chargeglobe die Quelle für `operator_id` je Land (wie heute das E-Control-Betreiberregister).
- **Vertragsverletzungsverfahren** zu Art. 20 wurden für 2025/2026 nicht gefunden [U]; die Durchsetzung liegt bei den Mitgliedstaaten.

## 3. Deutschland

### 3.1 Quellenlage

| Bedarf | Quelle | Befund |
|---|---|---|
| **Inventar** | Bundesnetzagentur **Ladesäulenregister** | CSV (51 MB) und XLSX (28 MB) unter [bundesnetzagentur.de](https://www.bundesnetzagentur.de/DE/Fachthemen/ElektrizitaetundGas/E-Mobilitaet/Ladesaeulenkarte/start.html), **CC BY 4.0** (Quellenvermerk „Bundesnetzagentur.de“), **monatlich** aktualisiert (Datei vom 01.09.2026, Stand XLSX 30.09.2026) [V]. Ein **Webservice** liefert denselben Datensatz **täglich**; Schnittstellenbeschreibung (OpenAPI) und Nutzungsvoraussetzungen gibt es nur auf E-Mail-Anfrage an `ladesaeulenregister@bnetza.de` ([Schnittstellen](https://www.bundesnetzagentur.de/DE/Fachthemen/ElektrizitaetundGas/E-Mobilitaet/Schnittstellen/start.html)) [V]. 212.064 öffentliche Ladepunkte (09/2026) [V-sek] |
| **Status (Echtzeit)** | Bundesnetzagentur | **nicht vorhanden** [V]. Die Spalte `Status` ist der Lebenszyklus („In Betrieb“), kein Belegungsstatus |
| **Status + Ad-hoc-Preis** | **Mobilithek** (NAP, BMV/BASt) | je Betreiber oder Dienstleister zwei Angebote `AFIR-recharging-stat-<Name>` und `AFIR-recharging-dyn-<Name>`, DATEX II v3.5 als **JSON** (Profil AFIR-Recharging 01-00-00), Pull mit Client-Zertifikat und Subscription-ID oder Push an einen eigenen Endpunkt; **kein zentrales Aggregat** [V] |
| **Aggregat ohne Registrierung** | **MobiData BW** (NVBW) | DATEX II v3.5 JSON `static`/`realtime` und OCPI 3.0 unter `api.mobidata-bw.de`, Quellen: BNetzA (statisch) plus die Mobilithek-Feeds von chargecloud, e-clearing.net, Eco-Movement, EnBW, Midorion, TC-Backend und Tesla sowie OCHP Albwerk, PBW, Heilbronn; Bounding Box ganz Deutschland; Lizenz **DL-DE-BY 2.0**; ohne Key [V] ([Datensatz](https://mobidata-bw.de/dataset/e-ladesaulen), [Quellenliste](https://api.mobidata-bw.de/ocpdb/api/public/v1/sources)). Rate-Limits und Nutzungsbedingungen der API sind nicht dokumentiert [U] |
| **Betreiber-IDs** | **bdew-codes.de** (IDRO: Energie Codes und Services GmbH im Auftrag des BDEW) | [Operator-ID-Liste](https://bdew-codes.de/Codenumbers/EMobilityId/OperatorIdList) mit [Excel-Export](https://bdew-codes.de/Codenumbers/EMobilityId/DownloadActiveEVSECodes) (1.743 Einträge, 17.10.2026), ohne Konto [V] |
| Ergänzung | Open Charge Map | 24.643 POIs für DE (17.10.2026) gegenüber 212.064 Ladepunkten im Register; kein BNetzA-Import [V] |
| Regional | Bonn (chargecloud POI, Echtzeit, Lizenz widersprüchlich), Hamburg (eRound über Mobilithek), Berlin (nur statisch) | nur punktuell nützlich |

**Format des BNetzA-CSV** (geprüft am Download vom 01.09.2026) [V]: UTF-8 mit BOM, Semikolon, Dezimalkomma, 10 Vorspannzeilen; eine Zeile je Ladeeinrichtung mit bis zu 6 Ladepunkten. Spalten: `Ladeeinrichtungs-ID` (stabiler 7-stelliger Schlüssel, überdauert Betreiberwechsel), `Betreiber`, `Anzeigename (Karte)`, `Status`, `Art der Ladeeinrichtung`, `Anzahl Ladepunkte`, `Nennleistung Ladeeinrichtung [kW]`, `Inbetriebnahmedatum`, Adresse, `Bundesland`, `Breitengrad`, `Längengrad`, `Standortbezeichnung`, `Informationen zum Parkraum`, `Bezahlsysteme`, Öffnungszeiten, dann je Ladepunkt `Steckertypen{n}`, `Nennleistung Stecker{n}`, **`EVSE-ID{n}`**, `Public Key{n}`. Die EVSE-ID ist im Meldeportal optional („falls vorhanden“) und daher nur teilweise befüllt; Matching braucht wie heute die Geo-Kaskade ([03](03-datenmodell.md)) als Rückfall.

**Mobilithek-Angebote** (GovData-Harvest, 17.10.2026) [V]: etwa 26 statische und 24 dynamische Angebote, u. a. EnBW mobility+ (CC BY 4.0), Tesla, Eco-Movement, chargecloud, e-clearing.net, Wirelane, Monta, VW Group Charging, Audi charging hub, E.ON Drive Infrastructure, LichtBlick, Qwello, SMATRICS, GP Joule, EWE, Hamburger Energienetze (eRound), vaylens. Lizenzen laut GovData: 36 × CC0, 8 × CC BY 4.0, 8 × „andere geschlossene Lizenz“. Die Angebote lassen sich nicht über die Mobilithek-Oberfläche (reine Single-Page-App) enumerieren, wohl aber über die [GovData-CKAN-API](https://www.govdata.de/ckan/api/3/action/package_search?q=AFIR-recharging&rows=200). Eine Drittauswertung ([hocsy.eu](https://hocsy.eu/de/auswertungen/cpo_afir_meldestatus.html)) schätzt den Meldestatus je Betreiber [V-sek].

**Rechtslage** [V]: Die *Verordnung zur Neuordnung des Ladesäulenrechts* ([BGBl. 2025 I Nr. 367](https://www.recht.bund.de/bgbl/1/2025/367/regelungstext.pdf?__blob=publicationFile&v=3)) ersetzt seit 01.01.2026 die LSV 2016: § 4 Anzeige der Inbetriebnahme binnen zwei Wochen, § 5 Prüfung der AFIR-Anforderungen mit Betriebsuntersagung, § 6 monatliche Daten an die Eichbehörden; **keine** Regelung zu dynamischen Daten, zur Mobilithek oder zu Art. 20. Die Datenpflicht nach Art. 20 läuft unmittelbar über den NAP ([Mobilithek-Leitfaden 08/2025](https://raw.githubusercontent.com/MobilithekDE/AFIR-DATEX-II-Recharging-Profil/main/Mobilithek-Leitfaden_fuer_AFIR-Recharging-Datenangebote_250805.pdf)). Das Kabinett hat am 29.10.2025 den Entwurf eines neuen IVSG beschlossen, das der Mobilithek eine gesetzliche Grundlage gibt ([BMV](https://www.bmv.de/SharedDocs/DE/Pressemitteilungen/2025/053-schnieder-baustellen-freie-ladesaeulen-oepnv-auslastung.html)); Verabschiedung und Sanktionen [U]. Für die THG-Quote ist seit 2026 die vollständige EVSE-ID je Ladepunkt Pflicht ([BAnz AT 30.06.2026 B9](https://www.umweltbundesamt.de/system/files/document/BAnz%20AT%2030.06.2026%20B9.pdf)), was die Befüllung im Register verbessern dürfte.

### 3.2 Was der Betreiber bereitstellen muss

Reihenfolge nach Nutzen. Schritte 1, 2 und 5 brauchen kein Konto und können sofort in die Entwicklung gehen.

1. **Bundesnetzagentur-Liste** (Inventar): nichts zu beantragen. Attribution „Bundesnetzagentur.de“ in `meta.attribution`.
2. **MobiData BW** (Status und Preise der wichtigsten Betreiber ohne Registrierung): nichts zu beantragen. Vor dem Produktivbetrieb eine kurze Mail an `mobidata-bw@nvbw.de` mit Projektbeschreibung und Abfragefrequenz, um Rate-Limits und Nutzungsbedingungen der API zu klären (Antwort an die Entwicklung weitergeben). Attribution nach DL-DE-BY 2.0: „Datenquelle: MobiData BW / NVBW, dl-de/by-2-0“.
3. **Bundesnetzagentur-Webservice** (tagesaktuelles Inventar statt monatlich): E-Mail an `ladesaeulenregister@bnetza.de` mit Projektbeschreibung (offene Datenbank, nicht kommerziell, Attribution), Bitte um die OpenAPI-Beschreibung und die Nutzungsvoraussetzungen. Weiterzugeben: die Beschreibung und etwaige Zugangsdaten.
4. **Mobilithek** (alle Betreiber-Feeds, auch die, die MobiData BW nicht spiegelt) ([BASt-Anleitung](https://www.bast.de/DE/Publikationen/Daten/VerhaltenundSicherheit/MDC/Datenbezug/Datenbezug_node.html), [Schnittstellenbeschreibung 1.3.2](https://mobilithek.info/cms/assets/65545c25-c155-488c-9c7c-b9da1c7685b5?download=)) [V]:
   1. Nutzerkonto unter [mobilithek.info/registration-request](https://mobilithek.info/registration-request) mit einer **institutionellen E-Mail-Adresse** (z. B. `…@chargeglobe.net`), Bestätigungslink, Profil, AGB.
   2. **Organisation registrieren** („Organisation registrieren“; eine Organisation kann Firma, Behörde, Hochschulinstitut oder „andere Einheit“ sein, eine Rechtsform wird nicht verlangt). Die Mobilithek prüft und schaltet per E-Mail frei; der Antragsteller wird Organisationsadministrator. Dauer der Prüfung [U].
   3. Rolle **Bestellmanagement** für das eigene Konto setzen (Voraussetzung zum Abonnieren).
   4. **Maschinenkonto** unter „Mein Profil“ → „Maschinenkonto hinzufügen“: Zertifikat von der Mobilithek erzeugen lassen (Mobilfunknummer nötig; das X.509-Zertifikat kommt als `.p12` per E-Mail, das Passwort per SMS) oder eigenes Zertifikat hochladen. Es gibt **keinen API-Key**, der Zugriff läuft ausschließlich über das Client-Zertifikat.
   5. Die gewünschten Angebote `AFIR-recharging-stat-*` und `AFIR-recharging-dyn-*` abonnieren (Liste über die GovData-CKAN-API, siehe oben); je Angebot die Lizenz lesen. Angebote ohne „Genehmigung erforderlich“ sind sofort aktiv.
   6. **An die Entwicklung übergeben** (nur über das Admin-UI, nie per Datei im Repo): `.p12`-Datei und SMS-Passwort, die CA-Kette von [mobilithek.info/help/download](https://mobilithek.info/help/download), je Angebot die **Subscription-ID** (nicht die Angebots-ID), die Lizenz des Angebots und den Namen des Dateneigners für die Attribution. Angebote unter „andere geschlossene Lizenz“ nur abonnieren, wenn der Lizenztext die Weiterveröffentlichung erlaubt.
5. **Betreiber-IDs**: Excel-Export von bdew-codes.de ohne Konto; die Entwicklung baut daraus den Seed der deutschen `operator_id`-Liste, analog zum E-Control-Betreiberregister.
6. **Open Charge Map**: der vorhandene Key ([ocm](endpunkte/ocm.md)) gilt auch für Deutschland; nur Lückenfüller.
7. **Beobachten**: IVSG-Verabschiedung; Verlagerung der Inbetriebnahme-Meldungen zur Mobilithek [V-sek]; ob die Bundesnetzagentur den Webservice irgendwann um DATEX II oder Status erweitert.

### 3.3 Was die Entwicklung danach baut
- Adapter `bnetza` (Tier `OFFICIAL`, Inventar aus CSV oder Webservice, Matching über EVSE-ID, sonst Geo + Betreiber + Steckerkonfiguration; `Ladeeinrichtungs-ID` als stabiler Quellschlüssel).
- Generischer **DATEX-II-Adapter** ([09](09-roadmap.md), Phase 3) mit konfigurierbarem Profil: zuerst MobiData BW (`static`/`realtime`, JSON v3.5), dann Mobilithek-Pull je Subscription-ID (`GET https://mobilithek.info:8443/mobilithek/api/V1.0/subscription?subscriptionID=…`, Client-Zertifikat, Pflicht-Header `Accept-Encoding: gzip`, `If-Modified-Since` für Delta-Pakete, 204 = kein Paket, 304 = nichts Neues, 404 bei Überschreitung des nicht dokumentierten Zugriffslimits). Status-Mapping `RefillPointStatusEnum` → OCPI steht in [08 §7](08-datenquellen-und-recht.md).
- Region-Seed der 16 Bundesländer (`DE-BW` … `DE-TH`) aus OSM wie `regions-at.geojson`.
- Attribution je Quelle in `meta.attribution`; Lizenz je Mobilithek-Angebot als Einstellung je Feed.

## 4. Was chargeglobe je Land braucht

Für jedes Land dieselben vier Fragen, die in Österreich die E-Control auf einmal beantwortet:

| Frage | Warum |
|---|---|
| Gibt es einen **zentralen Feed** oder nur Betreiber-Feeds? | ein Adapter gegen eine Quelle oder ein Adapter je Betreiber mit je eigenem Zugang |
| Liefert er **Echtzeitstatus** (≤ 1 min) und **Ad-hoc-Preis** strukturiert? | sonst nur Inventar, kein Statustracking und keine Preishistorie |
| **Zugang und Lizenz**: offen, Registrierung, Zertifikat, Vertrag; erlaubt die Lizenz die Weiterveröffentlichung über die chargeglobe-API? | bestimmt, was der Betreiber persönlich erledigen muss und was in `meta.attribution` steht |
| Wer ist die **IDRO**, und ist die Betreiberliste öffentlich? | Quelle für `operator_id` und Netz-Zuordnung der Tarife ([ADR-0013](adr/0013-ladetarife-netze-zonen-katalog.md)) |

## 5. Übersicht aller EU-Länder

Status live = ein Feed mit Belegungsstatus ist am 17.10.2026 tatsächlich abrufbar. Land-Abschnitte in 6.

| Land | NAP (Betreiber) | zentral / je Betreiber | Status live | Preise | Format | Zugang | Lizenz |
|---|---|---|---|---|---|---|---|
| **AT** | mobilitaetsdaten.gv.at (AustriaTech); Daten bei E-Control | zentral (E-Control) | ja, in Nutzung | ja | JSON-API; DATEX II 3.2/3.5 | API-Key (Registrierung) | eigene Nutzungsbedingungen |
| **BE** | transportdata.be (NGI) | je Aggregator (Eco-Movement, EnergyVision, Indigo, Gireve) | teilweise | ja (Eco-Movement, EnergyVision) | DATEX II v3 JSON / v3.7 XML | Token per E-Mail | CC BY 4.0, ODC-BY, k. A. |
| **BG** | sipbg.gov.bg/bgnap (MTC), datasheet.api.bg (API) | keine Ladedaten | nein | nein | – | Registrierung + API-Key | keine [U] |
| **CY** | traffic4cyprus.org.cy (Verkehrsministerium/EMS) | zentral, lückenhaft (171 Punkte, EAC fehlt) | nur Betriebszustand | nein | DATEX-II-ähnliches XML (nicht schemakonform) | offen | CC BY 4.0 |
| **CZ** | registr.dopravniinfo.cz (ŘSD/NDIC) | keine Ladedaten im NAP; MPO-XLSX statisch | nein | nein | XLSX | offen | unklar |
| **DE** | mobilithek.info (BMV/BASt) | je Betreiber/Dienstleister (~25); BNetzA nur Inventar; MobiData BW als Aggregat | ja (Mobilithek, MobiData BW) | ja | DATEX II v3.5 JSON; OCPI 3.0 (MobiData) | BNetzA offen; Mobilithek Organisation + Zertifikat; MobiData offen | CC BY 4.0 (BNetzA), CC0/CC BY/geschlossen (Mobilithek), DL-DE-BY 2.0 (MobiData) |
| **DK** | nap.vd.dk (Vejdirektoratet); Klimadatastyrelsen OCPI | je Betreiber („restricted“); OCPI-Sammlung im Aufbau | unklar [U] | [U] | DATEX II / OCPI | Login, Zweckbindung AFIR | unbekannt [U] |
| **EE** | andmed.eesti.ee (RIA/Transpordiamet) | je Betreiber (Katalog) | nein (0 Datensätze) | nein | DATEX II je Betreiber | je Betreiber | je Betreiber |
| **ES** | nap.dgt.es (DGT); Dynamik im SGV von MITECO | zentral, nur statisch (35.608 EVSE) | nein | nein | DATEX II v3 Table, 24 h | offen | CC BY |
| **FI** | Digitraffic afir.digitraffic.fi (Fintraffic) | zentral (18 Betreiber, ~50 %) | ja | teilweise | GeoJSON/JSON, DATEX II 3.6, MQTT | offen | CC BY 4.0 |
| **FR** | transport.data.gouv.fr (DGITM) + Qualicharge | zentral (~114.000 Ladepunkte dynamisch) | ja, mit Lücken | nein (nur Freitext) | CSV (irve-statique/-dynamique 2.3.1) | offen | Licence Ouverte 2.0 |
| **GR** | electrokinisi.yme.gov.gr (Verkehrsministerium) | zentral (10.085 EVSE) | ja (10 min) | nein | JSON (OCPI-nah), ZIP | offen | CC BY 4.0 |
| **HR** | promet-info.hr (Hrvatske ceste) | keine Ladedaten | nein | nein | DATEX II 2.3 (nur Verkehr) | Login | – |
| **HU** | napportal.kozut.hu (Magyar Közút) | je Betreiber (1 dynamisch, 3 statisch) | lückenhaft | [U] | DATEX II v3.3 XML | Registrierung + Freigabe | CC BY 4.0 |
| **IE** | data.gov.ie (Katalog) + TII-DXP | zentral geplant (DXP) | nein (Onboarding) | geplant | OCPI 2.2.1 Eingang; Ausgabe offen | Anfrage AFIRdata@tii.ie | CC BY 4.0 (TII) [U] |
| **IT** | cciss.it (MIT); fachlich PUN (MASE/GSE/RSE) | zentral in der PUN, nur Webkarte | nein (kein Feed) | nein | Webkarte, kein DATEX II | kein offener Zugang | keine |
| **LT** | eismoinfo.lt NPP + ev.vialietuva.lt (Via Lietuva) | zentral (13 Betreiber, 5.820 Ladepunkte) | ja | ja (OCPI tariffs) | DATEX II v3 + OCPI 2.3.0 | offen | CC BY 4.0 / ODC-BY |
| **LU** | data.public.lu (Ponts et Chaussées) | nur Chargy (KML/WMS) | fraglich | nein | KML, WMS | offen | CC0 (KML) |
| **LV** | transportdata.gov.lv (LVC) | zentral, nur statisch | nein | nein | DATEX II v3 XML | Registrierung + API-Key | CC0 1.0 |
| **MT** | geoservices.transport.gov.mt (Transport Malta) | staatliches Netz Charge My Ride, kein Datensatz | nein | nein | – | – | – |
| **NL** | ntm.ndw.nu / DOT-NL (NDW) | zentral (~40 CPOs) | ja | ja (OCPI tariffs) | OCPI 2.2.1 JSON, GeoJSON | offener Download | nicht angegeben [U] |
| **PL** | kpd.gddkia.gov.pl (GDDKiA) + EIPA (UDT) | zentral (EIPA → KPD) | ja | ja | JSON (EIPA); DATEX II 3.5 SOAP (KPD) | EIPA: E-Mail-Registrierung; KPD: Formular + Zertifikat | keine benannte Lizenz; Weiterverbreitung erlaubt |
| **PT** | nap-portugal.imt-ip.pt (IMT); Daten bei MOBI.E | zentral (19.265 EVSE) | ja (5 min) | nur Mindestgebühr | DATEX II v3 Table + Status | offen (ETag) | nicht angegeben [U] |
| **RO** | pna.cestrin.ro (CESTRIN/CNAIR) | keine Ladedaten | nein | nein | GeoJSON (Straßendaten) | Konto (~24 h) | nur mit Erlaubnis |
| **SE** | trafficdata.se (Trafikverket, nur Metadaten); NOBIL | zentral über NOBIL | ja | [U] | NOBIL-JSON | API-Key info.nobil.no | CC BY 4.0 |
| **SI** | nap.si (NCUP/MZIE) | zentral (4.673 EVSE) | ja (lückenhaft gepflegt) | ja (Preis/kWh, statisch) | DATEX II v3.6 | Registrierung + Freigabe, OAuth2 | CC BY-SA 4.0 |
| **SK** | kein NAP (NSDI abgeschaltet; eNRI DOP bis 2028) | – | nein | nein | – | – | – |

## 6. Länder im Einzelnen

### Österreich (Referenz)
Umgesetzt und in Betrieb, Details in [08 §1–2](08-datenquellen-und-recht.md) und der [Endpunkt-Notiz](endpunkte/econtrol.md). Zentrales Register mit Meldepflicht (§ 4a Bundesgesetz BGBl. I 38/2018, Ladepunkt-Daten-VO BGBl. II 257/2024), Echtzeit-Verfügbarkeit und Ad-hoc-Preis je Ladepunkt, 14.793 Ladestellen; API-Key an eine Domain gebunden, 2.500 Requests/h; der NAP mobilitaetsdaten.gv.at verweist auf die E-Control, die DATEX II v3.2/3.5 selbst erzeugt. IDRO ist die E-Control (Betreiberregister auf admin.ladestellen.at). Offener Punkt bleibt die schriftliche Klärung der Weiterverbreitung ([09](09-roadmap.md)).

### Belgien
- **NAP:** [transportdata.be](https://transportdata.be/pages/information-for-dataproviders) (CKAN), Nationaal Geografisch Instituut für das ITS-Steering-Committee, `contact@transportdata.be` [V]. Ein nationaler Erlass zu Art. 20 wurde nicht gefunden [U]; EVSE-IDs vergibt die gemeinsame **Benelux-IDRO** ([Beschluss M (2020) 18](https://benelux.int/wp-content/uploads/2022/12/M202018_FR.pdf)) [V].
- **Daten nur je Aggregator**, kein Landesfeed [V]:
  - **Eco-Movement „selected CPOs“** ([Datensatz](https://transportdata.be/en/dataset/ae5bd9d5-cb5b-4a15-80af-f10e7a17ffd8), [API-Doku](https://developers.eco-movement.com/docs/belgium-datex-ii-api)): DATEX II v3 JSON; statisch `https://nap-be.eco-movement.com/datex2/v1/locations` (ganz BE, 24 h), dynamisch `…/datex2/v1/status/{evse_id}` mit Status **und Ad-hoc-Preis** (≤ 1 min); **CC BY 4.0**, kostenlos; Token per E-Mail an `support@eco-movement.com`; Limits 2 Requests/min je EVSE, 30/h für Locations, 1 je 10 s je IP. Der dynamische Feed ist nur **je EVSE** abfragbar, ein landesweites Polling ist mit diesen Limits nicht möglich; nutzbar wie der E-Control-Abruf beim Öffnen eines Standorts. Welche CPOs enthalten sind, ist nicht gelistet [U].
  - **EnergyVision** ([Datensatz](https://transportdata.be/fr/dataset/fd399aa5-8897-488e-add4-4278e69cf483)): DATEX II v3.7 AFIR-Profil XML, statisch und dynamisch inkl. Ad-hoc-Preis, minütlich, API-Key per E-Mail an `myevplatform@energyvision.be`, Lizenz nicht angegeben, seit 22.04.2026 [V].
  - **Indigo**: nur statisch, DATEX II XML, ODC-BY [V]. **Gireve EVCI**: SOAP, kostenpflichtig [V]. Monta meldet für Kunden per Proxy [V-sek].
- **Betreiber:** Token bei Eco-Movement und API-Key bei EnergyVision per E-Mail beantragen (Projektbeschreibung, Attribution); Portal-Konto auf transportdata.be nur zum Beobachten neuer Datensätze.

### Bulgarien
- **NAP:** zwei Zugangspunkte: [BGNAP](https://sipbg.gov.bg/bgnap/portal/en) des Verkehrsministeriums (MTC) für multimodale Reisedaten, technisch Esri Bulgaria; für Straßendaten die Agentur „Пътна инфраструктура“ mit datasheet.api.bg und lima.api.bg (Kommissionsliste) [V]. lima.api.bg war nicht erreichbar, datasheet.api.bg liefert nur Parkplätze und Baustellen [V].
- **Recht:** Закон за обществения транспорт (ДВ 32/01.04.2026), Art. 13: der Minister betreibt den NAP; Ladepunktbetreiber werden nicht genannt [V]. **Keine Umsetzung von Art. 20, kein Register, keine IDRO** [V]: der Entwurf des Elektromobilitätsgesetzes (Konsultation 09–10/2025) enthält keine Register- oder NAP-Bestimmung; die Bauverordnung РД-02-20-2 (02/2026) übernimmt nur AFIR-Begriffe.
- **Daten:** BGNAP nennt „Ladestationen“ und „Status von Ladestationen“ als vorgesehene Kategorien, der Katalog enthält am 17.10.2026 aber nur 14 ÖPNV-Datensätze (GTFS, NeTEx, SIRI); kein CPO registriert [V]. Zugang: Registrierung, API-Key, Sandbox; Lizenz nicht angegeben [U]. 4.495 öffentliche Ladepunkte (EAFO Q2/2026) [V-sek].
- **Betreiber:** nichts zu beantragen, was Ladedaten liefert; Inventar nur über OSM/OCM und Betreiber (Eldrive, Fines, EVPoint, Electrocharge).

### Dänemark
- **NAP:** **Dataudveksler** des Vejdirektoratet (`nap.vd.dk` → [du-portal-ui.dataudveksler.app.vd.dk](https://du-portal-ui.dataudveksler.app.vd.dk/)), Login erforderlich [V].
- **Recht:** Lov om infrastruktur for alternative drivmidler; **BEK nr. 1456/2025** ([retsinformation](https://www.retsinformation.dk/eli/lta/2025/1456/pdf)): ID-Codes über Selbstbedienung der Færdselsstyrelsen (§§ 4–5), API-Pflicht (§§ 6–7), Strafen (§ 11); die **Klimadatastyrelsen** sammelt und verteilt die Daten nach Art. 20 Abs. 2 [V]/[V-sek]. Færdselsstyrelsen (19.12.2025): ab 14.04.2026 ist **DATEX II das einzig zulässige Format**, OCPI optional ([Mitteilung](https://www.fstyr.dk/nyheder/2025/dec/afir-operatoerer-skal-sikre-data-tilgaengeliggoerelse-i-det-europaeiske-format-datex-ii-fra-den-14-april-2026), [AFIR-OCPI-Leitfaden](https://www.fstyr.dk/publikationer/afir-ocpi-vejledning)) [V].
- **Daten:** zwei Pfade: (a) der NAP führt **Metadaten je Betreiber-API**, markiert „restricted – access exclusively for AFIR compliance“ (z. B. Monta für Uno-X, Stella, Nortec, NRGi) [V-sek]; (b) die Klimadatastyrelsen baut eine nationale **OCPI-Sammellösung** „Ladepunktsdata i realtid“ auf; die Infoseiten dazu waren am 17.10.2026 nicht erreichbar (404) [V]. Ad-hoc-Preise und Lizenz der Ladedatensätze [U].
- **Betreiber:** Konto auf dem Dataudveksler-Portal beantragen und je Betreiber-Datensatz Zugang anfragen (Zweckbindung AFIR prüfen); parallel bei der Klimadatastyrelsen (`kds@kds.dk`) nach dem Entwicklerzugang zur OCPI-Sammlung fragen.

### Estland
- **NAP:** das staatliche Datenportal [andmed.eesti.ee](https://andmed.eesti.ee) („Eesti teabevärav“, RIA; ITS-Behörde Transpordiamet) als reiner **Katalog**: die Daten bleiben beim Betreiber, der NAP listet Beschreibung und API-Adresse ([Transpordiamet](https://www.transpordiamet.ee/en/road-maintenance-and-traffic-management/estonian-road-network/estonian-national-access-point-nap)) [V].
- **Recht:** Energiewirtschaftsgesetz EnKS, Kapitel 8⁵, § 32²² und § 32²³, in Kraft seit **13.07.2026** ([Riigi Teataja](https://www.riigiteataja.ee/akt/EnKS)): Betreiber liefern statische und dynamische Daten kostenlos über den NAP; **Transpordiamet ist IDRO** (Bescheid in 15 Arbeitstagen, Antrag binnen drei Monaten nach Inkrafttreten) [V]. Im IDRR steht Estland noch auf „soon“ [V].
- **Daten:** am 17.10.2026 **kein einziger Ladepunkt-Datensatz** im Portal (Suche „laadimis“: 0) [V]. Format und Lizenz hängen vom Betreiber ab (Alexela, Elektrum, Eleport, Enefit, Circle K, Ignitis, Neste, VOOL) [V-sek].
- **Betreiber:** nichts zu beantragen; NAP-Katalog periodisch auf neue Betreiber-APIs prüfen.

### Finnland
- **NAP:** Liikennedatakatalogi (Fintraffic); Datenausgabe über **Digitraffic** (`afir.digitraffic.fi`); Traficom beaufsichtigt und ist IDRO ([Fintraffic AFIR](https://www.fintraffic.fi/fi/digitaalisetpalvelut/afir), [Traficom](https://www.traficom.fi/sv/transportsystem/eus-forordning-om-infrastruktur-alternativa-drivmedel-afir)) [V]. Recht: Lag 475/2024, Lag 992/2025, Gebührenverordnung 2025/1337 [V].
- **Zentral:** Betreiber liefern OCPI an Fintraffic, Fintraffic wandelt nach DATEX II; Traficom akzeptiert das als Erfüllung der DATEX-Pflicht [V]. Endpunkte ([Doku](https://www.digitraffic.fi/en/road-traffic/afir/)): `/api/charging-network/v1/operators`, `/locations` (GeoJSON) und `/locations/datex2-3.6`, `/locations/statuses` (minütlicher Snapshot `/statuses/all`, auch als DATEX II), `/tariffs` (JSON); MQTT `wss://afir.digitraffic.fi:443/mqtt`, Topic `status-v1/<cc>/<party>/<location>/<evse>`, anonym; Cursor-Paging 500 [V]. Status in Echtzeit, Tarife nur teilweise geliefert [V].
- **Zugang/Lizenz:** ohne Key; **CC BY 4.0** mit Attribution „Source: Fintraffic / digitraffic.fi, license CC 4.0 BY“ ([Terms](https://www.digitraffic.fi/en/terms-of-service/)); 60 Requests/min je IP ohne `Digitraffic-User`-Header, gzip Pflicht, ETag nutzen [V]/[V-sek].
- **IDRO:** Traficom, Antrag über Suomi.fi, 80 €/Jahr, [öffentliche Liste](https://www.traficom.fi/fi/idro) (Virta FI*VIR, Helen FI*HLN, Tesla FI*TSL …) [V].
- **Stand:** 18 Betreiber (Virta, ABC, Allego, IONITY, K-lataus, Lidl, Neste, Recharge, St1, Tesla …), etwa die Hälfte der über 20.000 Ladepunkte des Landes [V].
- **Betreiber:** nichts zu beantragen; die Entwicklung setzt `Digitraffic-User: chargeglobe` und die CC-BY-Attribution.

### Frankreich
- **NAP:** [transport.data.gouv.fr](https://transport.data.gouv.fr) (Point d'Accès National, DGITM); Arrêté vom 24.03.2026 [V-sek]. Betreiber liefern über **Qualicharge** (DGEC), Anbindung für DC-Betreiber seit 2025 Pflicht ([qualicharge.beta.gouv.fr](https://www.qualicharge.beta.gouv.fr/)) [V]. Recht: Décret 2017-26, Décret 2021-1561 (Code de l'énergie R. 353-4-1 ff.), Arrêté 04.05.2021 [V-sek].
- **Zentral:** [„[BETA] Base Nationale des Points de Recharge (IRVE)“](https://transport.data.gouv.fr/datasets/beta-bases-nationales-des-points-de-recharge-pour-vehicules-electriques-en-france-irve): statisch konsolidiert und dedupliziert (Ressource 84013, nächtlich), **dynamisch konsolidiert** (Ressource 84098, nicht dedupliziert, nahe Echtzeit), über 1.500 Produzenten, **Licence Ouverte 2.0** [V]. Die alte data.gouv-Konsolidierung endet 31.12.2026 [V].
- **Format:** CSV nach [Schema irve-dynamique 2.3.1](https://schema.data.gouv.fr/etalab/schema-irve-dynamique/2.3.1/), Schlüssel `id_pdc_itinerance` (= EVSE-ID), Zustand und Belegung je Ladepunkt; **kein strukturiertes Preisfeld** (nur Freitext `tarification` im statischen Schema); **kein DATEX II** auf dem PAN [V]/[V-sek].
- **Stand:** etwa **114.000 Ladepunkte** im dynamischen Feed (09.10.2026); große Betreiberlücken (Qovoltis 100 %, Aldi 94 %, Izivia 91 % fehlen, Driveco 65 %, Carrefour 48 %), viele Status älter als 7 Tage, ~2 % Zeitstempel ohne Zeitzone ([Qualitätsanalyse](https://github.com/transportdatagouvfr/consolidation-irve-data-quality/issues/9)) [V-sek]. Keine Historie auf dem PAN.
- **IDRO:** AFIREV (seit 30.04.2024 in Avere-France), [Präfixliste](https://www.data.gouv.fr/datasets/liste-des-prefixes-didentifiants-dinfrastructures-de-recharge-ouvertes-au-public-et-de-contrats-de-services-donnant-acces-aux-infrastructures-de-recharge-ouvertes-a-litinerance) als Datensatz unter LO 2.0 [V].
- **Betreiber:** nichts zu beantragen; `https://transport.data.gouv.fr/resources/84013/download` und `…/84098/download` direkt pollen; Attribution nach LO 2.0.

### Griechenland
- **NAP für Ladedaten:** das Register **Μ.Υ.Φ.Α.Η.** des Ministeriums für Infrastruktur und Verkehr, [electrokinisi.yme.gov.gr](https://electrokinisi.yme.gov.gr/), das sich selbst als National Access Point bezeichnet ([API-Handbuch](https://electrokinisi.yme.gov.gr/public/HelpMyfah/pIndex/)) [V]; der ITS-NAP data.nap.gov.gr führt keinen Ladepunkt-Datensatz [V].
- **Recht:** Gesetz 4710/2020 (Art. 12 Marktregister), KYA 355033/2021 (statische und dynamische Daten inkl. Verfügbarkeit je Stecker und Ad-hoc-Preis an das Register), Gesetz 4936/2022 Art. 13 [V]. CPOs liefern per **OCPI 2.2** (Locations, Tariffs, Status) mit Token, Dynamik unter 10 min [V]. Eine Anpassung an DATEX II nach DVO 2025/655 wurde nicht gefunden [U].
- **Zentral, offen:** tägliche statische Datei `https://electrokinisi.yme.gov.gr/public/static_files/GR.IDRO.static.data.latest.json.zip` und alle 10 min `…/GR.IDRO.dynamic.data.latest.json.zip` ([Public Data](https://electrokinisi.yme.gov.gr/public/HelpMyfah/PublicData/)) [V]. Geprüft am 17.10.2026: OCPI-nahes JSON (UTF-8 mit BOM), 4.319 Locations, **10.085 EVSEs**, 37 Party-IDs (DEI Blue, ERMIS/incharge, Joltie, Watt&Volt, Blink, EV Loader …), EVSE-IDs `GR*PPC*E…`; Status AVAILABLE 8.371, INOPERATIVE 546, OUTOFORDER 402, CHARGING 385, UNKNOWN 359, BLOCKED 22; **keine Tarife** in den öffentlichen Dateien [V].
- **Lizenz:** auf data.gov.gr **CC BY 4.0** ([Datensatz](https://data.gov.gr/dataset/dimosios-prosvasima-simeia-fortisis)) [V].
- **IDRO:** das Ministerium (`dteo@yme.gov.gr`), [Portal](https://electrokinisi.yme.gov.gr/public/IDRO/) mit öffentlicher Liste aller Marktteilnehmer [V].
- **Betreiber:** nichts zu beantragen; Tarife nur auf Antrag beim Ministerium [U].

### Irland
- **NAP:** [data.gov.ie](https://data.gov.ie) nur als Discovery-Portal; die Aggregation macht die **Data Exchange Platform (DXP)** von Transport Infrastructure Ireland (TII, Alt Fuels Data Office): CPOs liefern **OCPI 2.2.1**, TII validiert, danach wird die DXP im NAP „discoverable“ ([TII](https://www.tii.ie/en/roads-tolling/alt-fuel-projects-unit/alt-fuels-data-office/register-dxp/)) [V].
- **Recht:** AFIR unmittelbar; IDRO per S.I. No. 52 of 2026 [V]. Die Datenstrategie von ZEVI/DoT (02/2026) lässt offen, ob die DXP per geschlossener API, offener API oder offener Veröffentlichung ausgibt („yet to be specified“) [V].
- **Daten:** zentral geplant (statisch ≤ 24 h, dynamisch ≤ 1 min); ein Abruf durch Dritte ist nirgends dokumentiert [U]; auf data.gov.ie kein AFIR-Datensatz, nur Gemeinde-Datensätze (CC BY 4.0) [V]. TII-Daten stehen generell unter CC BY 4.0 [V], für die DXP [U].
- **IDRO:** TII, IDs `IE-ESB`, `IE-IOY`, `IE-TSL`; [öffentliches Register](https://www.tii.ie/media/dbdj2xvg/identiﬁcation-registration-organisation-public-register-16092026.pdf) (19 Einträge, 16.09.2026) [V].
- **Betreiber:** E-Mail an `AFIRdata@tii.ie` mit der Bitte um Zugang als „data user“ (API oder Download, Lizenz).

### Italien
- **NAP (ITS):** CCISS „Viaggiare Informati e Sicuri“ des MIT, [cciss.it](https://www.cciss.it/) [V]: nennt nur die ITS-Delegiertenverordnungen, **kein Ladepunkt-Datensatz, kein DATEX-II-Katalog, keine Lizenz** („All rights reserved“) [V]. Eine Domain nap.mit.gov.it (so bisher in [08](08-datenquellen-und-recht.md)) ist nicht auffindbar [U].
- **Fachliches Register:** **PUN** („Piattaforma Unica Nazionale“, MASE mit GSE und RSE, [piattaformaunicanazionale.it](https://www.piattaformaunicanazionale.it/)) [V]. Rechtsgrundlage DM MASE 16.03.2023 auf Basis Art. 45 Abs. 3 D.Lgs. 199/2021; CPOs mussten bis 26.07.2024 registrieren; Anlage 1 verlangt Basispreis je kWh und Echtzeitstatus [V-sek]. Ein Rechtsakt, der PUN oder CCISS zum AFIR-NAP macht, wurde nicht gefunden [U].
- **Daten:** zentral in der PUN (CPOs liefern per OCPI oder manuell/CSV; Status als Tagesmomentaufnahme, Echtzeit nur bei „Aggiornamento in real time“, [GSE-Handbuch](https://www.gse.it/documenti_site/Documenti%20GSE/Servizi%20per%20te/PUN/Moduli%20e%20modelli/Manuale%20Utente%20PUN.pdf)) [V]; **Preise werden öffentlich nicht angezeigt** [V-sek]. Nur Webkarte (ArcGIS-App), **kein Open-Data-Download, kein DATEX II**; die frühere Extraktion über ArcGIS-REST funktioniert seit einer Umstellung nicht mehr ([ondata](https://github.com/ondata/rete_ricarica_veicoli_elettrici)) [V]. Lizenz keine [V]. 78.939 Ladepunkte (16.10.2026) [V-sek].
- **IDRO:** keine (IDRR „soon“); die PUN vergibt intern einen „codice univoco CPO“ [V].
- **Betreiber:** Anfrage an MASE/GSE (PUN-Support) nach API oder Open Data und an das MIT (CCISS) nach dem AFIR-Feed; bis zur Antwort bleibt es beim Plan aus [09](09-roadmap.md) (Südtirol über OCM/OSM und Betreiber-Karten).

### Kroatien
- **NAP:** „Nacionalna pristupna točka“, Hrvatske ceste d.o.o., Portal [promet-info.hr](https://www.promet-info.hr/) (DATEX II 2.3 B2B mit Login) ([Hrvatske ceste](https://hrvatske-ceste.hr/hr/stranice/promet-i-sigurnost/dokumenti/76-nacionalna-pristupna-tocka)) [V]; **kein Ladepunkt-Datensatz** sichtbar [V].
- **Recht:** Zakon o uspostavi infrastrukture za alternativna goriva (NN 120/16, noch zur Richtlinie 2014/94) [V]; Meldeweg nach Art. 20 nicht auffindbar [U]. Die Registerseite des Ministeriums MMPI („Registar punionica“) ist eine leere Karte ohne Datenendpunkt [V].
- **IDRO:** CRO IDRO im MMPI, kostenlos, [öffentliches Register](https://pametnamobilnost.hr/en/cro-id-hr-guidelines/18) mit über 50 Einträgen (HR-ARC Acciona, HR-BIP Best in Parking, HR-HRV Connect Charge …), Format `HR*IBC*E…` [V].
- **Betreiber:** Anfrage an Hrvatske ceste und MMPI; bis dahin OSM/OCM.

### Lettland
- **NAP:** [transportdata.gov.lv](https://www.transportdata.gov.lv) („Nacionālais piekļuves punkts“), Latvijas Valsts ceļi (LVC) [V]. Recht: MK-Verordnung Nr. 188 vom 25.03.2025 i. d. F. Nr. 534 vom 08.09.2026 ([likumi.lv](https://likumi.lv/ta/id/359544-transporta-nozares-informacijas-nacionala-valsts-piekluves-punkta-darbibas-un-datu-iesniegsanas-noteikumi)): Anhang 1 nennt Standorte, Verfügbarkeit und Ad-hoc-Preis als DATEX II; Fristen TEN-T 31.12.2025, übriges Netz 31.12.2027; DATEX II ≥ 3.4 XML, REST, MQTT, API-Keys; Standardlizenz **CC0 1.0** [V].
- **Daten:** zentraler Datensatz [„Elektroauto uzlādes infrastruktūra“](https://www.transportdata.gov.lv/en/card/978835ee-f55b-481c-9791-ba0395d3619a) (DATEX II v3, Datei `NPP_DK_65.xml`, Update 11.09.2026), **nur statisch**; ein dynamischer Datensatz war nicht auffindbar [V].
- **IDRO:** LVC, IDs `LV-XXX`, Antrag mit eIDAS-Signatur an `idro@lvceli.lv`, [Register](https://www.transportdata.gov.lv/en/idro) mit 11 Einträgen [V].
- **Betreiber:** Konto anlegen, API-Key beziehen, Datensatz 65 abonnieren; nach dynamischen Daten bei `npp@lvceli.lv` fragen.

### Litauen
- **NAP:** [Nacionalinis prieigos punktas](https://maps.eismoinfo.lt/portal/apps/sites/#/npp) (ArcGIS-Hub) der AB **Via Lietuva**; Ladedaten auf [ev.vialietuva.lt](https://ev.vialietuva.lt) [V].
- **Recht:** Alternatyviųjų degalų įstatymas XIV-196, Art. 25 (Fassung XV-113 vom 14.01.2025, [e-seimas](https://e-seimas.lrs.lt/portal/legalAct/lt/TAD/0409c522915c11eb998483d0ae31615c/asr)): Informationssystem der öffentlichen Ladepunkte; eindeutige IDs vom Systemverwalter; Betreiber liefern nach Art. 20 Abs. 2 kostenlos per **OCPI**; Veröffentlichung offen; vierteljährliche Prüfung, Sperre bei Nichtlieferung. EIS-Statut (Via Lietuva, 30.01.2026): Pflichtfelder inkl. Ad-hoc-Echtzeitpreis; Daten zur Weiterverwendung publiziert [V].
- **Zentral, offen, geprüft am 17.10.2026** ([Open Data](https://ev.vialietuva.lt/atviri-duomenys-1)) [V]: DATEX II v3 `https://ev.vialietuva.lt/publicdata/EnergyInfrastructureTablePublication` (22,7 MB, 3.680 Sites, 18.338 refillPoints; Preis nur Freitext) und `…StatusPublication` (3,3 MB, 15.840 refillPointStatus, Werte **OCPI-artig** AVAILABLE/CHARGING/OUTOFORDER/INOPERATIVE/REMOVED/UNKNOWN statt DATEX-Enumeration); **OCPI 2.3.0** (auch 2.2.1/2.1.1): `/ocpi/2.3.0/locations` (party_ids IGN, IBG, ION, IKR) und `/ocpi/2.3.0/tariffs` (strukturierte Tarife). 13 Betreiber, 5.820 Ladepunkte an 3.291 Stationen.
- **Lizenz:** CC BY 4.0 / ODC-BY 1.0, Weiterverbreitung erlaubt [V]. Rate-Limits nicht dokumentiert [U].
- **Betreiber:** nichts zu beantragen; der erste Einsatzfall für den generischen OCPI-Adapter neben der Schweiz.

### Luxemburg
- **NAP:** [data.public.lu](https://data.public.lu), Administration des Ponts et Chaussées laut Kommissionsliste [V]. Ein großherzogliches Reglement zur AFIR war am 15.04.2024 im Regierungsrat, die Verabschiedung ist nicht belegt [V-sek]; zuständige AFIR-Behörde [U].
- **Daten:** **kein AFIR-/DATEX-Datensatz** [U]. Vorhanden: Chargy-Datensatz [„Bornes de chargement publiques“](https://data.public.lu/api/1/datasets/bornes-de-chargement-publiques-pour-voitures-electriques/) (CC0), aber nur eine KML-Ressource (Stand 2018) mit eingebettetem Key von my.chargy.lu („bornes avec prises libres“); Geoportail-WMS „Chargy stations“ mit Echtzeitbelegung unter „other restrictions“ [V]. Dritt-CPOs (Shell, Tesla) fehlen [U]. EVSE-IDs über die Benelux-IDRO; Register als CC0-Datensatz des Ministère de la Mobilité (Stand 2021) [V].
- **Betreiber:** Anfrage an `data@public.lu` bzw. Ponts et Chaussées, wo die AFIR-Daten der Betreiber liegen; bis dahin OSM/OCM.

### Malta
- **NAP:** Transport Malta, [eGIS „STREETS“](https://geoservices.transport.gov.mt/egis) (Webkarte mit Layer „Electrical Charging Points“, Gast-Login, „All Rights Reserved“) [V]; ein echter NAP wird erst mit der ITMS-Ausschreibung aufgebaut, 05/2026 wurde die Serverbeschaffung angekündigt [V-sek].
- **Recht:** S.L. 545.38 (LN 226/2022): Betreiberzulassung durch REWS, OCPP/OCPI-Pflicht, **keine Datenlieferpflicht an einen NAP** [V]. Entwurf des nationalen AFIR-Politikrahmens 09/2025 [V-sek].
- **Daten:** de facto ein staatliches Netz „Charge My Ride“ (~372 Punkte, Preise auf der Website, Verfügbarkeit nur in der App, **kein API oder Download**) [V]; REWS-Liste mit 16 zugelassenen Betreibern (23.06.2026) [V]. Kein DATEX II, keine Lizenz, keine IDRO [V].
- **Betreiber:** Anfrage an Transport Malta und REWS; sonst OCM/OSM.

### Niederlande
- **NAP für Ladedaten:** **DOT-NL** („Dataplatform Openbaar Toegankelijke laadpunten Nederland“) des NDW im Auftrag des Ministeriums IenW, eingebettet in den ITS-NAP ntm.ndw.nu ([NDW](https://www.ndw.nu/dataportalen/dot-nl)) [V]. Ein Ausführungsgesetz („Wet uitvoering EU-regels infrastructuur alternatieve brandstoffen“) war bis 12.10.2025 in Konsultation [V]; Aufsichtsbehörde für Art. 20 [U].
- **Zentral:** CPOs liefern **OCPI 2.2.1** an NDW; etwa 40 Parteien (Fastned, Allego, IONITY, Tesla), „nahezu vollständige Abdeckung“ angestrebt ([FAQ](https://docs.ndw.nu/faq/DOT-NL/)) [V]. Enthalten: Standorte, Echtzeit-Verfügbarkeit und **Tarife** (OCPI Tariffs) [V].
- **Zugang:** offene Downloads ohne Registrierung unter [opendata.ndw.nu](https://opendata.ndw.nu/): `charging_point_locations_ocpi.json.gz` (18 MB), `charging_point_tariffs_ocpi.json.gz` (3,9 MB), `charging_point_locations.geojson.gz` [V]; GeoJSON-BBox-API `dotnl.ndw.nu/api/rest/geojson/dynamic-road-status/charge-point-data/v1/features?bbox=…` (10 Requests/s, max. 1°², 1.000 Features), „noch in Entwicklung“ [V]. NDW konvertiert intern nach DATEX II, bietet aber keinen DATEX-Endpunkt für Abnehmer; OCPI-Push für Abnehmer ab etwa Ende 2026 [V].
- **Lizenz:** auf dem Datensatz **nicht angegeben** [V]; Weiterverbreitung vor Produktivbetrieb mit `mail@servicedeskndw.nu` klären [U].
- **IDRO:** Benelux-IDRO [V]. **Betreiber:** nichts zu beantragen, nur die Lizenzfrage per E-Mail klären.

### Polen
- **Register und NAP:** Das amtliche Register **EIPA** („Ewidencja Infrastruktury Paliw Alternatywnych“, [eipa.udt.gov.pl](https://eipa.udt.gov.pl)) des UDT sammelt Inventar, Echtzeit-Verfügbarkeit und Ad-hoc-Preise aller öffentlichen Ladepunkte [V]; der NAP **KPD** der GDDKiA ([kpd.gddkia.gov.pl](https://kpd.gddkia.gov.pl), `kpd@gddkia.gov.pl`) führt seit 20.01.2025 den daraus aggregierten DATEX-II-Feed `alternative-fuels` [V]. Recht: Ustawa o elektromobilności (Dz.U. 2026 poz. 1243) Art. 42: Eintragung vor Inbetriebnahme, Verfügbarkeit „sofort nach Änderung“, Preis binnen einer Stunde; Daten für jeden Interessierten; UDT vergibt die Codes [V].
- **Zugang EIPA:** JSON-Dateien `operator/pool/station/point/dynamic.json`, Preise in PLN je kWh/min; Registrierung nur mit E-Mail ([reader/register](https://eipa.udt.gov.pl/reader/register)), stündliche Download-Limits (Wert steht in der Bestätigungsmail), Konten ohne Abruf 30 Tage werden gelöscht; kostenlos, kommerziell nutzbar, Anzeige und Weiterverarbeitung erlaubt, keine benannte Lizenz, keine Gewähr für Aktualität ([FAQ](https://eipa.udt.gov.pl/faq)) [V].
- **Zugang KPD:** DATEX II v3.5, SOAP, Table- und StatusPublication, Pull oder Push; Registrierung per Formular an `kpd@gddkia.gov.pl`, Test- und Produktionszertifikat ([Anleitung](https://kpd.gddkia.gov.pl/wp-content/themes/kpd_test/static-pages/pliki/instrukcje/en/KPD_Instrukcja_dla_DDiOD_EN_10.00_260420.pdf)); Lizenz und Rate-Limits nicht genannt [V]. Für chargeglobe ist EIPA direkt die einfachere Quelle.
- **IDRO:** UDT/EIPA, Codes `PL-XXX`, Pool `PL-XXX-P…`, Punkt `PL-XXX-E…`; Betreiberliste als `operator.json` nach Registrierung [V].
- **Stand:** 17.10.2026 in EIPA 6.819 Standorte, 8.510 Stationen, 16.922 Ladepunkte (inkl. nicht betriebsbereite) [V]; nur UDT-geprüfte Stationen erscheinen.
- **Betreiber:** E-Mail-Registrierung bei EIPA, Bestätigungslink, Limits aus der Mail an die Entwicklung weitergeben.

### Portugal
- **NAP:** „Ponto de Acesso Nacional“ des IMT ([nap-portugal.imt-ip.pt](https://nap-portugal.imt-ip.pt/nap/home), Katalog nur mit JavaScript) [V]. Die Ladedaten liefert die **EADME = MOBI.E**, die nationale Plattform, seit 09/2026 auch mit Betreibern außerhalb des MOBI.E-Netzes [V-sek].
- **Recht:** Decreto-Lei 93/2025 (RJME, 14.08.2025): Art. 13 Pflicht der Betreiber, statische und dynamische Daten an die EADME zu liefern, Art. 18 Weiterleitung an den PAN; Portaria 31/2026/1; **Regra Técnica 1/EADME/2026** (18.05.2026): statisch ≤ 24 h, dynamisch ≤ 1 min, Lieferung als unauthentifizierte DATEX-II-Links oder per OCPI 2.2/2.2.1 ([PDF](https://www.mobie.pt/documents/699315/699782/Regra+T%C3%A9cnica+1_EADME_18052026.pdf/28f8cc57-7748-ef63-099b-840de7103d5f)) [V]; Übergangsfrist per DL 155/2026 bis 31.12.2027 [V-sek].
- **Zentral, offen, geprüft am 17.10.2026** [V]: statisch `https://pgm.mobie.pt/integration/nap/evChargingInfra` (EnergyInfrastructureTablePublication, 177 MB, **8.711 Sites, 19.265 refillPoints**, EVSE-ID als `externalIdentifier`, 93 CPO-Party-IDs), dynamisch `https://pgm.mobie.pt/integration/nap/evActualStatus` (EnergyInfrastructureStatusPublication, 38 MB, 5-Minuten-Takt; Status available 13.926, charging 2.857, unknown 1.273, outOfOrder 972, inoperative 186, blocked 45); Preis nur als Mindest-/Aktivierungsgebühr, **kein Preis je kWh**; ETag/304 wird unterstützt. Kein Operator-Name im Feed (nur Party-ID, Auflösung über die IDRO-Liste).
- **Lizenz:** nirgends angegeben; Weiterverbreitung ungeklärt [U]. **IDRO:** MOBI.E (`idro@mobie.pt`), [öffentliche Party-ID-Liste](https://www.mobie.pt/en/redemobie/idacs) [V].
- **Stand:** live, landesweit; ERSE zählte 15.647 Ladepunkte (02/2026), der Feed liegt darüber [V].
- **Betreiber:** nichts zu beantragen; die Lizenzfrage schriftlich an IMT (PAN) und `mic@mobie.pt` stellen, bevor die Daten über die chargeglobe-API weitergegeben werden.

### Rumänien
- **NAP:** [pna.cestrin.ro](https://pna.cestrin.ro/) („Punct Național de Acces“), CESTRIN im Umfeld der CNAIR, `office@cestrin.ro` [V]. **Recht:** OUG nr. 4/2026 (18.02.2026), bestätigt durch Legea nr. 89/2026 (09.06.2026): CNAIR soll den PNA als AFIR-Plattform entwickeln, Betreiber liefern statische und dynamische Daten kostenlos, Bußgelder 5.000–40.000 Lei [V-sek]; der Gesetzestext war nicht abrufbar, Fristen und Format [U].
- **Daten:** der Katalog (CSW `pna.cestrin.ro/geoportal/csw`) enthält am 17.10.2026 37 Datensätze, ausschließlich Straßendaten der CESTRIN, **keinen Ladepunkt-Datensatz** [V]. Lizenz: keine Open-Data-Lizenz, Nutzung „nur mit Erlaubnis“ ([Terms](https://pna.cestrin.ro/ro/termeni-si-conditii/)) [V]. Keine IDRO (IDRR „soon“) [V]. Über 10.000 öffentliche Ladepunkte (Q3/2026) [V-sek].
- **Betreiber:** nichts, was Ladedaten liefert; bei `office@cestrin.ro` nach dem Start des AFIR-Feeds fragen.

### Schweden
- **NAP:** [trafficdata.se](https://trafficdata.se) (Trafikverket), nur **Metadatenkatalog** [V]/[V-sek]. **Recht:** Förordning (2024:460) om infrastruktur för alternativa drivmedel ([lagen.nu](https://lagen.nu/2024:460/konsolidering/2024:961)): § 9 Transportstyrelsen beaufsichtigt Art. 20 Abs. 2/3, § 10 Trafikverket betreibt den NAP, § 12 **Energimyndigheten ist IDRO**; keine Sanktionsnorm [V].
- **Daten:** einziger Ladedatensatz im NAP ist [„Charging points for electric vehicles – NOBIL“](https://trafficdata.se/dataset/charging-points-for-electric-vehicles-nobil) [V]. De facto zentral ist **NOBIL** ([info.nobil.no](https://info.nobil.no/)), die nordische Ladepunkt-Datenbank (seit 10/2025 nur noch Norwegen und Schweden), mit Echtzeitstatus frei/belegt/außer Betrieb [V]; Ad-hoc-Preise [U]. Betreiber-DATEX-Feeds im NAP nicht gefunden [U].
- **Zugang/Lizenz:** NOBIL-API (JSON), API-Key über `info.nobil.no/api`, **CC BY 4.0**, kostenlos ([Energimyndigheten](https://www.energimyndigheten.se/klimat/transporter/laddinfrastruktur/registrera-din-laddstation/utvecklare/)) [V]. 9.660 Stationen, 62.753 Anschlüsse (07/2026) [V].
- **Betreiber:** API-Key bei NOBIL beantragen (Formular, Lizenz CC BY 4.0 akzeptieren) und im Admin-UI hinterlegen.

### Slowakei
- **NAP:** derzeit **keiner in Betrieb**. Der NSDI (odoprave.info) wurde im Q1/2024 abgeschaltet, die Domain zeigt heute eine fremde Seite [V]; die Kommissionsliste nennt nur Ersatzquellen. Nachfolger **eNRI DOP** (Verkehrsministerium): Laufzeit 04/2025–03/2028, Ausschreibung 06/2026, Ladepunkte kommen in den Projektunterlagen nicht vor ([Projektzweck](https://eurofondy.gov.sk/wp-content/uploads/2024/10/Zamer_NP_Elektronicky-narodny-register_eNRI-DOP_MD-SR.pdf)) [V].
- **Recht/Daten:** kein Gesetz zu Art. 20, kein Register gefunden [U]; kein Landesfeed, keine Echtzeit, keine Preise [U].
- **IDRO:** Wirtschaftsministerium ([economy.gov.sk](https://www.economy.gov.sk/priemysel/idro-new)), E-Service seit 06.05.2026, kostenlos, `SK*xxx` (CPO) / `SK-xxx` (MSP); [öffentliche Liste](https://www.economy.gov.sk/priemysel/idro-new/zoznam-registracii) mit 64 Einträgen (GreenWay SK*GWP, ZSE SK*Z37, OMV SK*O17, Slovnaft SK*S0I, Shell SK*TNM) [V]. 3.707 öffentliche Ladepunkte (SEVA, 30.06.2026) [V].
- **Betreiber:** nichts zu beantragen; die IDRO-Liste dient als Betreiberverzeichnis, Inventar über OSM/OCM und Betreiber-APIs.

### Slowenien
- **NAP:** [nap.si](https://www.nap.si) („Nacionalna točka dostopa“), NCUP im Infrastruktur- und Energieministerium (MZIE), `mzi.ncup@gov.si` [V]. **Recht:** ZIAG (Uradni list RS 62/23, [PIS](https://pisrs.si/pregledPredpisa?id=ZAKO8771)): Art. 31 öffentliches Register der ID-Codes, Art. 32 Registrierungspflicht für CPO/MSP, Art. 33 CPOs liefern statische und dynamische Daten (Betriebszustand, Verfügbarkeit, Ad-hoc-Preis) kostenlos in Echtzeit an den NAP, Daten „für alle Beteiligten zur Nutzung und Weiterverwendung“ [V].
- **Zentral:** drei landesweite Datensätze „Prometej IDACS“: [Energy Infrastructure Table](https://www.nap.si/en/datasets_details?id=46963663-38dd-eb04-43a9-cca9bdc0e4ba) (statisch, inkl. Preis je kWh in `fac:rates`), [Energy Infrastructure Status](https://www.nap.si/en/datasets_details?id=acc8a643-9dac-ecad-58da-0ce20f88f4bd) (available/charging/inoperative/unknown) und TPEG EMI; **DATEX II v3.6**, HTTPS-Pull, „bis zu 1 min“ [V]. Preise nur im Table-Feed, nur Preis je kWh (Sample 12/2025: 1.327 von 3.042 Ladepunkten) [V].
- **Zugang:** `https://b2b.nap.si/data/b2b.prometej.energyInfrastructureTablePublication` bzw. `…StatusPublication`; Konto im NAP-Benutzerzentrum, je Datensatz „zaprosi“, manuelle Freigabe, dann OAuth2-Token (`POST https://b2b.nap.si/uc/user/token`, 24 h gültig) ([B2B-Doku](https://ncup.si/resources/doc/nap_B2B_sl.pdf)); kostenlos; Rate-Limits [U]. Lizenz **CC BY-SA 4.0** [V] (Share-Alike wie bei OSM beachten, [08 §8](08-datenquellen-und-recht.md)).
- **IDRO:** Registrierungsbüro im Ministerium (Anwendung PROMETEJ), `SI*XXX*E…`; öffentliche XLSX [CPO/MSP-Liste](https://www.ncup.si/dc/prometej.register-cpo-msp) (34) und [EVSE-Register](https://www.ncup.si/dc/prometej.register-evse-id) [V].
- **Stand:** 4.673 Ladepunkte, 23 CPOs; aber nur 715 Ladepunkte haben in den letzten 7 Tagen Daten geliefert, 697 seit über 90 Tagen nicht [V].
- **Betreiber:** auf nap.si als Geschäftsnutzer registrieren, die drei Prometej-Datensätze anfordern, Freigabe abwarten, Zugangsdaten (Benutzer/Passwort für den Token) im Admin-UI hinterlegen.

### Spanien
- **NAP:** „Punto de Acceso Nacional“ der DGT, CKAN-Portal [nap.dgt.es](https://nap.dgt.es/) [V].
- **Recht:** Ley 7/2021 Art. 15 (Veröffentlichung über den NAP); Orden TED/445/2023 (Meldepflicht an MITECO, dynamische Daten nur ab 43 kW verpflichtend) [V]; Resolución DGPEM vom 02.04.2025 ([BOE-A-2025-7025](https://www.boe.es/diario_boe/txt.php?id=BOE-A-2025-7025)): dynamische Daten (Betrieb, frei/belegt, Preis je kWh inkl. Ad-hoc) per **OCPI 2.2.1/2.2/2.1.1** an das System SGV von MITECO, betrieben von Red Eléctrica, Push bei Änderung; Veröffentlichung über den NAP ausdrücklich erlaubt [V]. Die CNMC nennt eine Übergangsfrist bis 01.10.2026 für neue dynamische Meldepflichten [V-sek].
- **Zentral, offen:** Datensatz [„Puntos de recarga eléctrica para vehículos“](https://nap.dgt.es/es/dataset/puntos-de-recarga-electrica-para-vehiculos) (DGT/MITERD): DATEX II v3 `EnergyInfrastructureTablePublication` unter `https://nap.dgt.es/datex2/v3/miterd/EnergyInfrastructureTablePublication/electrolineras.xml`, Pull ohne Key, 24 h [V]. Geprüft am 17.10.2026: 83 MB, **12.060 Sites, 35.608 refillPoints**, EVSE-IDs `ES*915*E…` (100 %), Öffnungszeiten, Steckertyp, Leistung; **kein Status, keine Tarife**, kein Betreibername (leeres `fac:operator`) [V]. Ein dynamischer Datensatz existiert im NAP nicht; die Dynamik liegt im SGV und erscheint nur in den MITECO-Karten [V]/[V-sek].
- **Lizenz:** **CC BY**, „Licence and Free of charge“ [V]. **IDRO:** MITECO über [RIPREE](https://energia.serviciosmin.gob.es/RIPREE) (`bzn-censorecarga@miteco.es`) mit öffentlichem Export der Betreiber [V]; Inhalt nicht geprüft [U].
- **Betreiber:** nichts zu beantragen für das Inventar; für Status und Preise Anfrage an `nap@dgt.es` bzw. MITECO, ob und wann das SGV über den NAP veröffentlicht.

### Tschechien
- **NAP:** [registr.dopravniinfo.cz](https://registr.dopravniinfo.cz/) (NDIC der ŘSD), Abos über mobilitydata.rsd.cz mit unterschriebenem Antrag [V]. **Keine Ladedaten im NAP** (32 Quellen, alle Verkehr) [V]; die 2022 geplante Ausschreibung eines Ladepunkt-Zugangspunkts wurde aufgehoben [V].
- **Recht:** Zákon č. 311/2006 Sb. erwähnt weder AFIR Art. 20 noch dynamische Daten; keine Novelle anhängig [V]. Es gibt die statische **Evidence dobíjecích stanic** des Wirtschaftsministeriums (MPO): § 6 Abs. 5/6 Meldung vor Inbetriebnahme; veröffentlicht als [XLSX](https://mpo.gov.cz/cz/energetika/statistika/statistika-a-evidence-cerpacich-a-dobijecich-stanic/seznam-verejne-pristupnych-dobijecich-stanic--280706/) (Stand 31.08.2026: 3.738 Stationen, 6.527 Ladepunkte), ohne Status und Preise; Lizenz unklar (Seitenfooter CC BY-NC-ND 3.0 CZ) [V]. Keine IDRO (IDRR „soon“) [V].
- **Betreiber:** MPO-XLSX anonym laden (Lizenzfrage an `dobijecistanice@mpo.gov.cz`); sonst nichts.

### Ungarn
- **NAP:** [napportal.kozut.hu](https://napportal.kozut.hu/) („Nemzeti Adathozzáférési Pont“), Magyar Közút (ÚTINFORM) [V]. **Recht:** Gesetz 2024:LXXXIX (01.01.2025) ergänzt das Straßenverkehrsgesetz Kkt. 45/C § (8)–(11): die Energiebehörde **MEKH** führt das CPO/MSP-Register und vergibt Kennungen, Betreiber geben kostenlosen Zugang zu statischen und dynamischen Daten [V]; Regierungsverordnung 243/2019 § 7 (1) m) (ab 28.08.2025) verlangt Format und Frequenz nach DVO 2025/655 [V]. MEKH-Register seit 23.07.2025, monatlich ([mekh.hu](https://mekh.hu/villamosenergia-ipari-engedelyesek-listaja)) [V].
- **Daten:** der NAP ist ein Metadatenkatalog ohne Aggregation [V]. Am 17.10.2026 in „Töltőállomás információk“: E.ON Drive Infrastructure Hungary (statisch und **dynamisch**, DATEX II v3.3 XML, Push), MVM Mobiliti und Eco-Movement (nur statisch), Magyar Közút (v2.3) [V]. Ad-hoc-Preise nicht erkennbar [U]. Lizenz **CC BY 4.0**, kommerzielle Nutzung und Weitergabe erlaubt (ÁSZF Pkt. 32) [V]. 4.227 öffentliche Ladegeräte (Ende 2025) [V-sek]; der Großteil der CPOs fehlt im NAP.
- **Betreiber:** auf napportal.kozut.hu als Datennutzer registrieren, Aktivierungs-Mail, Freigabe durch Magyar Közút, dann die Datensätze abonnieren (E.ON Dynamic, Profil-ID 228).

### Zypern
- **NAP:** „Cyprus National Access Point (CyNAP)“, [traffic4cyprus.org.cy](https://www.traffic4cyprus.org.cy/) (KIOS/UCY im Auftrag des Verkehrsministeriums, 42 Datensätze) [V]. Die Datensatzbeschreibung zitiert AFIR; ein nationales Umsetzungsgesetz wurde nicht gefunden [U].
- **Zentral, offen, lückenhaft:** Datensatz [„Electric Vehicle Chargers“](https://traffic4cyprus.org.cy/en/dataset/electricvehiclecharges) des Department of Electrical and Mechanical Services: XML-Pull ohne Key `https://fixcyprus.cy/gnosis/open/api/nap/datasets/electric_vehicle_chargers/`, nächtlich, DATEX-II-v3-Namespaces, aber **nicht schemakonform** (eigene Elemente `chargingPoint`, `chargingPointStatus`); geprüft am 17.10.2026: **171 Ladepunkte**, Status operational 139 / unavailable 32 (Betriebszustand, keine Belegung), Betreiber Petrolina 112, EV Power 50, e-Point 4, Lidl 4; **keine EVSE-IDs, keine Preise**; der größte Betreiber EAC „e-charge“ fehlt [V].
- **Lizenz:** **CC BY 4.0** [V]. **IDRO:** keine (IDRR „soon“) [V].
- **Betreiber:** nichts zu beantragen; nur als Ergänzung zu OCM brauchbar.

## 7. Empfehlung für die Reihenfolge

Nach Nutzen für chargeglobe (Status + Preis, Lizenz, Aufwand) und Nähe zum heutigen Scope ([01](01-vision-und-scope.md): AT → DACH → Italien):

1. **Deutschland** (Phase 3): sofort mit BNetzA-Inventar und MobiData BW starten (beides offen); parallel Mobilithek-Organisation und Zertifikat beantragen (Abschnitt 3.2), um die Feeds zu bekommen, die MobiData BW nicht spiegelt. Hinweis für die Planung: der Mobilithek-Pull liefert Deltas, nicht Snapshots; der Adapter braucht einen eigenen Zustand je Subscription.
2. **Schweiz** (Phase 3, [08 §5](08-datenquellen-und-recht.md)): OCPI ab 11/2026, offen.
3. **Italien** (Phase 4): die PUN hat keinen maschinenlesbaren Zugang; der Betreiber muss bei MASE/GSE und MIT anfragen, sonst bleibt Südtirol beim Plan über OCM/OSM und Betreiber-Karten.
4. **Ohne Vertrag sofort anbindbar**, falls der Scope wächst: Finnland, Litauen (beide OCPI/DATEX, CC BY), Portugal (DATEX II mit Status im 5-Minuten-Takt, Lizenz vorher klären), Griechenland (JSON mit Status, CC BY 4.0), Niederlande (OCPI, Lizenz klären), Frankreich (CSV, ohne Preise), Spanien (nur Inventar), Polen (EIPA, E-Mail-Registrierung), Schweden (NOBIL, Key).
5. **Mit Registrierung und Freigabe:** Slowenien (OAuth2, CC BY-SA), Belgien (Tokens je Aggregator), Ungarn (ein dynamischer Betreiber), Lettland (nur statisch), Dänemark (restriktiv).
6. **Noch keine Quelle:** Tschechien, Slowakei, Rumänien, Bulgarien, Kroatien, Malta, Estland, Irland, Luxemburg; Zypern nur als Ergänzung. Hier nur OSM/OCM und die IDRO-Listen als Betreiberverzeichnis; halbjährlich neu prüfen.

## 8. Offene Punkte

- Die Recherche beruht auf dem Stand vom 17.10.2026; alle Suchkontingente der Teilrecherchen waren am Ende erschöpft, die mit [U] markierten Punkte sind nicht geschlossen.
- Deutschland: Zugriffslimit und Prüfdauer der Mobilithek, Nutzungsbedingungen der MobiData-BW-API, Stand des IVSG.
- Dänemark: Zugang und Lizenz der Ladedatensätze; Schweden: Ad-hoc-Preise in NOBIL; Niederlande: Datenlizenz des DOT-NL; Portugal: Lizenz des MOBI.E-Feeds; Luxemburg: zuständige AFIR-Behörde; Rumänien: Wortlaut der OUG 4/2026; Italien, Kroatien, Malta, Zypern: Rechtsakt, der den AFIR-NAP benennt.
- Strukturierte Ad-hoc-Preise liefern bisher nur Österreich, Polen, Slowenien (je kWh), Litauen und die Niederlande (OCPI-Tarife) sowie die Betreiber-Feeds in Deutschland und Belgien; in allen anderen Ländern fehlen sie trotz DVO 2025/655.
- CEAP: sobald die Kommission den gemeinsamen europäischen Zugangspunkt freischaltet (geplant bis 31.12.2026), ist zu prüfen, ob er Länderfeeds ersetzt.
