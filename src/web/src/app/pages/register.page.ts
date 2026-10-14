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
  selector: 'app-register',
  host: { class: 'form-page' },
  template: `
    <mat-card appearance="outlined">
      <mat-card-content>
        <h1>Konto anlegen</h1>
        @if (done()) {
          <p id="register-done" role="status">
            Falls die Adresse noch nicht registriert war, wurde ein Konto angelegt. Du erhältst eine E-Mail
            mit einem Link, um die Adresse zu bestätigen.
          </p>
          <p><a matButton routerLink="/login">Zur Anmeldung</a></p>
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
            <mat-form-field>
              <mat-label>Passwort</mat-label>
              <input matInput type="password" formControlName="password" autocomplete="new-password" required />
              <mat-hint>Mindestens 10 Zeichen.</mat-hint>
              <mat-error>Mindestens 10 Zeichen.</mat-error>
            </mat-form-field>
            <button matButton="filled" type="submit" [disabled]="busy()">Registrieren</button>
          </form>
          <p class="form-links"><a matButton routerLink="/login">Ich habe schon ein Konto</a></p>
        }
      </mat-card-content>
    </mat-card>
  `,
})
export class RegisterPage {
  private readonly auth = inject(AuthService);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required, Validators.minLength(10)]],
  });
  protected readonly busy = signal(false);
  protected readonly errors = signal<string[]>([]);
  protected readonly done = signal(false);

  protected submit(): Promise<void> {
    return attemptValid(this.form, this.busy, this.errors, async () => {
      const { email, password } = this.form.getRawValue();
      await this.auth.register(email, password, 'de');
      this.done.set(true);
    });
  }
}
