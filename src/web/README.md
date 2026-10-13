# web

Angular-22-Frontend von chargeglobe (Standalone Components, Signals, Zoneless). Die Startseite ist anonym
erreichbar (Platzhalter für die Karte, Zustand von API und Datenbank aus `/api/v1/health`) und zeigt nach dem
Login E-Mail und Permissions. Seiten: `/register`, `/confirm-email`, `/login`, `/forgot-password`,
`/reset-password`, `/account` (nur angemeldet). Spec-Hilfen liegen in `src/app/testing.ts` und gehören nur zum Test-Build (`tsconfig.spec.json`). `src/app/auth` enthält `AuthService` (Access-Token im Speicher,
Refresh über Cookie), den HTTP-Interceptor und den Guard `requirePermission`.

Befehle (Build, Tests mit Coverage, Dev-Server) stehen in der [CLAUDE.md](../../CLAUDE.md) im Repo-Root.
Im Compose-Stack liefert nginx den Build aus und reicht `/api`, `/openapi` und `/scalar` an den
`api`-Container weiter ([nginx.conf](nginx.conf)). Der Container spricht ausschließlich HTTPS mit einem
self-signed Zertifikat, das beim ersten Start erzeugt wird ([ADR-0009](../../docs/adr/0009-https-only-self-signed.md)).
