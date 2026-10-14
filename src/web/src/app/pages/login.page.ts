import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { attempt, describeError, problemCode } from '../auth/problem';

/** German texts for the `code` of a refused login (API: login). */
export const LOGIN_PROBLEMS: Record<string, string> = {
  invalid_credentials: 'E-Mail-Adresse oder Passwort ist falsch.',
  account_not_confirmed:
    'Dein Konto ist noch nicht bestätigt. Öffne den Link aus der Bestätigungsmail und melde dich dann an.',
  account_pending_approval:
    'Dein Konto wartet auf die Freischaltung durch eine Administratorin oder einen Administrator. Du bekommst eine E-Mail, sobald es freigeschaltet ist.',
  account_disabled: 'Dein Konto ist gesperrt. Wende dich an die Betreiber von chargeglobe.',
};

@Component({
  imports: [MatButtonModule, MatCardModule, MatFormFieldModule, MatInputModule, ReactiveFormsModule, RouterLink],
  selector: 'app-login',
  host: { class: 'form-page' },
  template: `
    <mat-card appearance="outlined">
      <mat-card-content>
        <h1>Anmelden</h1>
        <p class="muted">Mit Konto siehst du Statusverlauf und Auslastung jedes Ladepunkts.</p>
        @for (error of errors(); track error) {
          <p class="error" role="alert">{{ error }}</p>
        }
        @if (code() === 'account_not_confirmed') {
          <p class="resend">
            <button matButton="tonal" type="button" id="resend" [disabled]="busy()" (click)="resend()">Bestätigungsmail erneut senden</button>
          </p>
        }
        @if (sent(); as message) {
          <p id="resent" role="status">{{ message }}</p>
        }
        <form [formGroup]="form" (ngSubmit)="submit()">
          <mat-form-field>
            <mat-label>E-Mail</mat-label>
            <input matInput type="email" formControlName="email" autocomplete="username" required />
            <mat-error>Bitte eine E-Mail-Adresse eingeben.</mat-error>
          </mat-form-field>
          <mat-form-field>
            <mat-label>Passwort</mat-label>
            <input matInput type="password" formControlName="password" autocomplete="current-password" required />
            <mat-error>Bitte das Passwort eingeben.</mat-error>
          </mat-form-field>
          <button matButton="filled" type="submit" [disabled]="busy()">Anmelden</button>
        </form>
        <p class="form-links">
          <a matButton routerLink="/register">Konto anlegen</a>
          <a matButton routerLink="/forgot-password">Passwort vergessen?</a>
        </p>
      </mat-card-content>
    </mat-card>
  `,
})
export class LoginPage {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', Validators.required],
  });
  protected readonly busy = signal(false);
  protected readonly errors = signal<string[]>([]);
  /** Code of the last refused login, to offer the matching action. */
  protected readonly code = signal<string | null>(null);
  protected readonly sent = signal<string | null>(null);

  protected async submit(): Promise<void> {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.busy.set(true);
    this.errors.set([]);
    this.code.set(null);
    this.sent.set(null);
    try {
      const { email, password } = this.form.getRawValue();
      await this.auth.login(email, password);
      await this.router.navigateByUrl(this.route.snapshot.queryParamMap.get('returnUrl') ?? '/');
    } catch (error) {
      const code = problemCode(error);
      this.code.set(code);
      this.errors.set(code && LOGIN_PROBLEMS[code] ? [LOGIN_PROBLEMS[code]] : describeError(error));
    } finally {
      this.busy.set(false);
    }
  }

  protected resend(): Promise<void> {
    return attempt(this.busy, this.errors, async () => {
      await this.auth.resendConfirmation(this.form.getRawValue().email, 'de');
      this.code.set(null);
      this.sent.set('Wir haben dir eine neue Bestätigungsmail geschickt. Bitte prüfe auch den Spam-Ordner.');
    });
  }
}
