# Endpunkt-Notiz: Verbund V-Go – App (Android, `at.verbund.charge`, White-Label der SMATRICS-App `at.eprovider`)

| | |
|---|---|
| Analysiert am | 2026-10-17 |
| Analysiert von | chargeglobe |
| Methode | Statische Analyse der APK (Version 1.1.0, Build 545, Kotlin, Retrofit/OkHttp/Hilt): `BuildConfig`, `NetworkModule`, Retrofit-Interfaces `PoiApi`, `B2CApi`, `WhitelabelingApi`; Abruf mit curl |
| Tier | 2 (inoffizieller App-Endpunkt; **in der App eingebetteter, für alle Installationen gleicher Bearer-Key**) |
| Zugang | statischer App-Key (`BuildConfig.POI_API_KEY`, Bearer), kein Nutzerkonto nötig |
| ToS geprüft | nein, **offen**; ein eingebetteter Fremd-Key widerspricht dem Geist von CLAUDE.md Regel 6 (Klärung mit SMATRICS nötig) |
| Lizenz / Attribution | keine Angabe |
| Rate-Limit beobachtet | keins ausgelöst (8 Aufrufe) |
| Capabilities | StaticInventory · EvseIds · LiveStatusPerEvse · BulkByBoundingBox · **BulkAll** · ByEvseId · ByLocationId · DeltaSince – kein Preis |

## Befund

`BuildConfig` des Flavors `VerbundProductionBackend` nennt alle Backends (die URLs sind öffentlich einsehbar; Schlüsselwerte stehen hier bewusst nicht):

| Konstante | Wert |
|---|---|
| `BASE_URL_POI` | `https://poi-service.smatrics-api.com/b2c/` (Standortdaten) |
| `BASE_URL_B2C_SERVICE` / `BASE_URL_B2C_WEBSOCKET` | `https://b2c-service.smatrics-api.com/api/v1/` / `wss://b2c-service.smatrics-api.com/ws/` (Konto, Sessions, Live-Laden) |
| `BASE_URL_ESB_API` | `https://api.smatrics.com/EsbAPI/` (Start/Stopp, Kundendaten) |
| `BASE_URL_WHITELABELING_API` | `https://verbundapp.smatrics.com/api/2.2/` (Partner-Konfiguration) |
| `BASE_URL_AUTH_SERVICE`, `REALM` | `https://auth-smatrics.verbund.com/auth/`, `verbund-b2c` (Keycloak, Client `mobile-app-cli`) |
| `POI_API_KEY`, `POI_CHARGING_HISTORY_API_KEY` | je ein 64-Zeichen-Key; `NetworkModule.providePoiApi` hängt ihn per `BearerAuthInterceptor` als `Authorization: Bearer <key>` an |

Fundstelle der Keys: `at/eprovider/BuildConfig.java` und `at/eprovider/di/NetworkModule.java` im dekompilierten Code. Das Replay-Skript erwartet den POI-Key in `VGO_POI_KEY`.

## Endpunkte (`PoiApi`, Basis `https://poi-service.smatrics-api.com/b2c/`, Bearer-Key, JSON)

| Methode, Pfad | Body / Parameter | Antwort |
|---|---|---|
| `POST locations/reduced/bounding-box` | `{"latitude_north","latitude_south","longitude_east","longitude_west"}` | Standorte mit allen EVSEs, Steckern, Status (`ReducedB2CLocation`) |
| `GET locations/reduced` | – | **Gesamtbestand** |
| `GET locations/reduced/delta?lastCall=<Zeitstempel>` | `lastCall` | seit dem Zeitpunkt geänderte Standorte (nicht aufgerufen) |
| `POST locations/` | `{"cpos":[{"operatorId","partnerName"}]}` | volle Standorte je Betreiber (nicht aufgerufen) |
| `POST locations/location-ids` | `{"location_ids":[…]}` | volle Standorte (mit Adresse, Bildern, Wegbeschreibung) |
| `POST locations/evse-ids` | `{"evse_ids":[…]}` | volle Standorte zu EVSE-IDs (nicht aufgerufen) |
| `POST evses/status` | `{"evse_ids":[…]}` | `[{"evse_id","status","status_last_updated"}]` |

### Demo 1: Kartenausschnitt (`POST locations/reduced/bounding-box`)
- Ausschnitt Graz (47.05–47.09 N, 15.40–15.47 E): 200, 126 kB, **101 Standorte, 256 EVSEs**; Ausschnitt Klagenfurt (46.55–46.64 N, 14.20–14.35 E): 161 Standorte, 399 EVSEs.
- Antwort (Standort Krumpendorf, gekürzt; unverändert aus dem Abruf):
```json
{ "id": "1420842846621936",
  "name": [ { "language": "de-DE", "text": "ProGe_Krumpendorf" } ],
  "operator": { "id": "AT*HTB", "name": "ChargePoint Austria GmbH" },
  "suboperator": { "id": "ChargePoint Austria GmbH", "name": "ChargePoint Austria GmbH" },
  "coordinates": { "longitude": "14.208428", "latitude": "46.621936" },
  "time_zone": "Europe/Vienna",
  "opening_times": { "twentyfourseven": true, "regular_hours": [], "exceptional_openings": [], "exceptional_closings": [] },
  "facilities": [], "last_updated": "2024-10-24T05:36:05Z",
  "evses": [ { "evse_id": "AT*HTB*E1011702", "status": "AVAILABLE",
      "capabilities": [ "RFID_READER", "REMOTE_START_STOP_CAPABLE" ],
      "connectors": [ { "id": "1", "standard": "IEC_62196_T2", "format": "SOCKET", "power_type": "AC_3_PHASE", "power_kw": 11.0 } ],
      "last_updated": "2024-10-24T05:36:05Z" } ] }
```

