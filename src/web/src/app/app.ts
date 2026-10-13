import { Component, inject } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';
import { AuthService } from './auth/auth.service';

@Component({
  imports: [RouterLink, RouterOutlet],
  selector: 'app-root',
  template: `
    <header>
      <h1><a routerLink="/">chargeglobe</a></h1>
      <nav>
        @if (auth.isAuthenticated()) {
          @if (auth.hasPermission('settings:read')) {
            <a routerLink="/admin/settings" id="nav-settings">Einstellungen</a>
          }
          <a routerLink="/account" id="nav-account">Mein Konto</a>
        } @else {
          <a routerLink="/login" id="nav-login">Anmelden</a>
          <a routerLink="/register" id="nav-register">Registrieren</a>
        }
      </nav>
    </header>
    <main>
      <router-outlet />
    </main>
  `,
})
export class App {
  protected readonly auth = inject(AuthService);
}
