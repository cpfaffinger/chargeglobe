import { HttpClient } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth.service';

interface HealthResponse {
  status: string;
  db: string;
}

/** Start page, reachable without an account: map placeholder and API/database state. */
@Component({
  imports: [RouterLink],
  selector: 'app-home',
  template: `
    <section id="map-placeholder" aria-label="Karte">
      <p>Hier entsteht die Karte der Ladepunkte.</p>
    </section>
    <p id="api-status">API: {{ apiStatus() }}</p>
    <p id="db-status">DB: {{ dbStatus() }}</p>
    @if (auth.me(); as me) {
      <section id="session">
        <p>
          Angemeldet als <strong id="me-email">{{ me.email }}</strong>
        </p>
        <p id="me-permissions">Berechtigungen: {{ me.permissions.join(', ') }}</p>
        <button type="button" id="logout" (click)="auth.logout()">Abmelden</button>
      </section>
    } @else {
      <p id="anonymous">
        <a routerLink="/login">Anmelden</a> oder <a routerLink="/register">Konto anlegen</a>, um alle
        Funktionen zu nutzen.
      </p>
    }
  `,
})
export class HomePage {
  private readonly http = inject(HttpClient);
  protected readonly auth = inject(AuthService);

  protected readonly apiStatus = signal('…');
  protected readonly dbStatus = signal('…');

  constructor() {
    this.http.get<HealthResponse>('/api/v1/health').subscribe({
      next: (health) => {
        this.apiStatus.set(health.status);
        this.dbStatus.set(health.db);
      },
      error: () => {
        this.apiStatus.set('unavailable');
        this.dbStatus.set('unknown');
      },
    });
  }
}
