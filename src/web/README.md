# web

Angular-22-Frontend von chargeglobe (Standalone Components, Signals, Zoneless). Die Startseite ist die anonym
erreichbare Karte (`src/app/pages/home.page.ts`, Code unter `src/app/map`): MapLibre GL JS mit der Konfiguration aus
`/api/v1/map/config`, Knopf „Mein Standort“ ([ADR-0007](../../docs/adr/0007-karte-standort-routing.md)). `maplibre-gl` wird nur
von `src/app/map/map-engine.ts` importiert und erst auf der Kartenseite nachgeladen; die Datei braucht WebGL, ist per
`codeCoverageExclude` (angular.json) von der Coverage ausgenommen und wird in den Specs durch einen Stub ersetzt. Der
Web-Worker von MapLibre wird als Asset unter `/maplibre/` ausgeliefert. Die Kopfzeile zeigt angemeldet die E-Mail-Adresse
(Link auf `/account`) und „Abmelden“; `/account` zeigt Rollen, Permissions und den Zustand von API und Datenbank. Seiten: `/register`, `/confirm-email`, `/login`, `/forgot-password`,
`/reset-password`, `/account` (nur angemeldet), `/setup` (nur bei leerer Instanz) und die Administration `/admin` mit den Unterseiten `/admin/users`, `/admin/roles` (Client unter `src/app/admin`), `/admin/crawler`, `/admin/mail` (Client unter `src/app/mail`) und `/admin/settings` (generisch aus `GET /api/v1/settings` gerendert, Code unter `src/app/settings`); welche Unterseite mit welcher Permission sichtbar ist, steht in `src/app/admin/admin-pages.ts`. Das Suchfeld der Toolbar liegt unter `src/app/search`. Spec-Hilfen liegen in `src/app/testing.ts` und gehören nur zum Test-Build (`tsconfig.spec.json`). `src/app/auth` enthält `AuthService` (Access-Token im Speicher,
Refresh über Cookie), den HTTP-Interceptor und die Guards `requirePermission` und `requireAnyPermission`.

Befehle (Build, Tests mit Coverage, Dev-Server) stehen in der [CLAUDE.md](../../CLAUDE.md) im Repo-Root.
Im Compose-Stack liefert nginx den Build aus und reicht `/api`, `/openapi` und `/scalar` an den
`api`-Container weiter ([nginx.conf](nginx.conf)). Der Container spricht ausschließlich HTTPS mit einem
self-signed Zertifikat, das beim ersten Start erzeugt wird ([ADR-0009](../../docs/adr/0009-https-only-self-signed.md)).
