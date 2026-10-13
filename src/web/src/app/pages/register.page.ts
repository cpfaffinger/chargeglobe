import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { attempt } from '../auth/problem';

@Component({
  imports: [ReactiveFormsModule, RouterLink],
  selector: 'app-register',
  template: `
    <h2>Konto anlegen</h2>
    @if (done()) {
      <p id="register-done" role="status">
        Falls die Adresse noch nicht registriert war, wurde ein Konto angelegt. Du erhältst eine E-Mail
        mit einem Link, um die Adresse zu bestätigen.
      </p>
      <p><a routerLink="/login">Zur Anmeldung</a></p>
    } @else {
      <form [formGroup]="form" (ngSubmit)="submit()">
        <label>E-Mail <input type="email" formControlName="email" autocomplete="username" required /></label>
        <label>
          Passwort (mindestens 10 Zeichen)
          <input type="password" formControlName="password" autocomplete="new-password" required />
        </label>
        <button type="submit" [disabled]="busy()">Registrieren</button>
      </form>
      @for (error of errors(); track error) {
        <p class="error" role="alert">{{ error }}</p>
      }
      <p><a routerLink="/login">Ich habe schon ein Konto</a></p>
    }
  `,
})
export class RegisterPage {
  private readonly auth = inject(AuthService);

  protected readonly form = inject(FormBuilder).nonNullable.group({ email: '', password: '' });
  protected readonly busy = signal(false);
  protected readonly errors = signal<string[]>([]);
  protected readonly done = signal(false);

  protected submit(): Promise<void> {
    return attempt(this.busy, this.errors, async () => {
      const { email, password } = this.form.getRawValue();
      await this.auth.register(email, password, 'de');
      this.done.set(true);
    });
  }
}
