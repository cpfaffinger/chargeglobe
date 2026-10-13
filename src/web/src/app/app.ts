import { HttpClient } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { RouterOutlet } from '@angular/router';

interface HealthResponse {
  status: string;
  db: string;
}

@Component({
  imports: [RouterOutlet],
  selector: 'app-root',
  template: `
    <main>
      <h1>chargeglobe</h1>
      <p id="api-status">API: {{ apiStatus() }}</p>
      <p id="db-status">DB: {{ dbStatus() }}</p>
      <router-outlet />
    </main>
  `,
})
export class App {
  private readonly http = inject(HttpClient);

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
