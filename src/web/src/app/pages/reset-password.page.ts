import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { attemptValid } from '../auth/problem';

/** Target of the link in the reset mail: /reset-password?userId=…&token=… */
@Component({
  imports: [MatButtonModule, MatCardModule, MatFormFieldModule, MatInputModule, ReactiveFormsModule, RouterLink],
  selector: 'app-reset-password',
  host: { class: 'form-page' },
  template: `
    <mat-card appearance="outlined">
      <mat-card-content>
        <h1>Neues Passwort vergeben</h1>
        @if (done()) {
          <p id="reset-done" role="status">Dein Passwort wurde geändert.</p>
          <p><a matButton routerLink="/login">Zur Anmeldung</a></p>
        } @else {
          @for (error of errors(); track error) {
            <p class="error" role="alert">{{ error }}</p>
          }
          <form [formGroup]="form" (ngSubmit)="submit()">
            <mat-form-field>
              <mat-label>Neues Passwort</mat-label>
              <input matInput type="password" formControlName="newPassword" autocomplete="new-password" required />
              <mat-hint>Mindestens 10 Zeichen.</mat-hint>
              <mat-error>Mindestens 10 Zeichen.</mat-error>
            </mat-form-field>
            <button matButton="filled" type="submit" [disabled]="busy()">Passwort speichern</button>
          </form>
        }
      </mat-card-content>
    </mat-card>
  `,
})
export class ResetPasswordPage {
  private readonly auth = inject(AuthService);
  private readonly query = inject(ActivatedRoute).snapshot.queryParamMap;

  protected readonly form = inject(FormBuilder).nonNullable.group({
    newPassword: ['', [Validators.required, Validators.minLength(10)]],
  });
  protected readonly busy = signal(false);
  protected readonly errors = signal<string[]>([]);
  protected readonly done = signal(false);

  protected submit(): Promise<void> {
    return attemptValid(this.form, this.busy, this.errors, async () => {
      await this.auth.resetPassword(
        this.query.get('userId') ?? '',
        this.query.get('token') ?? '',
        this.form.getRawValue().newPassword,
      );
      this.done.set(true);
    });
  }
}
