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
| `StatusPoll` | Status aller EVSEs einer Quelle | adaptiv 1–15 min | hoch → Hauptgegenstand der Optimierung |
| `PricePoll` | Ad-hoc-/EMP-Preise | 1×/Tag je EVSE, sofort bei neuem EVSE | mittel |
| `Reconcile` | Vergleich: EVSEs, die seit 14 Tagen in keiner Quelle mehr auftauchen → `lifecycle=REMOVED`; neue EVSEs im amtlichen Verzeichnis ohne Status-Quelle → Hinweis | 1×/Tag | gering |

## Status-Polling-Strategie

### 1. Billigsten Abfragemodus wählen
Adapter deklarieren per Capability, was geht. Der Scheduler bevorzugt:

1. `BulkAll` – ein Request liefert alle Status (ideal, z. B. OCPI-`Locations` mit Status, manche Web-Karten).
2. `BulkByBoundingBox` – Scope-Regionen werden in Kacheln zerlegt (Z8–Z10, abhängig von Antwortgrößen-Limits der Quelle). Kacheln ohne Ladepunkte werden übersprungen, Kacheln mit vielen Ladepunkten weiter unterteilt (Quadtree, persistiert in `poll_tile`).
3. `ByEvseId` – nur für Ladepunkte, die anders nicht erreichbar sind; streng rate-limitiert.

### 2. Adaptive Intervalle je Ladepunkt
Ziel: Frische dort, wo sich etwas ändert, ohne Request-Budget zu sprengen.

```
interval = clamp(base[power_class] × activity_factor × time_of_day_factor, min, max)

base:    HPC ≥ 150 kW: 2 min · DC 50–149 kW: 3 min · AC: 5 min
activity_factor: 0.5 wenn ≥ 6 Statuswechsel in 24 h; 2.0 wenn 0 Wechsel in 7 Tagen
time_of_day: 1.0 (06–22 Uhr) · 3.0 (22–06 Uhr)
min/max: 1 min / 30 min
```

Bei Bulk-Modi ist das Intervall je Kachel = Minimum der enthaltenen EVSE-Intervalle.

### 3. Request-Budget je Provider
`RateLimitPolicy` (z. B. 60/min, Burst 10) wird als **clusterweiter** Token-Bucket in Postgres (`provider_rate_budget`) umgesetzt, damit mehrere Crawler-Instanzen zusammen das Limit einhalten; die Werte sind im Config-Service änderbar. Zusätzlich ein **Tagesbudget**; bei Überschreitung werden Intervalle global gestreckt statt Requests fallen zu lassen. Antworten mit `429`/`503` → exponentielles Backoff mit Jitter, Beachtung von `Retry-After`, nach 5 Fehlern in Folge Circuit-Breaker für 10 min + Alarm.

### 4. Change Detection & Dedupe
- `content_hash` der Antwort: identische Antwort → nur `last_seen` aktualisieren, kein neuer Snapshot.
- Statuswechsel → neues `evse_status_interval`, Domain-Event `EvseStatusChanged`.
- Flapping-Schutz: Wechsel, der innerhalb von 30 s zurückgenommen wird, wird mit `confidence=0.5` markiert.

### 5. Höflichkeit
- Eigener, ehrlicher `User-Agent: chargeglobe/<version> (+https://github.com/…; contact@…)`.
- `robots.txt` wird für Web-Karten respektiert (für reine JSON-APIs oft nicht definiert; dann gilt das konfigurierte Limit).
- Conditional Requests (`ETag`/`If-Modified-Since`) wo unterstützt.
- Nie parallel gegen dieselbe Quelle über das Limit hinaus; Verteilung der Last über das Intervall (Jitter), keine Spitzen zur vollen Minute.

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
