# 05 – Crawling-Konzept

## Quellen-Tiers

| Tier | Art | Beispiele (AT) | Liefert | Risiko |
|---|---|---|---|---|
| **0 – Amtlich / Open Data** | Verzeichnisse mit Meldepflicht, nationale Zugangspunkte, Open-Data-Portale | E-Control Ladestellenverzeichnis, NAP (AFIR-Daten, DATEX II), data.gv.at, OpenChargeMap, OpenStreetMap | Inventar-Backbone, EVSE-IDs, Ad-hoc-Preise, teils Status | sehr gering; Lizenz beachten |
| **1 – Öffentliche Web-Karten** | Browser-Karten der CPOs/EMPs; XHR-Endpunkte sind ohne App-Analyse sichtbar | Betreiber-Websites („Ladestationen finden“), Tesla-Standortkarte, Electroverse-Webkarte | Status je Ladepunkt (oft), Preise (teils) | gering–mittel: ToS, Rate-Limits, Format-Änderungen |
| **2 – App-Backends** | Endpunkte, die nur die Mobile-App nutzt | EMP-Apps mit Tarifanzeige, CPO-Apps mit Live-Status | Status, EMP-Tarife je Ladepunkt | mittel: Auth/Signaturen, Pinning, ToS |
| **3 – Partner-Feeds** | Vertraglich/auf Anfrage: OCPI oder OICP-Zugang | CPOs, die Daten teilen (AFIR verpflichtet zur Weitergabe an den NAP, nicht an uns – aber viele geben OCPI-Zugang auf Anfrage) | alles, sauber, mit Push | keines; Aufwand im Partnering |

**Reihenfolge:** Erst Tier 0 vollständig (Inventar + IDs), dann Tier 1 für Status, dann Tier 2 für Preise/Lücken, Tier 3 opportunistisch. Jede höhere Tier-Quelle hängt sich an bestehende EVSE-IDs; sie darf Stammdaten nur ergänzen, nicht ersetzen (siehe Präzedenz in [03](03-datenmodell.md)).

## Jobtypen und Frequenzen

| Job | Was | Frequenz (Default) | Kosten |
|---|---|---|---|
| `InventoryFull` | kompletter Export/Scan der Quelle für aktive Regionen | 1×/Tag (Tier 0), 1×/Woche (OSM) | gering (ein Download) bis mittel (bbox-Scan) |
| `InventoryDelta` | `since`-Abfrage, falls unterstützt | 1×/Stunde | gering |
| `StatusPoll` | Status um einen Abfragepunkt (bzw. einer Region bei Quellen ohne Punktsuche) | je Punkt 5/15/60 min nach Aktivität, nachts ×3 | hoch → Hauptgegenstand der Optimierung |
| `TilePlan` | Abfragepunkte aus den bekannten Standorten neu berechnen, Prioritäten aus der Aktivität | 1×/Stunde | keine Requests |
| `PricePoll` | Ad-hoc-/EMP-Preise | 1×/Tag je EVSE, sofort bei neuem EVSE | mittel |
| `Reconcile` | Vergleich: EVSEs, die seit 14 Tagen in keiner Quelle mehr auftauchen → `lifecycle=REMOVED`; neue EVSEs im amtlichen Verzeichnis ohne Status-Quelle → Hinweis | 1×/Tag | gering |

## Status-Polling-Strategie (passiv)

Leitlinie: **so frisch wie das Budget es erlaubt, nie mehr.** Ein festes Stundenbudget je Quelle wird gleichmäßig verteilt; wo sich viel ändert oder jemand hinschaut, wird öfter gelesen, ruhige Gegenden selten. Fehler der Quelle führen zu Pausen statt zu Wiederholungen.

### 1. Billigsten Abfragemodus wählen
Adapter deklarieren per Capability, was geht. Der Scheduler bevorzugt:

