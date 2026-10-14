import { Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { attempt } from '../auth/problem';

/** Target of the link in the confirmation mail: /confirm-email?userId=…&token=… */
@Component({
  imports: [MatButtonModule, MatCardModule, MatProgressBarModule, RouterLink],
  selector: 'app-confirm-email',
  host: { class: 'form-page' },
  template: `
    <mat-card appearance="outlined">
      <mat-card-content>
        <h1>E-Mail-Adresse bestätigen</h1>
        @if (confirmed()) {
          <p id="confirm-done" role="status">Deine E-Mail-Adresse ist bestätigt.</p>
          <p><a matButton="filled" routerLink="/login">Zur Anmeldung</a></p>
        } @else if (busy()) {
          <p>Die Adresse wird bestätigt …</p>
          <mat-progress-bar mode="indeterminate" aria-label="Die Adresse wird bestätigt" />
        }
        @for (error of errors(); track error) {
          <p class="error" role="alert">{{ error }}</p>
        }
      </mat-card-content>
    </mat-card>
  `,
})
export class ConfirmEmailPage {
  protected readonly busy = signal(false);
  protected readonly errors = signal<string[]>([]);
  protected readonly confirmed = signal(false);

  constructor() {
    const auth = inject(AuthService);
    const query = inject(ActivatedRoute).snapshot.queryParamMap;

    void attempt(this.busy, this.errors, async () => {
      await auth.confirmEmail(query.get('userId') ?? '', query.get('token') ?? '');
      this.confirmed.set(true);
    });
  }
}