### Demo 2: Gesamtbestand (`GET locations/reduced`)
- 200, **2,6 MB** (gzip-komprimiert, mit `curl --compressed` laden), **29.527 Standorte, 91.617 EVSEs**. Betreiber nach Standorten: ChargePoint Austria 11.132, EnBW 2.608, LichtBlick 2.117, EVN 1.362, Wien Energie 1.074, SMATRICS (`AT*SMA`) 996, Salzburg AG 880, E.ON Drive 812 …
- Die Koordinaten reichen von −82° bis +82° Breite: Das ist **nicht nur Österreich, sondern der gesamte Roaming-Bestand**; für AT ist nach Land bzw. Box zu filtern.
- Status im Gesamtbestand: AVAILABLE 68.491 · CHARGING 15.313 · OUTOFORDER 6.516 · UNKNOWN 916 · PLANNED 336 · RESERVED 29 · INOPERATIVE 16.

### Demo 3: Status je EVSE (`POST evses/status`)
`{"evse_ids":["AT*HTB*E1011702"]}` → `[{"evse_id":"AT*HTB*E1011702","status":"AVAILABLE","status_last_updated":"2026-10-16T04:11:03Z"}]`

### Demo 4: Vollständiger Standort (`POST locations/location-ids`)
`{"location_ids":["1420842846621936"]}` liefert zusätzlich `address` ("Heinzelweg 16"), `city`, `postal_code`, `country` (ISO-3, `AUT`) und je EVSE `status_last_updated`, `directions[]`, `images[]`.

### Mapping auf das Ingest-Modell
- `evses[].evse_id` → `ProviderEvseSnapshot.EvseId` (OCPI-Format mit `*`, normalisieren)
- `id` (Standort, numerischer String) → Standort-Schlüssel der Quelle; `operator.id` → Betreiber-ID
- `connectors[].standard`/`format`/`power_type`/`power_kw` → bereits OCPI-Vokabular
- `status` + `status_last_updated` → Status; `last_updated` ist der Stand der Stammdaten, nicht des Status

## Status-Mapping

| Rohwert | OCPI-Status |
|---|---|
| `AVAILABLE` | `AVAILABLE` |
| `CHARGING` | `CHARGING` |
| `RESERVED` | `RESERVED` |
| `OUTOFORDER` | `OUTOFORDER` |
| `INOPERATIVE` | `INOPERATIVE` |
| `PLANNED` | `PLANNED` |
| `UNKNOWN` | `UNKNOWN` |

## Stecker-Mapping
OCPI direkt. Im Grazer Test: `IEC_62196_T2` (178), `IEC_62196_T2_COMBO` (65), `CHADEMO` (12), `DOMESTIC_F` (1); `format` `SOCKET`/`CABLE`.

## Preisdarstellung
Der POI-Dienst liefert **keine Preise**. Preis- und Tarifendpunkte der App (`B2CApi`: `customers/customer/at-home/pricing`; Whitelabeling: `partner/{EMP_ID}`) gehören zum Konto bzw. zur Partnerkonfiguration und wurden nicht aufgerufen.

## Replay
`VGO_POI_KEY=… docs/endpunkte/replay/vgo-smatrics.sh` (Bounding Box). Gesamtbestand: Kommentar am Ende des Skripts.

## Offene Punkte / Risiken
- **Wert für chargeglobe:** größter Bestand der fünf Apps (91.600 EVSEs mit Live-Status, EVSE-IDs, OCPI-nah, **Delta-Endpunkt**), grundsätzlich ideal für Inventar und Status in AT und DACH.
- **Compliance:** Der Zugang beruht auf einem aus der App extrahierten Key, den alle Installationen teilen. Das ist kein anonymer Endpunkt im Sinn von CLAUDE.md Regel 6. Vor einem Adapter Erlaubnis bzw. Partnerzugang bei SMATRICS/Verbund anfragen; der Key gehört nicht ins Repo, sondern ins Admin-UI (`provider.api_key`).
- Der Key kann mit jedem App-Release rotieren. `POI_CHARGING_HISTORY_API_KEY` gehört zu `PoiChargingHistoryApi` und wurde nicht verwendet.
- Aktualität: `status_last_updated` des Stichprobenstandorts lag Stunden zurück; die tatsächliche Aktualisierungsrate der Quelle ist zu messen (Delta-Abruf alle 1–2 min gegen Gesamtbestand).
- Betreiber `AT*SMA` hat im Feld `operator.name` nur die ID; `suboperator` kann den Firmennamen enthalten.
