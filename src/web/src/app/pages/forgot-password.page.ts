import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { attemptValid } from '../auth/problem';

@Component({
  imports: [MatButtonModule, MatCardModule, MatFormFieldModule, MatInputModule, ReactiveFormsModule, RouterLink],
  selector: 'app-forgot-password',
  host: { class: 'form-page' },
  template: `
    <mat-card appearance="outlined">
      <mat-card-content>
        <h1>Passwort vergessen</h1>
        @if (done()) {
          <p id="forgot-done" role="status">
            Falls zu der Adresse ein Konto gehört, erhältst du eine E-Mail mit einem Link zum Zurücksetzen.
          </p>
        } @else {
          @for (error of errors(); track error) {
            <p class="error" role="alert">{{ error }}</p>
          }
          <form [formGroup]="form" (ngSubmit)="submit()">
            <mat-form-field>
              <mat-label>E-Mail</mat-label>
              <input matInput type="email" formControlName="email" autocomplete="username" required />
              <mat-error>Bitte eine gültige E-Mail-Adresse eingeben.</mat-error>
            </mat-form-field>
            <button matButton="filled" type="submit" [disabled]="busy()">Link anfordern</button>
          </form>
        }
        <p class="form-links"><a matButton routerLink="/login">Zur Anmeldung</a></p>
      </mat-card-content>
    </mat-card>
  `,
})
export class ForgotPasswordPage {
  private readonly auth = inject(AuthService);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
  });
  protected readonly busy = signal(false);
  protected readonly errors = signal<string[]>([]);
  protected readonly done = signal(false);

  protected submit(): Promise<void> {
    return attemptValid(this.form, this.busy, this.errors, async () => {
      await this.auth.requestPasswordReset(this.form.getRawValue().email, 'de');
      this.done.set(true);
    });
  }
}
