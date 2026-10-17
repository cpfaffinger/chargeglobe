import { DatePipe } from '@angular/common';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorIntl, MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { ActivatedRoute } from '@angular/router';
import { AccountState, AdminService, ApiKeyInfo, PermissionDefinition, RoleSummary, UserPage, UserSummary, USERS_PAGE_SIZE } from '../admin/admin.service';
import { AuthService } from '../auth/auth.service';
import { attempt } from '../auth/problem';
import { AppIcon } from '../design/icon';
import { AppLoading } from '../design/loading';
import { GermanPaginatorIntl } from '../design/paginator-intl';

export const STATE_LABELS: Record<AccountState, string> = {
  ACTIVE: 'aktiv',
  PENDING_CONFIRMATION: 'E-Mail unbestätigt',
  PENDING_APPROVAL: 'wartet auf Freischaltung',
  DISABLED: 'gesperrt',
};

/** Waiting time after the last key stroke before the search runs. */
export const SEARCH_DEBOUNCE_MS = 300;

/**
 * User administration (user:read to see, user:manage to change): search by e-mail address, state, roles; approve,
 * disable or enable, assign roles, send the confirmation mail again.
 */
@Component({
  imports: [AppLoading, AppIcon, DatePipe, MatButtonModule, MatFormFieldModule, MatInputModule, MatPaginatorModule, MatSelectModule, MatTableModule],
  selector: 'app-admin-users',
  providers: [{ provide: MatPaginatorIntl, useClass: GermanPaginatorIntl }],
  template: `
    <h1>Nutzer</h1>
    <div class="users-filter">
      <mat-form-field subscriptSizing="dynamic">
        <mat-label>E-Mail-Adresse suchen</mat-label>
        <input matInput type="search" id="user-search" [value]="search()" (input)="type($event)" />
      </mat-form-field>
      <button matButton type="button" id="reload" [disabled]="loading()" (click)="load()"><app-icon name="refresh" />Aktualisieren</button>
    </div>
    @for (error of errors(); track error) {
      <p class="error" role="alert">{{ error }}</p>
    }
    @if (loading()) {
      <app-loading id="users-loading" text="Konten werden geladen …" />
    }
    @if (users(); as current) {
      <div class="table-box">
        <table mat-table [dataSource]="current.data" id="users">
          <ng-container matColumnDef="email">
            <th mat-header-cell *matHeaderCellDef>E-Mail</th>
            <td mat-cell *matCellDef="let user" class="email">{{ user.email }}</td>
          </ng-container>
          <ng-container matColumnDef="state">
            <th mat-header-cell *matHeaderCellDef>Status</th>
            <td mat-cell *matCellDef="let user"><span [class]="'account-state ' + user.state.toLowerCase()">{{ label(user.state) }}</span></td>
          </ng-container>
          <ng-container matColumnDef="roles">
            <th mat-header-cell *matHeaderCellDef>Rollen</th>
            <td mat-cell *matCellDef="let user" class="roles">
              @if (canManage) {
                <mat-form-field subscriptSizing="dynamic" class="role-select">
                  <mat-select multiple [value]="user.roles" [attr.aria-label]="'Rollen von ' + user.email" (selectionChange)="setRoles(user, $event.value)">
                    @for (role of roles(); track role.name) {
                      <mat-option [value]="role.name">{{ role.name }}</mat-option>
                    }
                  </mat-select>
                </mat-form-field>
              } @else {
                {{ user.roles.join(', ') }}
              }
            </td>
          </ng-container>
          <ng-container matColumnDef="extra">
            <th mat-header-cell *matHeaderCellDef>Zusatzrechte</th>
            <td mat-cell *matCellDef="let user" class="extra">
              @if (canManage) {
                <mat-form-field subscriptSizing="dynamic" class="role-select">
                  <mat-select
                    multiple
                    [value]="user.extraPermissions"
                    placeholder="keine"
                    [attr.aria-label]="'Zusatzrechte von ' + user.email"
                    (selectionChange)="setPermissions(user, $event.value)"
                  >
                    @for (permission of permissions(); track permission.key) {
                      <mat-option [value]="permission.key" [title]="permission.description">{{ permission.key }}</mat-option>
                    }
                  </mat-select>
                </mat-form-field>
              } @else {
                {{ user.extraPermissions.join(', ') || '–' }}
              }
            </td>
          </ng-container>
          <ng-container matColumnDef="createdAt">
            <th mat-header-cell *matHeaderCellDef>Angelegt</th>
            <td mat-cell *matCellDef="let user">{{ user.createdAt | date: 'dd.MM.yyyy' }}</td>
          </ng-container>
          <ng-container matColumnDef="actions">
            <th mat-header-cell *matHeaderCellDef>Aktionen</th>
            <td mat-cell *matCellDef="let user" class="actions">
              <button matButton type="button" class="show-keys" (click)="showKeys(user)">API-Keys</button>
              @if (canManage) {
                @if (user.state === 'PENDING_APPROVAL') {
                  <button matButton="tonal" type="button" class="approve" (click)="approve(user)">Freischalten</button>
                }
                @if (user.state === 'PENDING_CONFIRMATION') {
                  <button matButton type="button" class="confirm-mail" (click)="sendConfirmation(user)">Bestätigungsmail</button>
                }
                @if (user.state === 'DISABLED') {
                  <button matButton type="button" class="enable" (click)="setEnabled(user, true)">Entsperren</button>
                } @else if (user.id !== me) {
                  <button matButton type="button" class="disable" (click)="setEnabled(user, false)">Sperren</button>
                }
                @if (user.id !== me) {
                  <button matButton type="button" class="delete" (click)="remove(user)">Löschen</button>
                }
              }
            </td>
          </ng-container>
          <tr mat-header-row *matHeaderRowDef="columns"></tr>
          <tr mat-row *matRowDef="let row; columns: columns"></tr>
          <tr class="mat-mdc-row no-data" *matNoDataRow>
            <td class="mat-mdc-cell" [attr.colspan]="columns.length" id="users-empty">Keine Konten gefunden.</td>
          </tr>
        </table>
      </div>
      @if (keysOf(); as owner) {
        <section id="user-keys">
          <h2>API-Keys von {{ owner.email }}</h2>
          <div class="table-box">
            <table class="data-table">
              <thead>
                <tr>
                  <th scope="col">Name</th><th scope="col">Präfix</th><th scope="col">Berechtigungen</th><th scope="col">erstellt</th>
                  <th scope="col">läuft ab</th><th scope="col">zuletzt benutzt</th><th scope="col">Status</th>
                </tr>
              </thead>
              <tbody>
                @for (key of keys(); track key.id) {
                  <tr [class.revoked]="key.revokedAt" [attr.data-key]="key.id">
                    <td>{{ key.name }}</td>
                    <td><code>{{ key.prefix }}</code></td>
                    <td>{{ key.permissions.join(', ') }}</td>
                    <td>{{ key.createdAt | date: 'dd.MM.yyyy HH:mm' }}</td>
                    <td>{{ key.expiresAt ? (key.expiresAt | date: 'dd.MM.yyyy HH:mm') : 'nie' }}</td>
                    <td>{{ key.lastUsedAt ? (key.lastUsedAt | date: 'dd.MM.yyyy HH:mm') : '–' }}</td>
                    <td>
                      @if (key.revokedAt) {
                        widerrufen {{ key.revokedAt | date: 'dd.MM.yyyy HH:mm' }}
                      } @else if (canManage) {
                        <button matButton type="button" class="revoke" (click)="revoke(owner, key)">Widerrufen</button>
                      } @else {
                        gültig
                      }
                    </td>
                  </tr>
                } @empty {
                  <tr><td colspan="7" id="keys-empty">Dieses Konto hat keine API-Keys.</td></tr>
                }
              </tbody>
            </table>
          </div>
        </section>
      }
      <mat-paginator
        [length]="current.meta.total"
        [pageIndex]="page() - 1"
        [pageSize]="pageSize"
        [hidePageSize]="true"
        [disabled]="loading()"
        aria-label="Seiten der Nutzerliste"
        (page)="go($event)"
      />
    }
  `,
})
export class AdminUsersPage {
  private readonly admin = inject(AdminService);
  private readonly auth = inject(AuthService);
  private readonly snackBar = inject(MatSnackBar);
  private timer: ReturnType<typeof setTimeout> | undefined;

