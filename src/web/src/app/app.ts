import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatMenuModule } from '@angular/material/menu';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { ADMIN_PAGES, AdminPageLink } from './admin/admin-pages';
import { AuthService } from './auth/auth.service';
import { AppIcon, IconName } from './design/icon';
import { ThemeMode, ThemeService, Viewport } from './design/theme.service';
import { LocationSearch } from './search/location-search';

const THEMES: { mode: ThemeMode; label: string; icon: IconName }[] = [
  { mode: 'light', label: 'Hell', icon: 'light_mode' },
  { mode: 'dark', label: 'Dunkel', icon: 'dark_mode' },
  { mode: 'system', label: 'System', icon: 'contrast' },
];

/**
 * Shell: Material toolbar with the navigation and the search, on small screens (Viewport.mobile) a menu button that
 * opens navigation and search as side sheet. The theme switch (light, dark, system) sits in the toolbar or in the side
 * sheet. The admin pages are reached through "Administration": a drop-down with the overview and the permitted pages
 * in the toolbar, the overview in the side sheet.
 */
@Component({
  imports: [
    AppIcon,
    LocationSearch,
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
        <a matButton routerLink="/favorites" routerLinkActive="active" id="nav-favorites" (click)="close()">Favoriten</a>
        @if (adminPages(); as pages) {
          @if (pages.length > 0) {
            @if (viewport.mobile()) {
              <a matButton routerLink="/admin" routerLinkActive="active" id="nav-admin" (click)="close()">Administration</a>
            } @else {
              <button
                matButton
                type="button"
                id="nav-admin"
                [class.active]="router.url.startsWith('/admin')"
                [matMenuTriggerFor]="adminMenu"
              >
                Administration<app-icon name="expand_more" />
              </button>
              <mat-menu #adminMenu="matMenu">
                <a mat-menu-item routerLink="/admin" id="admin-menu-overview">Übersicht</a>
                @for (page of pages; track page.path) {
                  <a mat-menu-item [routerLink]="'/admin/' + page.path" [id]="'admin-menu-' + page.path">{{ page.label }}</a>
                }
              </mat-menu>
            }
          }
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
        @if (viewport.mobile()) {
          <app-location-search (chosen)="close()" />
        }
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
          @if (!viewport.mobile()) {
            <app-location-search />
          }
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
  protected readonly router = inject(Router);

  protected readonly themes = THEMES;
  protected readonly currentTheme = computed(() => THEMES.find((option) => option.mode === this.theme.mode())!);
  protected readonly menuOpen = signal(false);

  /** The admin pages the user may open; without any, there is no "Administration". */
  protected adminPages(): AdminPageLink[] {
    return ADMIN_PAGES.filter((page) => this.auth.hasPermission(page.permission));
  }

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
