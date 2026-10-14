import { Component, inject } from '@angular/core';
import { Router, RouterLink, RouterOutlet } from '@angular/router';
import { AuthService } from './auth/auth.service';

@Component({
  imports: [RouterLink, RouterOutlet],
  selector: 'app-root',
  template: `
    <header>
      <h1><a routerLink="/">chargeglobe</a></h1>
      <nav>
        @if (auth.me(); as me) {
          @if (auth.hasPermission('settings:read')) {
            <a routerLink="/admin/settings" id="nav-settings">Einstellungen</a>
          }
          @if (auth.hasPermission('settings:manage')) {
            <a routerLink="/admin/mail" id="nav-mail">E-Mail</a>
          }
          <a routerLink="/account" id="nav-account" title="Mein Konto">{{ me.email }}</a>
          <button type="button" id="logout" (click)="logout()">Abmelden</button>
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
  private readonly router = inject(Router);

  /** Ends the session and returns to the map, which is the only page for anonymous visitors. */
  protected async logout(): Promise<void> {
    await this.auth.logout();
    await this.router.navigateByUrl('/');
  }
}