  protected readonly columns = ['email', 'state', 'roles', 'extra', 'createdAt', 'actions'];
  protected readonly pageSize = USERS_PAGE_SIZE;
  protected readonly canManage = this.auth.hasPermission('user:manage');
  protected readonly me = this.auth.me()?.id;
  protected readonly search = signal(inject(ActivatedRoute).snapshot.queryParamMap.get('q') ?? '');
  protected readonly page = signal(1);
  protected readonly users = signal<UserPage | null>(null);
  protected readonly roles = signal<RoleSummary[]>([]);
  protected readonly permissions = signal<PermissionDefinition[]>([]);
  protected readonly keysOf = signal<UserSummary | null>(null);
  protected readonly keys = signal<ApiKeyInfo[]>([]);
  protected readonly loading = signal(false);
  protected readonly errors = signal<string[]>([]);

  constructor() {
    void this.load();
    if (this.canManage) {
      void attempt(signal(false), this.errors, async () => this.roles.set(await this.admin.roles()));
      void attempt(signal(false), this.errors, async () => this.permissions.set(await this.admin.permissions()));
    }
    inject(DestroyRef).onDestroy(() => clearTimeout(this.timer));
  }

  protected label(state: AccountState): string {
    return STATE_LABELS[state];
  }

  /** Searches after a short pause in typing, from the first page. */
  protected type(event: Event): void {
    this.search.set((event.target as HTMLInputElement).value.trim());
    clearTimeout(this.timer);
    this.timer = setTimeout(() => {
      this.page.set(1);
      void this.load();
    }, SEARCH_DEBOUNCE_MS);
  }

