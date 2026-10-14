# ADR-0007: Karte, Nutzerstandort und Routing auf offener OSM-Basis

**Status:** akzeptiert · **Datum:** 2026-10-13

## Kontext
Die Web-App zeigt im Endausbau alle Ladepunkte auf einer Karte, soll die Position des Betrachters anzeigen und Routen (inkl. Straßenkarte) in die Karte einbetten. Google Maps scheidet wegen der API-Kosten aus. Gesucht ist ein Stack ohne nutzungsabhängige Kosten, ohne API-Key-Zwang und ohne Lizenzrisiko, der zum Compose-Betrieb passt.

## Entscheidung
1. **Renderer: MapLibre GL JS** (BSD-3-Clause, npm-Paket `maplibre-gl`) im Angular-Frontend. Vektorkarte, Clustering (GeoJSON-Source mit `cluster: true`, Daten aus `GET /locations` des sichtbaren Ausschnitts) und Routen-Layer laufen im Browser. Die Bibliothek wird erst auf der Kartenseite nachgeladen (eigener Lazy-Chunk), ihr Web-Worker wird als Asset unter `/maplibre/maplibre-gl-worker.mjs` ausgeliefert. Nur der Adapter `src/web/src/app/map/map-engine.ts` importiert `maplibre-gl`; Seiten sprechen die schmale Abstraktion `MapEngine` an (Karte erzeugen, Standort auslösen, aufräumen).
2. **Basiskarte: OpenStreetMap-Vektorkacheln über OpenFreeMap.** Die öffentliche OpenFreeMap-Instanz verlangt weder Registrierung noch API-Key und setzt keine Limits für Kartenaufrufe; sie ist spendenfinanziert und gibt keine Verfügbarkeitszusage. Die Style-URL ist deshalb eine Einstellung im Config-Service (`map.style_url`) und nicht im Code verdrahtet; zusammen mit `map.attribution` und dem Start-Ausschnitt (`map.start.latitude`, `map.start.longitude`, `map.start.zoom`) liefert sie der anonyme Endpunkt `GET /api/v1/map/config` an das Frontend. Wird Verfügbarkeit kritisch, werden die Kacheln selbst gehostet (OpenFreeMap-Self-Hosting oder Protomaps/PMTiles als zusätzlicher Compose-Service); das Frontend ändert sich dadurch nicht.
3. **Attribution** ist Pflicht und immer sichtbar: das Attribution-Control ist dauerhaft aufgeklappt und zeigt den Text aus `map.attribution` (Default „© OpenStreetMap contributors, OpenMapTiles“, ODbL) zusätzlich zu den Angaben, die MapLibre aus den Kachel-Metadaten des Styles einblendet.
4. **Nutzerstandort: Browser-Geolocation-API** über das `GeolocateControl` von MapLibre (mit `trackUserLocation`), ausgelöst über den Knopf „Mein Standort“ der Kartenseite; das Icon des Controls ist ausgeblendet, damit es nur einen Knopf gibt. Die Position wird nur im Browser verarbeitet, nach ausdrücklicher Zustimmung des Nutzers, und weder an die API gesendet noch gespeichert. Ohne sicheren Kontext, ohne Geolocation im Browser, bei abgelehnter Freigabe und ohne WebGL zeigt die Seite einen verständlichen Hinweis. Einzige Ausnahme ist eine Routenanfrage, bei der Start/Ziel an die eigene API gehen; sie werden dort nicht persistiert und nicht geloggt. Die Standortanzeige steht auch anonymen Besuchern zur Verfügung.
5. **Routing: selbst gehostete Engine Valhalla** (MIT) als Compose-Service `routing`, gespeist aus OSM-Extrakten (Geofabrik: zuerst Österreich, mit dem Scope wachsend). Der Service ist nicht exponiert; das Frontend spricht nur `POST /api/v1/routes` an. Die API kapselt die Engine hinter dem Port `IRoutingEngine` (Anti-Corruption-Layer), sodass OSRM oder GraphHopper ohne API-Änderung einsetzbar bleiben. Routing ist eine Funktion für angemeldete Nutzer ([ADR-0005](0005-identity-rbac-openapi.md)).
6. **Keine Drittanbieter-Aufrufe außer der Basiskarte**: kein Tracking, keine externen Schriften oder Skripte. Der Browser ruft außerhalb der eigenen App nur Style, TileJSON, Kacheln, Glyphen und Sprites der konfigurierten Style-URL ab; beim Default (`https://tiles.openfreemap.org/styles/liberty`) ist das ausschließlich der Host `tiles.openfreemap.org`.
7. **Routenanfragen per POST**, obwohl sie nur lesen: Koordinaten sind personenbezogen und sollen nicht in URLs und Access-Logs landen.

## Alternativen
- **Google Maps Platform**: nutzungsabhängige Kosten, Key-Pflicht, restriktive Bedingungen für die Darstellung fremder Daten.
- **Mapbox**: Mapbox GL JS ist seit Version 2 proprietär und wird je Kartenaufruf abgerechnet; Token-Pflicht. MapLibre ist der freie Fork der letzten offenen Version.
- **Rasterkacheln von `tile.openstreetmap.org`**: die Nutzungsrichtlinie der OSMF erlaubt keine intensive Nutzung durch Dritt-Apps und gibt keine Verfügbarkeitszusage; nur als Notlösung für die Entwicklung.
- **Öffentliche Routing-Demoserver** (OSRM/FOSSGIS, Valhalla-Demo): Fair-Use mit etwa 1 Request/s, kein produktiver Einsatz vorgesehen.
- **openrouteservice (gehostet)**: kostenloser Plan mit API-Key und 2.000 Routen/Tag; für einen öffentlichen Dienst zu knapp und eine externe Abhängigkeit, an die Standortdaten der Nutzer fließen würden.
- **OSRM selbst gehostet**: sehr schnell, aber je Profil ein eigener vorberechneter Graph und wenig Laufzeit-Optionen; Valhalla erlaubt Kostenmodelle je Anfrage (Maut/Autobahn meiden) und Isochronen aus einem Graphen.

## Konsequenzen
- Die Geolocation-API funktioniert nur in einem sicheren Kontext (HTTPS oder `localhost`). Das ist durch den HTTPS-only-Betrieb erfüllt ([ADR-0009](0009-https-only-self-signed.md)); beim Direktzugriff mit dem self-signed Zertifikat muss der Browser die Zertifikatsausnahme akzeptiert haben.
- Der `routing`-Service braucht beim ersten Start Zeit und Speicher für den Graphaufbau und ein eigenes Volume; OSM-Extrakte werden periodisch aktualisiert (Single-Runner-Job des Workers).
- Routen-Antworten tragen die ODbL-Attribution in `meta.attribution`.
- Spätere Ausbaustufen bauen darauf auf: Ladepunkte entlang einer Route (PostGIS-Korridorabfrage), Erreichbarkeits-Isochronen.
