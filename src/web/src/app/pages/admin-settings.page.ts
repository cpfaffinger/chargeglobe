import { Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { attempt } from '../auth/problem';
import { SettingRow } from '../settings/setting-row';
import { Setting, SettingsService } from '../settings/settings.service';

/**
 * Admin page of the central config service, rendered generically from GET /settings.
 * Readable with settings:read (route guard); editing needs settings:manage.
 */
@Component({
  imports: [RouterLink, SettingRow],
  selector: 'app-admin-settings',
  template: `
    <h2>Einstellungen</h2>
    <p>
      Änderungen wirken ohne Neustart in allen Containern. Einstellungen mit dem Zusatz
      <code>provider:…</code> gelten für die genannte Datenquelle.
    </p>
    @if (auth.hasPermission('settings:manage')) {
      <p id="mail-link">
        SMTP-Zugang prüfen: <a routerLink="/admin/mail">Testmail senden und Postausgang ansehen</a>
      </p>
    }
    @if (busy()) {
      <p>Einstellungen werden geladen …</p>
    }
    @for (error of errors(); track error) {
      <p class="error" role="alert">{{ error }}</p>
    }
    @for (setting of settings(); track setting.key) {
      @for (scope of setting.scopes; track scope) {
        <app-setting-row
          [setting]="setting"
          [scope]="scope"
          [canManage]="auth.hasPermission('settings:manage')"
          (changed)="replace($event)"
        />
      }
    }
  `,
})
export class AdminSettingsPage {
  protected readonly auth = inject(AuthService);

  protected readonly settings = signal<Setting[]>([]);
  protected readonly busy = signal(false);
  protected readonly errors = signal<string[]>([]);

  constructor() {
    const service = inject(SettingsService);
    void attempt(this.busy, this.errors, async () => this.settings.set(await service.list()));
  }

  protected replace(updated: Setting): void {
    this.settings.update((list) => list.map((setting) => (setting.key === updated.key ? updated : setting)));
  }
}
