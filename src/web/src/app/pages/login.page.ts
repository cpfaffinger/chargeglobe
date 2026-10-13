import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { attempt } from '../auth/problem';

@Component({
  imports: [ReactiveFormsModule, RouterLink],
  selector: 'app-login',
  template: `
    <h2>Anmelden</h2>
    <form [formGroup]="form" (ngSubmit)="submit()">
      <label>E-Mail <input type="email" formControlName="email" autocomplete="username" required /></label>
      <label>
        Passwort
        <input type="password" formControlName="password" autocomplete="current-password" required />
      </label>
      <button type="submit" [disabled]="busy()">Anmelden</button>
    </form>
    @for (error of errors(); track error) {
      <p class="error" role="alert">{{ error }}</p>
    }
    <p>
      <a routerLink="/register">Konto anlegen</a> ·
      <a routerLink="/forgot-password">Passwort vergessen?</a>
    </p>
  `,
})
export class LoginPage {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly form = inject(FormBuilder).nonNullable.group({ email: '', password: '' });
  protected readonly busy = signal(false);
  protected readonly errors = signal<string[]>([]);

  protected submit(): Promise<void> {
    return attempt(this.busy, this.errors, async () => {
      const { email, password } = this.form.getRawValue();
      await this.auth.login(email, password);
      await this.router.navigateByUrl(this.route.snapshot.queryParamMap.get('returnUrl') ?? '/');
    });
  }
}
