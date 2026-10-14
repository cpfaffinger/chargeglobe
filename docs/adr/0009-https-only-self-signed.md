# ADR-0009: HTTPS-only am exponierten Port mit self-signed Zertifikat

**Status:** akzeptiert · **Datum:** 2026-10-13

## Kontext
Browser im HTTPS-only-Modus (z. B. Firefox) lassen sich gegen einen reinen HTTP-Port nicht sinnvoll zum Debuggen verwenden, und Browser-Funktionen wie die Geolocation-API ([ADR-0007](0007-karte-standort-routing.md)) setzen einen sicheren Kontext voraus. Das öffentlich gültige Zertifikat stellt der vorgelagerte Loadbalancer (nginx, außerhalb dieses Repos) bereit.

## Entscheidung
1. **Der exponierte Port (`CHARGEGLOBE_PORT`, Default `5005`) spricht ausschließlich HTTPS.** Ein HTTP-Request auf diesen Port wird per `301` auf `https://` umgeleitet; einen separaten HTTP-Port gibt es nicht.
2. **Das Zertifikat ist self-signed und bleibt es dauerhaft.** Der `web`-Container erzeugt es beim ersten Start selbst (RSA 2048, 10 Jahre gültig, SAN: `localhost`, `127.0.0.1` und der Hostname aus `PUBLIC_BASE_URL`) und legt es im Bind-Mount `./data/tls` ab ([ADR-0011](0011-bind-mounts-statt-volumes.md)), damit eine im Browser gespeicherte Ausnahme Rebuilds übersteht. Wird das Verzeichnis gelöscht, entsteht ein neues Zertifikat.
3. **TLS endet im `web`-Container.** Innerhalb des Compose-Netzes spricht nginx weiter HTTP mit `api:8080`; `api`, `crawler`, `worker` und `db` veröffentlichen keine Ports.
4. **Öffentlich gültige Zertifikate, Let's Encrypt und Caddy bleiben außerhalb der App.** Der Loadbalancer terminiert TLS für Endnutzer und spricht den App-Port per HTTPS an, ohne das self-signed Zertifikat zu validieren. Live ist das der nginx des Hosts mit dem Cloudflare-Origin-Zertifikat hinter dem Cloudflare-Proxy; der App-Port ist dort nur an `127.0.0.1` gebunden (`CHARGEGLOBE_BIND_ADDRESS`). Die Client-Adresse reicht er als `X-Forwarded-For` weiter; der `web`-Container vertraut dem Header nur von privaten Adressen.
5. **Die App sendet kein HSTS.** Weder nginx im `web`-Container noch die API setzen `Strict-Transport-Security`, und die API leitet nie selbst auf HTTPS um. HSTS gilt je Hostname über alle Ports und verbietet dem Browser, für ein nicht vertrauenswürdiges Zertifikat eine Ausnahme anzulegen; mit einem self-signed Zertifikat wäre der App-Port damit nicht mehr erreichbar. Wenn HSTS gewünscht ist, setzt es der Loadbalancer mit dem gültigen Zertifikat. Ein Integrationstest stellt sicher, dass die API den Header nicht sendet.

## Alternativen
- HTTP am App-Port und TLS nur im Loadbalancer: Direktzugriff auf den Dev-Port wäre im HTTPS-only-Modus nicht debugbar und kein sicherer Kontext.
- Zertifikatsverwaltung in der App (ACME): doppelt sich mit dem Loadbalancer und braucht Port 80/443 sowie DNS-Kontrolle.
- Eigene CA für die Entwicklung (mkcert): müsste auf jedem Entwicklerrechner installiert werden; die Browser-Ausnahme genügt.

## Konsequenzen
- Direktzugriff zeigt einmalig eine Zertifikatswarnung; `curl` braucht `-k`. Healthcheck: `curl -k https://<host>:5005/api/v1/health`.
- `PUBLIC_BASE_URL` beginnt mit `https://`; ändert sich der Hostname, muss das Zertifikat neu erzeugt werden (`./data/tls` löschen).
- Cookies (Refresh-Token) werden immer mit `Secure` gesetzt.
- Setzt ein anderer Dienst unter demselben Hostnamen oder einer übergeordneten Domain (`includeSubDomains`) HSTS, verweigert der Browser die Ausnahme für den App-Port trotzdem. Abhilfe liegt außerhalb der App: Zugriff über den Loadbalancer mit gültigem Zertifikat oder über einen Hostnamen ohne HSTS-Eintrag.
