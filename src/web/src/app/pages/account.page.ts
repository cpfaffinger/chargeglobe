import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { AuthService } from '../auth/auth.service';
import { describeError } from '../auth/problem';

interface HealthResponse {
  status: string;
  db: string;
}

/**
 * Account of the signed-in user (guarded in app.routes.ts): identity, "Passwort ändern" (the other sessions end) and the
 * state of API and database.
 */
@Component({
  imports: [MatButtonModule, MatCardModule, MatFormFieldModule, MatInputModule, ReactiveFormsModule],
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
    <mat-card appearance="outlined" id="password-change">
      <mat-card-content>
        <h2>Passwort ändern</h2>
        @for (error of errors(); track error) {
          <p class="error" role="alert">{{ error }}</p>
        }
        @if (changed()) {
          <p id="password-changed" role="status">Dein Passwort ist geändert. Auf anderen Geräten bist du abgemeldet.</p>
        }
        <form [formGroup]="form" (ngSubmit)="change()">
          <mat-form-field>
            <mat-label>Aktuelles Passwort</mat-label>
            <input matInput type="password" formControlName="currentPassword" autocomplete="current-password" required />
            <mat-error>Bitte das aktuelle Passwort eingeben.</mat-error>
          </mat-form-field>
          <mat-form-field>
            <mat-label>Neues Passwort</mat-label>
            <input matInput type="password" formControlName="newPassword" autocomplete="new-password" required />
            <mat-error>Mindestens 10 Zeichen.</mat-error>
          </mat-form-field>
          <mat-form-field>
            <mat-label>Neues Passwort wiederholen</mat-label>
            <input matInput type="password" formControlName="repeat" autocomplete="new-password" required />
            <mat-error>Bitte das neue Passwort wiederholen.</mat-error>
          </mat-form-field>
          <button matButton="filled" type="submit" [disabled]="busy()">Passwort ändern</button>
        </form>
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

  protected readonly form = inject(FormBuilder).nonNullable.group({
    currentPassword: ['', Validators.required],
    newPassword: ['', [Validators.required, Validators.minLength(10)]],
    repeat: ['', Validators.required],
  });
  protected readonly busy = signal(false);
  protected readonly errors = signal<string[]>([]);
  protected readonly changed = signal(false);
  protected readonly apiStatus = signal('…');
  protected readonly dbStatus = signal('…');

  protected async change(): Promise<void> {
    this.changed.set(false);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const { currentPassword, newPassword, repeat } = this.form.getRawValue();
    if (newPassword !== repeat) {
      this.errors.set(['Die beiden neuen Passwörter stimmen nicht überein.']);
      return;
    }

    this.busy.set(true);
    this.errors.set([]);
    try {
      await this.auth.changePassword(currentPassword, newPassword);
      this.form.reset();
      this.changed.set(true);
    } catch (error) {
      const wrongCurrent = error instanceof HttpErrorResponse && error.error?.errors?.currentPassword;
      this.errors.set(wrongCurrent ? ['Das aktuelle Passwort ist falsch.'] : describeError(error));
    } finally {
      this.busy.set(false);
    }
  }

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
