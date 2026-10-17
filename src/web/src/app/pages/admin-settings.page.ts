import { Component, computed, inject, signal } from '@angular/core';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { attempt } from '../auth/problem';
import { AppIcon } from '../design/icon';
import { SettingRow } from '../settings/setting-row';
import { Setting, SettingsService } from '../settings/settings.service';

/** Panels of the page by the first part of the key; unknown prefixes are listed under "Weitere". */
const GROUPS: { prefix: string; title: string; summary: string }[] = [
  { prefix: 'provider', title: 'Datenquellen', summary: 'Zugang, Regionen und Abfrageintervalle je Quelle' },
  { prefix: 'mail', title: 'E-Mail (SMTP)', summary: 'Versand von Bestätigungs- und Passwort-Mails' },
  { prefix: 'map', title: 'Karte', summary: 'Kartenstil hell und dunkel, Start-Ausschnitt, Status-Alter' },
  { prefix: 'auth', title: 'Anmeldung', summary: 'Rate-Limit der Anmeldung' },
  { prefix: 'ingest', title: 'Ingest', summary: 'Wiederholungen und Aufbewahrung' },
  { prefix: 'price', title: 'Preise', summary: 'Beispielrechnung: Energie und Ladeleistung des Beispielautos' },
];

interface Row {
  setting: Setting;
  scope: string;
}

/**
 * Admin page of the central config service, rendered generically from GET /settings: one expansion panel per group
 * of keys, a search over key, scope and description. Readable with settings:read (route guard); editing needs
 * settings:manage.
 */
@Component({
  imports: [AppIcon, MatExpansionModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, RouterLink, SettingRow],
  selector: 'app-admin-settings',
  template: `
    <h1>Einstellungen</h1>
    <p class="muted">
      Änderungen wirken ohne Neustart in allen Containern. Einstellungen mit dem Zusatz
      <code>provider:…</code> gelten für die genannte Datenquelle.
    </p>
    @if (auth.hasPermission('settings:manage')) {
      <p id="mail-link">
        SMTP-Zugang prüfen: <a routerLink="/admin/mail">Testmail senden und Postausgang ansehen</a>
      </p>
    }
    <mat-form-field id="settings-search" subscriptSizing="dynamic">
      <app-icon matPrefix name="search" />
      <mat-label>Einstellung suchen</mat-label>
      <input matInput type="search" autocomplete="off" [value]="query()" (input)="search($event)" />
    </mat-form-field>
    @if (busy()) {
      <mat-progress-bar mode="indeterminate" aria-label="Einstellungen werden geladen" />
    }
    @for (error of errors(); track error) {
      <p class="error" role="alert">{{ error }}</p>
    }
    <mat-accordion multi>
      @for (group of groups(); track group.title) {
        <mat-expansion-panel [expanded]="group.expanded" class="group">
          <mat-expansion-panel-header>
            <mat-panel-title>{{ group.title }}</mat-panel-title>
            <mat-panel-description>{{ group.summary }}</mat-panel-description>
          </mat-expansion-panel-header>
          @for (row of group.rows; track row.setting.key + row.scope) {
            <app-setting-row
              [setting]="row.setting"
              [scope]="row.scope"
              [canManage]="auth.hasPermission('settings:manage')"
              (changed)="replace($event)"
            />
          }
        </mat-expansion-panel>
      }
    </mat-accordion>
    @if (settings().length > 0 && groups().length === 0) {
      <p id="no-match">Keine Einstellung passt zu „{{ query() }}“.</p>
    }
  `,
})
export class AdminSettingsPage {
  protected readonly auth = inject(AuthService);

  protected readonly settings = signal<Setting[]>([]);
  protected readonly query = signal('');
  protected readonly busy = signal(false);
  protected readonly errors = signal<string[]>([]);

  /** Rows per group in catalog order; while searching only matches, with their panels open. */
  protected readonly groups = computed(() => {
    const term = this.query().trim().toLowerCase();
    const rows: Row[] = this.settings()
      .flatMap((setting) => setting.scopes.map((scope) => ({ setting, scope })))
      .filter(({ setting, scope }) => !term || `${setting.key} ${scope} ${setting.description}`.toLowerCase().includes(term));
    const known = GROUPS.map((group) => group.prefix);
    const prefixOf = (row: Row) => row.setting.key.split('.')[0];
    return [...GROUPS, { prefix: '', title: 'Weitere', summary: 'Ohne eigene Gruppe' }]
      .map((group) => ({
        ...group,
        expanded: term !== '' || group.prefix === 'provider',
        rows: rows.filter((row) => (group.prefix ? prefixOf(row) === group.prefix : !known.includes(prefixOf(row)))),
      }))
      .filter((group) => group.rows.length > 0);
  });

  constructor() {
    const service = inject(SettingsService);
    void attempt(this.busy, this.errors, async () => this.settings.set(await service.list()));
  }

  protected search(event: Event): void {
    this.query.set((event.target as HTMLInputElement).value);
  }

  protected replace(updated: Setting): void {
    this.settings.update((list) => list.map((setting) => (setting.key === updated.key ? updated : setting)));
  }
}
