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

interface Row {
  setting: Setting;
  scope: string;
}

/** A sub-group of a panel: the rows it takes, in catalog order. */
interface Section {
  title: string;
  takes: (row: Row) => boolean;
}

interface Group {
  title: string;
  summary: string;
  sections: Section[];
}

/** Names of the source adapters of the provider settings ("provider:econtrol"). */
const PROVIDER_NAMES: Record<string, string> = { econtrol: 'E-Control', ocm: 'Open Charge Map' };

const keyIn = (...keys: string[]) => (row: Row) => keys.some((key) => row.setting.key === key || row.setting.key.startsWith(`${key}.`));
const globalKeyIn = (...keys: string[]) => (row: Row) => row.scope === 'global' && keyIn(...keys)(row);

/**
 * Panels and their sub-groups. A row goes to the first section that takes it; the settings of one source adapter
 * (scope provider:<key>) form a section of their own, created from the scopes that exist. Rows no section takes are
 * listed under "Weitere".
 */
function groupsFor(rows: Row[]): Group[] {
  const providerScopes = [...new Set(rows.map((row) => row.scope).filter((scope) => scope.startsWith('provider:')))].sort();
  return [
    {
      title: 'Datenquellen',
      summary: 'Zugang je Quelle, Intervalle, Budget und Pausen',
      sections: [
        ...providerScopes.map((scope) => {
          const key = scope.slice('provider:'.length);
          return { title: `${PROVIDER_NAMES[key] ?? key} (${scope})`, takes: (row: Row) => row.scope === scope };
        }),
        { title: 'Standard für alle Quellen', takes: globalKeyIn('provider.enabled', 'provider.api_key', 'provider.referer', 'provider.regions') },
        { title: 'Abfrageintervalle', takes: globalKeyIn('provider.interval', 'provider.tile_interval_hot_minutes', 'provider.tile_interval_normal_minutes', 'provider.tile_interval_quiet_minutes') },
        { title: 'Budget und Pausen', takes: globalKeyIn('provider.request_budget_per_hour', 'provider.cooldown_step_minutes', 'provider.cooldown_max_minutes') },
        { title: 'Weitere Quelleneinstellungen', takes: keyIn('provider') },
        { title: 'Abrufprotokoll', takes: keyIn('crawler') },
      ],
    },
    {
      title: 'Karte und Status',
      summary: 'Kartenstil, Start-Ausschnitt, Alter und Glättung des Status',
      sections: [
        { title: 'Kartenstil', takes: keyIn('map.style_url', 'map.style_url_dark', 'map.attribution') },
        { title: 'Start-Ausschnitt', takes: keyIn('map.start') },
        { title: 'Status', takes: keyIn('map.status_max_age_minutes', 'status') },
        { title: 'Weitere Karteneinstellungen', takes: keyIn('map') },
      ],
    },
    {
      title: 'Preise',
      summary: 'Beispielrechnung: Energie und Ladeleistung des Beispielautos',
      sections: [{ title: 'Beispielsitzung', takes: keyIn('price') }],
    },
    {
      title: 'Anmeldung und Konten',
      summary: 'Bestätigung, Freischaltung, Rate-Limit',
      sections: [
        { title: 'Neue Konten', takes: keyIn('auth.require_confirmed_email', 'auth.require_admin_approval') },
        { title: 'Schutz', takes: keyIn('auth') },
      ],
    },
    {
      title: 'E-Mail (SMTP)',
      summary: 'Versand von Bestätigungs- und Passwort-Mails',
      sections: [
        { title: 'Versand', takes: keyIn('mail.enabled', 'mail.max_attempts', 'mail.retention_days') },
        { title: 'SMTP-Server', takes: keyIn('mail.smtp') },
        { title: 'Absender', takes: keyIn('mail.from') },
        { title: 'Weitere Mail-Einstellungen', takes: keyIn('mail') },
      ],
    },
    {
      title: 'Daten und Aufbewahrung',
      summary: 'Ingest, Lebenszyklus, Ladeparks, Adressregister, Audit-Log',
      sections: [
        { title: 'Ingest', takes: keyIn('ingest') },
        { title: 'Lebenszyklus und Ladeparks', takes: keyIn('lifecycle', 'location') },
        { title: 'Adressregister', takes: keyIn('address') },
        { title: 'Audit-Log', takes: keyIn('audit') },
      ],
    },
  ];
}


/**
 * Admin page of the central config service, rendered generically from GET /settings: one expansion panel per group
 * of keys with sub-groups (one per source adapter), a search over key, scope and description. Readable with
 * settings:read (route guard); editing needs settings:manage. The search field stays read-only until it gets the focus:
 * browsers fill saved logins into a text field before a password field (the secret settings) even with
 * autocomplete="off", which would filter the page by the user's e-mail address.
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
      <input
        matInput
        type="search"
        name="settings-filter"
        autocomplete="off"
        data-form-type="other"
        [readOnly]="!searchArmed()"
        [value]="query()"
        (focus)="searchArmed.set(true)"
        (input)="search($event)"
      />
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
          @for (section of group.sections; track section.title) {
            <section class="settings-section">
              @if (group.sections.length > 1 || section.title !== group.title) {
                <h2>{{ section.title }}</h2>
              }
              @for (row of section.rows; track row.setting.key + row.scope) {
                <app-setting-row
                  [setting]="row.setting"
                  [scope]="row.scope"
                  [canManage]="auth.hasPermission('settings:manage')"
                  (changed)="replace($event)"
                />
              }
            </section>
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
  /** False until the search field gets the focus (no autofill into a read-only field). */
  protected readonly searchArmed = signal(false);

  /** Rows per group and section in catalog order; while searching only matches, with their panels open. */
  protected readonly groups = computed(() => {
    const term = this.query().trim().toLowerCase();
    const rows: Row[] = this.settings()
      .flatMap((setting) => setting.scopes.map((scope) => ({ setting, scope })))
      .filter(({ setting, scope }) => !term || `${setting.key} ${scope} ${setting.description}`.toLowerCase().includes(term));
    const groups = groupsFor(rows);
    const placed = new Set<Row>();
    const take = (section: Section) => {
      const taken = rows.filter((row) => !placed.has(row) && section.takes(row));
      taken.forEach((row) => placed.add(row));
      return { title: section.title, rows: taken };
    };
    const filled = groups.map((group, index) => ({
      title: group.title,
      summary: group.summary,
      expanded: term !== '' || index === 0,
      sections: group.sections.map(take).filter((section) => section.rows.length > 0),
    }));
    const rest = rows.filter((row) => !placed.has(row));
    return [
      ...filled,
      { title: 'Weitere', summary: 'Ohne eigene Gruppe', expanded: term !== '', sections: [{ title: 'Weitere', rows: rest }].filter((section) => section.rows.length > 0) },
    ].filter((group) => group.sections.length > 0);
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
