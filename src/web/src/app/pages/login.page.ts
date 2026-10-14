import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { attemptValid } from '../auth/problem';

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

  protected submit(): Promise<void> {
    return attemptValid(this.form, this.busy, this.errors, async () => {
      const { email, password } = this.form.getRawValue();
      await this.auth.login(email, password);
      await this.router.navigateByUrl(this.route.snapshot.queryParamMap.get('returnUrl') ?? '/');
    });
  }
}