  protected go(event: PageEvent): Promise<void> {
    this.page.set(event.pageIndex + 1);
    return this.load();
  }

  protected load(): Promise<void> {
    return attempt(this.loading, this.errors, async () => {
      this.users.set(await this.admin.users(this.search(), this.page()));
    });
  }

  protected approve(user: UserSummary): Promise<void> {
    return this.change(() => this.admin.setApproved(user.id, true), `${user.email} ist freigeschaltet.`);
  }

  protected setEnabled(user: UserSummary, enabled: boolean): Promise<void> {
    return this.change(() => this.admin.setEnabled(user.id, enabled), `${user.email} ist ${enabled ? 'entsperrt' : 'gesperrt'}.`);
  }

  protected setRoles(user: UserSummary, roles: string[]): Promise<void> {
    return this.change(() => this.admin.setRoles(user.id, roles), `Rollen von ${user.email} gespeichert.`);
  }

  /** Lists the API keys of the account below the table (without key values). */
  protected showKeys(user: UserSummary): Promise<void> {
    return attempt(this.loading, this.errors, async () => {
      this.keys.set(await this.admin.apiKeys(user.id));
      this.keysOf.set(user);
    });
  }

  protected revoke(user: UserSummary, key: ApiKeyInfo): Promise<void> {
    return attempt(this.loading, this.errors, async () => {
      await this.admin.revokeApiKey(user.id, key.id);
      this.keys.set(await this.admin.apiKeys(user.id));
      this.snackBar.open(`API-Key „${key.name}“ von ${user.email} ist widerrufen.`, 'OK', { duration: 4000 });
    });
  }

  protected setPermissions(user: UserSummary, permissions: string[]): Promise<void> {
    return this.change(() => this.admin.setPermissions(user.id, permissions), `Zusatzrechte von ${user.email} gespeichert.`);
  }

  /** Deletes the account after a confirmation and reloads the page of the list. */
  protected async remove(user: UserSummary): Promise<void> {
    if (!window.confirm(`${user.email} endgültig löschen? Favoriten, Einstellungen und API-Keys des Kontos gehen verloren.`)) {
      return;
    }

    await attempt(this.loading, this.errors, async () => {
      await this.admin.deleteUser(user.id);
      this.snackBar.open(`${user.email} ist gelöscht.`, 'OK', { duration: 4000 });
      if (this.keysOf()?.id === user.id) {
        this.keysOf.set(null);
      }
    });
    await this.load();
  }

  protected sendConfirmation(user: UserSummary): Promise<void> {
    return attempt(this.loading, this.errors, async () => {
      await this.admin.sendConfirmation(user.id);
      this.snackBar.open(`Bestätigungsmail an ${user.email} ist unterwegs.`, 'OK', { duration: 4000 });
    });
  }

  /** Runs a change, replaces the account in the list and confirms it; a refused change reloads the list. */
  private async change(action: () => Promise<UserSummary>, message: string): Promise<void> {
    await attempt(this.loading, this.errors, async () => {
      const changed = await action();
      this.users.update((current) => ({ ...current!, data: current!.data.map((user) => (user.id === changed.id ? changed : user)) }));
      this.snackBar.open(message, 'OK', { duration: 4000 });
    });
    const errors = this.errors();
    if (errors.length > 0) {
      // The list shows the stored state again (e.g. the roles before a refused change).
      await this.load();
      this.errors.set(errors);
    }
  }
}
