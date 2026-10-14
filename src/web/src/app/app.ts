import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatMenuModule } from '@angular/material/menu';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from './auth/auth.service';
import { AppIcon, IconName } from './design/icon';
import { ThemeMode, ThemeService, Viewport } from './design/theme.service';

const THEMES: { mode: ThemeMode; label: string; icon: IconName }[] = [
  { mode: 'light', label: 'Hell', icon: 'light_mode' },
  { mode: 'dark', label: 'Dunkel', icon: 'dark_mode' },
  { mode: 'system', label: 'System', icon: 'contrast' },
];

/**
 * Shell: Material toolbar with the navigation, on small screens (Viewport.mobile) a menu button that opens the
 * navigation as side sheet. The theme switch (light, dark, system) sits in the toolbar or in the side sheet.
 */
@Component({
  imports: [
    AppIcon,
    MatButtonModule,
    MatButtonToggleModule,
    MatMenuModule,
    MatSidenavModule,
    MatToolbarModule,
    NgTemplateOutlet,
    RouterLink,
    RouterLinkActive,
    RouterOutlet,
  ],
  selector: 'app-root',
  template: `
    <ng-template #links>
      @if (auth.me(); as me) {
        <a matButton routerLink="/" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }" id="nav-map" (click)="close()">Karte</a>
        @if (auth.hasPermission('settings:read')) {
          <a matButton routerLink="/admin/settings" routerLinkActive="active" id="nav-settings" (click)="close()">Einstellungen</a>
        }
        @if (auth.hasPermission('jobs:read')) {
          <a matButton routerLink="/admin/crawler" routerLinkActive="active" id="nav-crawler" (click)="close()">Crawler</a>
        }
        @if (auth.hasPermission('settings:manage')) {
          <a matButton routerLink="/admin/mail" routerLinkActive="active" id="nav-mail" (click)="close()">E-Mail</a>
          <a matButton routerLink="/setup" routerLinkActive="active" id="nav-setup" (click)="close()">Einrichtung</a>
        }
        <a matButton routerLink="/account" routerLinkActive="active" id="nav-account" title="Mein Konto" (click)="close()">{{ me.email }}</a>
        <button matButton type="button" id="logout" (click)="logout()"><app-icon name="logout" />Abmelden</button>
      } @else {
        <a matButton routerLink="/login" routerLinkActive="active" id="nav-login" (click)="close()">Anmelden</a>
        <a matButton="filled" routerLink="/register" id="nav-register" (click)="close()">Registrieren</a>
      }
    </ng-template>

    <mat-sidenav-container class="shell">
      <mat-sidenav class="nav-sheet" mode="over" [opened]="menuOpen()" (closedStart)="menuOpen.set(false)">
        <a class="brand" routerLink="/" (click)="close()"><span class="logo"><app-icon name="bolt" /></span>chargeglobe</a>
        <nav aria-label="Navigation">
          @if (viewport.mobile()) {
            <ng-container [ngTemplateOutlet]="links" />
          }
        </nav>
        <span class="theme-label" id="theme-label">Erscheinungsbild</span>
        <mat-button-toggle-group
          id="theme-toggle"
          aria-labelledby="theme-label"
          [value]="theme.mode()"
          (change)="theme.set($event.value)"
        >
          @for (option of themes; track option.mode) {
            <mat-button-toggle [value]="option.mode">{{ option.label }}</mat-button-toggle>
          }
        </mat-button-toggle-group>
      </mat-sidenav>
      <mat-sidenav-content class="shell-content">
        <mat-toolbar class="app-toolbar">
          @if (viewport.mobile()) {
            <button matIconButton type="button" id="menu" aria-label="Menü öffnen" (click)="menuOpen.set(true)">
              <app-icon name="menu" />
            </button>
          }
          <a class="brand" routerLink="/"><span class="logo"><app-icon name="bolt" /></span>chargeglobe</a>
          @if (!viewport.mobile()) {
            <nav class="toolbar-nav" aria-label="Hauptnavigation">
              <ng-container [ngTemplateOutlet]="links" />
            </nav>
          }
          <span class="spacer"></span>
          <button
            matIconButton
            type="button"
            id="theme-menu"
            [matMenuTriggerFor]="themeMenu"
            [attr.aria-label]="'Erscheinungsbild: ' + currentTheme().label"
            [title]="'Erscheinungsbild: ' + currentTheme().label"
          >
            <app-icon [name]="currentTheme().icon" />
          </button>
          <mat-menu #themeMenu="matMenu">
            @for (option of themes; track option.mode) {
              <button mat-menu-item type="button" role="menuitemradio" [attr.aria-checked]="option.mode === theme.mode()" (click)="theme.set(option.mode)">
                <app-icon [name]="option.icon" /><span>{{ option.label }}</span>
              </button>
            }
          </mat-menu>
        </mat-toolbar>
        <main>
          <router-outlet />
        </main>
      </mat-sidenav-content>
    </mat-sidenav-container>
  `,
})
export class App {
  protected readonly auth = inject(AuthService);
  protected readonly theme = inject(ThemeService);
  protected readonly viewport = inject(Viewport);
  private readonly router = inject(Router);

  protected readonly themes = THEMES;
  protected readonly currentTheme = computed(() => THEMES.find((option) => option.mode === this.theme.mode())!);
  protected readonly menuOpen = signal(false);

  protected close(): void {
    this.menuOpen.set(false);
  }

  /** Ends the session and returns to the map, which is the only page for anonymous visitors. */
  protected async logout(): Promise<void> {
    this.close();
    await this.auth.logout();
    await this.router.navigateByUrl('/');
  }
}
