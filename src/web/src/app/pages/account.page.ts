import { Component, inject } from '@angular/core';
import { AuthService } from '../auth/auth.service';

/** Example of a page for signed-in users only (guarded in app.routes.ts). */
@Component({
  selector: 'app-account',
  template: `
    <h2>Mein Konto</h2>
    <dl>
      <dt>E-Mail</dt>
      <dd id="account-email">{{ auth.me()?.email }}</dd>
      <dt>Rollen</dt>
      <dd id="account-roles">{{ auth.me()?.roles?.join(', ') }}</dd>
      <dt>Berechtigungen</dt>
      <dd id="account-permissions">{{ auth.me()?.permissions?.join(', ') }}</dd>
    </dl>
  `,
})
export class AccountPage {
  protected readonly auth = inject(AuthService);
}
