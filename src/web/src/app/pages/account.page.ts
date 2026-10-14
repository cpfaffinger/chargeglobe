import { HttpClient } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { AuthService } from '../auth/auth.service';

interface HealthResponse {
  status: string;
  db: string;
}

/** Account of the signed-in user (guarded in app.routes.ts) with the state of API and database. */
@Component({
  imports: [MatCardModule],
  selector: 'app-account',
  template: `
    <h1>Mein Konto</h1>
    <mat-card appearance="outlined" id="account">
      <mat-card-content>
        <dl>
          <dt>E-Mail</dt>
          <dd id="account-email">{{ auth.me()?.email }}</dd>
          <dt>Rollen</dt>
          <dd id="account-roles">{{ auth.me()?.roles?.join(', ') }}</dd>
          <dt>Berechtigungen</dt>
          <dd id="account-permissions">{{ auth.me()?.permissions?.join(', ') }}</dd>
        </dl>
      </mat-card-content>
    </mat-card>
    <section>
      <h2>Systemstatus</h2>
      <p id="api-status">API: {{ apiStatus() }}</p>
      <p id="db-status">DB: {{ dbStatus() }}</p>
    </section>
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
