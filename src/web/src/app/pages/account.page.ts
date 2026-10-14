import { HttpClient } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { AuthService } from '../auth/auth.service';

interface HealthResponse {
  status: string;
  db: string;
}

/** Account of the signed-in user (guarded in app.routes.ts) with the state of API and database. */
@Component({
  selector: 'app-account',
  template: `
    <h2>Mein Konto</h2>
    <dl>
      <dt>E-Mail</dt>
      <dd id="account-email">{{ auth.me()?.email }}</dd>
      <dt>Rollen</dt>
      <dd id="account-roles">{{ auth.me()?.roles?.join(', ') }}</dd>
      <dt>Berechtigungen</dt>
      <dd id="account-permissions">{{ auth.me()?.permissions?.join(', ') }}</dd>
    </dl>
    <h3>Systemstatus</h3>
    <p id="api-status">API: {{ apiStatus() }}</p>
    <p id="db-status">DB: {{ dbStatus() }}</p>
  `,
})
export class AccountPage {
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