1. `BulkAll` – ein Request (oder eine Seitenfolge) liefert alles (ideal, z. B. OCPI-`Locations` mit Status, manche Web-Karten). Für das Inventar legt der Scheduler dann **einen** Plan für alle Regionen aus `provider.regions` an (Region z. B. `AT-9,AT-3,AT-4,AT-1`, Box = umschließendes Rechteck der Regionen) statt einen je Region; die E-Control liest so ihr Register (`GET /search/stations`, ganz Österreich in 17 Seiten). Ein Bulk-Status zählt nur, wenn er aktuell ist: die DATEX-II-Status-Publikation der E-Control ist ein Stunden alter Snapshot und wird nicht genutzt ([Endpunkt-Notiz](endpunkte/econtrol.md)).
2. `NearestToPoint` – ein Request liefert den Live-Status der `n` nächsten Stationen um einen Punkt (E-Control `GET /search`, `n` = 100). Der Status wird über **Abfragepunkte** gelesen (Tabelle `poll_tile`, Abschnitt 2), je Punkt genau ein Request.
3. `BulkByBoundingBox` – je Region ein Status-Plan mit der Bounding-Box der Region (`poll_plan`), für Quellen ohne Punktsuche.
4. `ByEvseId` – nur für Ladepunkte, die anders nicht erreichbar sind; streng rate-limitiert.

### 2. Abfragepunkte (`poll_tile`)
Der Plan `TILE_PLAN` (ein Plan je Quelle für alle Regionen, stündlich) berechnet die Punkte aus den bekannten aktiven Standorten der Quelle (`source_record`) in der Box der Regionen:

- **Überdeckung:** In einer raumfüllenden Reihenfolge (Z-Ordnung) setzt jeder noch nicht abgedeckte Standort einen Punkt in die Mitte seiner `0,9 × n` nächsten Standorte; die `0,9 × n` nächsten dieser Mitte gelten als abgedeckt (verliert die Mitte den Startstandort, wird er selbst der Punkt). Die 10 % Reserve fangen Stationen ab, die die Quelle kennt, chargeglobe aber nicht (inaktiv, außerhalb der Regionen). Gleiche Eingabe ergibt gleiche Punkte; ein Punkt behält daher seinen Abrufzustand über die stündliche Neuberechnung. Ohne bekannte Standorte (Inventar noch nicht verarbeitet) versucht der Plan es nach 10 min erneut.
- **Priorität** aus den Statuswechseln der abgedeckten Standorte in den letzten 24 h (`evse_status_interval`): `HOT` ab 24 Wechseln oder ab 6 Wechseln mit Schnelllader (≥ 150 kW), `QUIET` ohne Wechsel und ohne Schnelllader, sonst `NORMAL`. Intervalle aus dem Config-Service: `provider.tile_interval_hot_minutes` (5), `…_normal_minutes` (15), `…_quiet_minutes` (60); von 22 bis 6 Uhr (Wien) dreimal so lang.
- **Reihenfolge:** Der Scheduler arbeitet erst fällige Pläne ab, dann den am stärksten überfälligen Punkt (Alter ÷ Intervall, nie gelesene zuerst); vor allem anderen kommen **angeforderte** Punkte: Öffnet jemand einen Standort (`GET /locations/{id}`), wird sein Punkt markiert (`demand_at`) und mit dem nächsten freien Request gelesen, höchstens alle 2 min. Der API-Request wartet darauf nicht.
- **Parallelität:** Jede Crawler-Instanz arbeitet einen Punkt nach dem anderen ab; mehrere Instanzen teilen sich die Punkte über `FOR UPDATE SKIP LOCKED` und dasselbe Budget.

Mit 600 Requests/h und ~200 Punkten (Ost-Österreich) bleiben lebhafte Punkte bei 5 min, normale bei 15 min; reicht das Budget nicht, verlängert sich das Alter aller Punkte gleichmäßig (überfälligste zuerst), statt dass Requests wegfallen.

### 3. Request-Budget je Provider
Jeder Request nimmt vor dem Senden ein Token aus dem **clusterweiten** Token-Bucket in Postgres (`provider_rate_budget`). Der Bucket füllt sich mit `provider.request_budget_per_hour` (Default 600, höchstens 2.400, gleichmäßig über die Stunde, Burst ⅕ der Minutenrate), nie schneller als die `RateLimitPolicy` des Descriptors (E-Control 40/min); ohne Token wartet der Request die errechnete Zeit (100 ms – 1 min). Zusätzlich hält der Adapter je Prozess die Descriptor-Rate ein (`RateLimitingHandler`). Requests fallen nie weg, Läufe dauern nur länger.

### 4. Pausen nach Fehlern (Anti-Flood)
Fehler der Quelle werden **nicht wiederholt**; stattdessen pausiert der Crawler die Quelle clusterweit (`provider_backoff`):

