import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { attempt } from '../auth/problem';

@Component({
  imports: [ReactiveFormsModule, RouterLink],
  selector: 'app-forgot-password',
  template: `
    <h2>Passwort vergessen</h2>
    @if (done()) {
      <p id="forgot-done" role="status">
        Falls zu der Adresse ein Konto gehört, erhältst du eine E-Mail mit einem Link zum Zurücksetzen.
      </p>
    } @else {
      <form [formGroup]="form" (ngSubmit)="submit()">
        <label>E-Mail <input type="email" formControlName="email" autocomplete="username" required /></label>
        <button type="submit" [disabled]="busy()">Link anfordern</button>
      </form>
      @for (error of errors(); track error) {
        <p class="error" role="alert">{{ error }}</p>
      }
    }
    <p><a routerLink="/login">Zur Anmeldung</a></p>
  `,
})
export class ForgotPasswordPage {
  private readonly auth = inject(AuthService);

  protected readonly form = inject(FormBuilder).nonNullable.group({ email: '' });
  protected readonly busy = signal(false);
  protected readonly errors = signal<string[]>([]);
  protected readonly done = signal(false);

  protected submit(): Promise<void> {
    return attempt(this.busy, this.errors, async () => {
      await this.auth.requestPasswordReset(this.form.getRawValue().email, 'de');
      this.done.set(true);
    });
  }
}
