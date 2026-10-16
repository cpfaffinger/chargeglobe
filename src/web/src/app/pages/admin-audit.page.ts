import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { AdminService, AuditEntry, AuditFilter } from '../admin/admin.service';
import { attempt } from '../auth/problem';
import { AppIcon } from '../design/icon';
import { AppLoading } from '../design/loading';

const NO_FILTER: AuditFilter = { actor: '', action: '', entity: '', from: '', to: '' };

/** Labels of the actions recorded so far; unknown ones are shown as recorded. */
export const ACTION_LABELS: Record<string, string> = {
  'apikey.create': 'API-Key angelegt',
  'apikey.revoke': 'API-Key widerrufen',
  'auth.login': 'Anmeldung',
  'auth.register': 'Registrierung',
  'settings.update': 'Einstellung geändert',
  'user.roles': 'Rollen zugewiesen',
  'user.approve': 'Konto freigeschaltet',
  'user.disable': 'Konto gesperrt',
  'user.enable': 'Konto entsperrt',
};

/** Start of the day of an input of type date (local time) as ISO time; the day after for the end of a range. */
export function dayStart(value: string, next = false): string {
  const [year, month, day] = value.split('-').map(Number);
  return new Date(year, month - 1, day + (next ? 1 : 0)).toISOString();
}

/**
 * The audit log (audit:read): who changed what and when, newest first, filtered by actor, start of the action and of
 * the target, and a range of days; older entries page by page.
 */
@Component({
  imports: [AppIcon, AppLoading, DatePipe, MatButtonModule, MatFormFieldModule, MatInputModule],
  selector: 'app-admin-audit',
  template: `
    <h1>Audit-Log</h1>
    <p class="muted">
      Alle schreibenden Aktionen mit Zeit, Akteur und Ziel, neueste zuerst: Anmeldungen, Konten, Rollen, API-Keys, Einstellungen,
      Tarife, Konflikte und Favoriten. Werte von Schlüsseln oder Passwörtern stehen nie im Log.
    </p>
    <form class="audit-filter" (submit)="search($event)">
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Akteur (E-Mail oder Id)</mat-label>
        <input matInput id="audit-actor" [value]="filter().actor" (input)="set('actor', $event)" />
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Aktion beginnt mit</mat-label>
        <input matInput id="audit-action" placeholder="apikey." [value]="filter().action" (input)="set('action', $event)" />
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>Ziel beginnt mit</mat-label>
        <input matInput id="audit-entity" placeholder="api_key:" [value]="filter().entity" (input)="set('entity', $event)" />
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>von</mat-label>
        <input matInput type="date" id="audit-from" [value]="filter().from" (input)="set('from', $event)" />
      </mat-form-field>
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>bis</mat-label>
        <input matInput type="date" id="audit-to" [value]="filter().to" (input)="set('to', $event)" />
      </mat-form-field>
      <button matButton="filled" type="submit" id="audit-search" [disabled]="loading()"><app-icon name="search" />Suchen</button>
      <button matButton type="button" id="audit-reset" [disabled]="loading()" (click)="reset()">Zurücksetzen</button>
    </form>
    @for (error of errors(); track error) {
      <p class="error" role="alert">{{ error }}</p>
    }
    <div class="table-box">
      <table class="data-table" id="audit">
        <thead>
          <tr><th scope="col">Zeit</th><th scope="col">Aktion</th><th scope="col">Ziel</th><th scope="col">Akteur</th></tr>
        </thead>
        <tbody>
          @for (entry of entries(); track entry.id) {
            <tr>
              <td>{{ entry.occurredAt | date: 'dd.MM.yyyy HH:mm:ss' }}</td>
              <td class="action" [title]="entry.action">{{ action(entry) }}</td>
              <td class="target"><code>{{ entry.target }}</code></td>
              <td class="actor">{{ actor(entry) }}</td>
            </tr>
          } @empty {
            @if (!loading()) {
              <tr><td colspan="4" id="audit-empty">Keine Einträge für diese Filter.</td></tr>
            }
          }
        </tbody>
      </table>
    </div>
    @if (loading()) {
      <app-loading id="audit-loading" text="Einträge werden geladen …" />
    } @else if (cursor()) {
      <button matButton type="button" id="audit-more" (click)="more()">Ältere laden</button>
    }
  `,
})
export class AdminAuditPage {
  private readonly admin = inject(AdminService);

  protected readonly filter = signal<AuditFilter>(NO_FILTER);
  protected readonly entries = signal<AuditEntry[]>([]);
  protected readonly cursor = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly errors = signal<string[]>([]);

  constructor() {
    void this.load(false);
  }

  protected set(field: keyof AuditFilter, event: Event): void {
    const value = (event.target as HTMLInputElement).value.trim();
    this.filter.update((current) => ({ ...current, [field]: value }));
  }

  protected search(event: Event): Promise<void> {
    event.preventDefault();
    return this.load(false);
  }

  protected reset(): Promise<void> {
    this.filter.set(NO_FILTER);
    return this.load(false);
  }

  protected more(): Promise<void> {
    return this.load(true);
  }

  protected action(entry: AuditEntry): string {
    return ACTION_LABELS[entry.action] ?? entry.action;
  }

  protected actor(entry: AuditEntry): string {
    const who = entry.actor.email ?? entry.actor.userId ?? 'System';
    return entry.actor.apiKeyId ? `${who} über API-Key „${entry.actor.apiKeyName ?? entry.actor.apiKeyId}“` : who;
  }

  /** The first page for the filter, or with <code>more</code> the next older one appended. */
  private load(more: boolean): Promise<void> {
    const current = this.filter();
    const filter: AuditFilter = {
      ...current,
      from: current.from ? dayStart(current.from) : '',
      to: current.to ? dayStart(current.to, true) : '',
    };
    return attempt(this.loading, this.errors, async () => {
      const page = await this.admin.auditLog(filter, more ? this.cursor() : null);
      this.entries.update((entries) => (more ? [...entries, ...page.data] : page.data));
      this.cursor.set(page.meta.nextCursor);
    });
  }
}