| Fehler | Reaktion |
|---|---|
| `429`, `5xx`, Netzwerkfehler, Timeout, unerwartetes Format | Pause = n × `provider.cooldown_step_minutes` (Default 5) für den n-ten Fehler in Folge, höchstens `provider.cooldown_max_minutes` (Default 120); ein längeres `Retry-After` der Quelle gewinnt |
| `401`, `403`, fehlender Key | Quelle gesperrt, bis sich eine ihrer Einstellungen (Scope `provider:<key>`) ändert |
| Fehler außerhalb der Quelle (z. B. Datenbank) | keine Pause, der Plan läuft nach höchstens 5 min erneut |

Ein erfolgreicher Lauf mit mindestens einem Request beendet die Fehlerserie. Während der Pause bekommt die Quelle keine Pläne und keine Abfragepunkte, auch die Health-Probe (stündlich) entfällt.

### 5. Protokoll und Live-Ansicht
Jeder Lauf (Plan oder Abfragepunkt) steht in `crawler_run` (Start, Ende, Requests, Datensätze, Ergebnis, Fehler, verursachte Pause; Aufbewahrung `crawler.run_retention_days`, Default 7) und im Log des Crawlers:

```
Job STATUS_POLL econtrol point 17/212 (48.2085,16.3731, 90 locations) started (HOT, last poll 6 min ago)
Job STATUS_POLL econtrol point 17/212 (…) done: 318 records, 1 requests in 0.4 s; 412/600 requests in the last hour; 3 query points due; next plan TILE_PLAN AT-9,AT-3,AT-4,AT-1 at 09:00 UTC
E-Control register page 3/17: stations 2001–3000 of 16070
Job INVENTORY_FULL econtrol AT-9,AT-3,AT-4,AT-1 failed: E-Control search/stations returned 503.; provider paused until 08:20:00 UTC (failure 1 in a row)
```

Die Standard-Logs des `HttpClient` (eine Zeile je Request ohne Kontext) sind im Crawler auf `Warning` gefiltert. Administratoren sehen den Zustand live unter `/admin/crawler` (`GET /api/v1/admin/crawler/status` und `/runs`, Permission `jobs:read`).

### 6. Change Detection & Dedupe
- `content_hash` der Antwort: identische Antwort → nur `last_seen` aktualisieren, kein neuer Snapshot.
- Statuswechsel → neues `evse_status_interval`, Domain-Event `EvseStatusChanged`.
- Flapping-Schutz: Wechsel, der innerhalb von 30 s zurückgenommen wird, wird mit `confidence=0.5` markiert.

### 7. Höflichkeit
- Eigener, ehrlicher `User-Agent: chargeglobe/<version> (+https://github.com/…; contact@…)`.
- `robots.txt` wird für Web-Karten respektiert (für reine JSON-APIs oft nicht definiert; dann gilt das konfigurierte Limit).
- Conditional Requests (`ETag`/`If-Modified-Since`) wo unterstützt.
- Nie parallel gegen dieselbe Quelle über das Limit hinaus; das Stundenbudget wird gleichmäßig über die Stunde verteilt, keine Spitzen zur vollen Minute.

## Inventar-Merge: Ablauf je Quell-Datensatz

```
Raw-Antwort ─▶ raw_snapshot (hash, jsonb)
            ─▶ Mapper ─▶ ProviderLocationSnapshot
            ─▶ je EVSE: EvseId.Parse → Matching-Kaskade (03) → source_record upsert
            ─▶ Attribut-Merge nach Präzedenz → attribute_origin
            ─▶ Abweichung > Schwellwert? → source_conflict
            ─▶ last_seen / last_confirmed_at aktualisieren
```

Schwellwerte (Default): Koordinaten > 150 m, Leistungsabweichung > 20 %, Steckeranzahl ≠, Betreiber ≠.

## Preise crawlen

Preise sind fachlich der schwierigste Teil, weil Apps sie unterschiedlich zeigen:

