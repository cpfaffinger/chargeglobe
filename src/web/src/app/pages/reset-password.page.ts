import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { attempt } from '../auth/problem';

/** Target of the link in the reset mail: /reset-password?userId=…&token=… */
@Component({
  imports: [ReactiveFormsModule, RouterLink],
  selector: 'app-reset-password',
  template: `
    <h2>Neues Passwort vergeben</h2>
    @if (done()) {
      <p id="reset-done" role="status">Dein Passwort wurde geändert.</p>
      <p><a routerLink="/login">Zur Anmeldung</a></p>
    } @else {
      <form [formGroup]="form" (ngSubmit)="submit()">
        <label>
          Neues Passwort (mindestens 10 Zeichen)
          <input type="password" formControlName="newPassword" autocomplete="new-password" required />
        </label>
        <button type="submit" [disabled]="busy()">Passwort speichern</button>
      </form>
      @for (error of errors(); track error) {
        <p class="error" role="alert">{{ error }}</p>
      }
    }
  `,
})
export class ResetPasswordPage {
  private readonly auth = inject(AuthService);
  private readonly query = inject(ActivatedRoute).snapshot.queryParamMap;

  protected readonly form = inject(FormBuilder).nonNullable.group({ newPassword: '' });
  protected readonly busy = signal(false);
  protected readonly errors = signal<string[]>([]);
  protected readonly done = signal(false);

  protected submit(): Promise<void> {
    return attempt(this.busy, this.errors, async () => {
      await this.auth.resetPassword(
        this.query.get('userId') ?? '',
        this.query.get('token') ?? '',
        this.form.getRawValue().newPassword,
      );
      this.done.set(true);
    });
  }
}
