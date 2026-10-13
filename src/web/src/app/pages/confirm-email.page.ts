import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { attempt } from '../auth/problem';

/** Target of the link in the confirmation mail: /confirm-email?userId=…&token=… */
@Component({
  imports: [RouterLink],
  selector: 'app-confirm-email',
  template: `
    <h2>E-Mail-Adresse bestätigen</h2>
    @if (confirmed()) {
      <p id="confirm-done" role="status">Deine E-Mail-Adresse ist bestätigt.</p>
      <p><a routerLink="/login">Zur Anmeldung</a></p>
    } @else if (busy()) {
      <p>Die Adresse wird bestätigt …</p>
    }
    @for (error of errors(); track error) {
      <p class="error" role="alert">{{ error }}</p>
    }
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