| Situation | Umgang |
|---|---|
| App zeigt vollständigen Tarif am Ladepunkt (kWh, min ab X, Session, Blockiergebühr) | `ProviderPrice` mit `TariffElements` → `tariff` wird automatisch angelegt/versioniert |
| App zeigt nur „ab 0,59 €/kWh“ | flache `price_observation`; Tarif bleibt `null`; Vergleichsrechnung nur für kWh-Anteil, im UI als „unvollständig“ markiert |
| EMP hat Flat-Tarif unabhängig vom Ladepunkt (z. B. ein Preis für alle AC-Punkte eines CPO-Netzes) | ein `tariff` je (EMP × CPO × Leistungsklasse), Assignment über Regeln statt pro EVSE |
| Preis nur in PDF/Website | manuelle Pflege im Admin (Tarif-Editor), `verified_by=MANUAL`, mit Quelle-URL und Datum |
| Ad-hoc-Preis per AFIR im NAP/Verzeichnis | Tier 0, höchste Präzedenz für Ad-hoc |

Poll-Frequenz: Preise ändern sich selten, aber zu Monatsanfang gehäuft → 1×/Tag je EVSE, zusätzlich am 1. und 2. jedes Monats 3×/Tag.

## App-Analyse-Methodik (Tier 2)

Ziel ist, **öffentlich erreichbare HTTP-Endpunkte** zu identifizieren, die eine App ohne Nutzer-Login für Karte/Status/Preise nutzt, und diese so zu dokumentieren, dass ein Adapter sie mit aufgezeichneten Fixtures mocken und testen kann. Das Vorgehen wird von Maintainern lokal durchgeführt; **in das Repository kommen nur Endpunkt-Beschreibungen, Beispielantworten (sanitisiert) und der Adapter-Code – nie dekompilierter App-Code, Keys oder Zertifikate.**

1. **Web-Karte zuerst.** Hat der Anbieter eine Browser-Karte, reicht DevTools → Network → XHR. Das deckt erfahrungsgemäß die Mehrzahl der CPOs ab und ist rechtlich am unkritischsten.
2. **Traffic-Mitschnitt der App** (Android-Emulator oder eigenes Gerät, mitmproxy/HTTP Toolkit). Viele Apps nutzen Certificate Pinning; dann hilft ein Emulator mit Root und Frida/objection („ssl pinning bypass“). Nur eigene Geräte/Emulatoren, nur eigene Accounts.
3. **Statische Analyse** (jadx/apktool) nur ergänzend: um Basis-URLs, Query-Parameter, Header-Konventionen (API-Key-Header, Signatur-Schemata) und Status-Enums zu verstehen. Ergebnis ist eine Endpunkt-Notiz, kein Code-Abdruck.
4. **Bewertung**: anonym abrufbar? Nur mit App-Key (statisch in der App)? Nur mit Nutzer-Login? Signierte Requests? Daraus folgt die Einstufung:
   - anonym / statischer Key → Adapter zulässig, Key in Konfiguration (nicht im Repo), Limit konservativ.
   - Nutzer-Login erforderlich → nur mit eigenem Testaccount und ausdrücklich ToS-konform; sonst nicht anbinden, stattdessen Partner-Anfrage (Tier 3).
   - signierte/obfuskierte Requests → nicht anbinden (Aufwand, Fragilität, Rechtsrisiko).
5. **Dokumentation** im Format von [07](07-bestandsanalyse-apps-at.md): Basis-URL, Endpunkt, Parameter, Beispiel-Response (gekürzt), Capabilities, Rate-Limit-Beobachtungen, Datum der Analyse.
6. **Backend-Plattformen erkennen**: Viele österreichische CPOs nutzen dieselben White-Label-Backends (z. B. has·to·be/be.ENERGISED, jetzt ChargePoint). Ist ein Backend einmal verstanden, deckt ein parametrisierbarer Adapter mehrere Anbieter ab – siehe Übersicht in 07.

## Compliance-Leitplanken (Kurzfassung, Details in 08)

- Tier 0 immer bevorzugen; Attribution der Lizenz in jeder API-Antwort (`sources[]`).
- Keine Umgehung von Zugangsbeschränkungen im Sinne technischer Schutzmaßnahmen; Pinning-Bypass dient der *Analyse auf dem eigenen Gerät*, nicht dem Betrieb.
- Keine personenbezogenen Daten: Session-/Nutzerdaten werden nie abgefragt oder gespeichert.
- Rate-Limits so, dass unsere Last unter der eines einzelnen aktiven Nutzers pro Quelle bleibt, wo per-ID-Abfragen nötig sind.
- Abschalt-Mechanismus je Provider (Kill-Switch in Konfiguration) und Kontaktadresse im User-Agent; Takedown-Anfragen werden befolgt und dokumentiert.
