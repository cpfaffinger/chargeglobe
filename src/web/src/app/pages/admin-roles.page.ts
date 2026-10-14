import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar } from '@angular/material/snack-bar';
import { AdminService, PermissionDefinition, RoleSummary } from '../admin/admin.service';
import { AuthService } from '../auth/auth.service';
import { attempt, attemptValid } from '../auth/problem';

/**
 * Roles as bundles of permissions (user:read to see, role:manage to change): a matrix permissions × roles with a
 * save button per role, a form for a new role and deletion of roles without accounts. Admin always holds every
 * permission; the default roles cannot be deleted.
 */
@Component({
  imports: [MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, ReactiveFormsModule],
  selector: 'app-admin-roles',
  template: `
    <h1>Rollen</h1>
    <p class="muted">
      Eine Rolle bündelt Berechtigungen; geprüft werden immer nur die Berechtigungen. Admin hat stets alle; die Standardrollen
      werden beim Start angelegt und lassen sich nicht löschen.
    </p>
    @for (error of errors(); track error) {
      <p class="error" role="alert">{{ error }}</p>
    }
    @if (canManage) {
      <form [formGroup]="form" (ngSubmit)="create()" id="new-role">
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Neue Rolle</mat-label>
          <input matInput formControlName="name" maxlength="50" required />
          <mat-error>Bitte einen Namen mit 1 bis 50 Zeichen eingeben.</mat-error>
        </mat-form-field>
        <button matButton="tonal" type="submit" [disabled]="busy()">Anlegen</button>
      </form>
    }
    <div class="table-box">
      <table class="role-matrix" id="roles">
        <thead>
          <tr>
            <th scope="col">Berechtigung</th>
            @for (role of roles(); track role.name) {
              <th scope="col" [id]="'role-' + role.name">
                <span class="role-name">{{ role.name }}</span>
                <span class="muted">{{ role.users }} {{ role.users === 1 ? 'Konto' : 'Konten' }}</span>
                @if (canManage && !role.fullCatalog) {
                  <button matButton="tonal" type="button" class="save" [disabled]="busy() || !changed(role)" (click)="save(role)">Speichern</button>
                  @if (!role.default && role.users === 0) {
                    <button matButton type="button" class="delete" [disabled]="busy()" (click)="remove(role)">Löschen</button>
                  }
                }
              </th>
            }
          </tr>
        </thead>
        <tbody>
          @for (permission of permissions(); track permission.key) {
            <tr>
              <th scope="row"><code>{{ permission.key }}</code><span class="muted">{{ permission.description }}</span></th>
              @for (role of roles(); track role.name) {
                <td>
                  <mat-checkbox
                    [checked]="has(role, permission.key)"
                    [disabled]="!canManage || role.fullCatalog || busy()"
                    [attr.data-role]="role.name"
                    [attr.data-permission]="permission.key"
                    [aria-label]="permission.key + ' für ' + role.name"
                    (change)="toggle(role, permission.key, $event.checked)"
                  />
                </td>
              }
            </tr>
          }
        </tbody>
      </table>
    </div>
  `,
})
export class AdminRolesPage {
  private readonly admin = inject(AdminService);
  private readonly snackBar = inject(MatSnackBar);

  protected readonly canManage = inject(AuthService).hasPermission('role:manage');
  protected readonly form = inject(FormBuilder).nonNullable.group({ name: ['', [Validators.required, Validators.maxLength(50)]] });
  protected readonly roles = signal<RoleSummary[]>([]);
  protected readonly permissions = signal<PermissionDefinition[]>([]);
  /** Edited permissions per role, until saved. */
  protected readonly drafts = signal<Record<string, string[]>>({});
  protected readonly busy = signal(false);
  protected readonly errors = signal<string[]>([]);

  constructor() {
    void this.load();
  }

  protected has(role: RoleSummary, permission: string): boolean {
    return (this.drafts()[role.name] ?? role.permissions).includes(permission);
  }

  protected changed(role: RoleSummary): boolean {
    const draft = this.drafts()[role.name];
    return draft !== undefined && [...draft].sort().join() !== [...role.permissions].sort().join();
  }

  protected toggle(role: RoleSummary, permission: string, checked: boolean): void {
    const current = this.drafts()[role.name] ?? role.permissions;
    const next = checked ? [...current, permission] : current.filter((key) => key !== permission);
    this.drafts.update((drafts) => ({ ...drafts, [role.name]: next }));
  }

  protected save(role: RoleSummary): Promise<void> {
    return attempt(this.busy, this.errors, async () => {
      await this.admin.putRole(role.name, this.drafts()[role.name]);
      this.snackBar.open(`Rolle ${role.name} gespeichert.`, 'OK', { duration: 4000 });
      // Unsaved changes of the other roles stay.
      this.drafts.update((drafts) => Object.fromEntries(Object.entries(drafts).filter(([name]) => name !== role.name)));
      await this.reload();
    });
  }

  protected create(): Promise<void> {
    return attemptValid(this.form, this.busy, this.errors, async () => {
      const name = this.form.getRawValue().name.trim();
      await this.admin.putRole(name, []);
      this.form.reset();
      this.snackBar.open(`Rolle ${name} angelegt.`, 'OK', { duration: 4000 });
      await this.reload();
    });
  }

  protected remove(role: RoleSummary): Promise<void> {
    return attempt(this.busy, this.errors, async () => {
      await this.admin.deleteRole(role.name);
      this.snackBar.open(`Rolle ${role.name} gelöscht.`, 'OK', { duration: 4000 });
      await this.reload();
    });
  }

  private load(): Promise<void> {
    return attempt(this.busy, this.errors, async () => {
      const [permissions] = await Promise.all([this.admin.permissions(), this.reload()]);
      this.permissions.set(permissions);
    });
  }

  private async reload(): Promise<void> {
    this.roles.set(await this.admin.roles());
  }
}
