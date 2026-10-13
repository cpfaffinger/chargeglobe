# web

Angular-22-Frontend von chargeglobe (Standalone Components, Signals, Zoneless). Die Startseite ruft
`/api/v1/health` auf und zeigt den Zustand von API und Datenbank.

Befehle (Build, Tests mit Coverage, Dev-Server) stehen in der [CLAUDE.md](../../CLAUDE.md) im Repo-Root.
Im Compose-Stack liefert nginx den Build aus und reicht `/api`, `/openapi` und `/scalar` an den
`api`-Container weiter ([nginx.conf](nginx.conf)).
