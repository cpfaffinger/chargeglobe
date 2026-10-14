import { Component, computed, inject } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatTabsModule } from '@angular/material/tabs';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { ADMIN_PAGES, AdminPageLink } from '../admin/admin-pages';
import { AuthService } from '../auth/auth.service';

/** The admin pages the signed-in user may open. */
function visiblePages(auth: AuthService): AdminPageLink[] {
  return ADMIN_PAGES.filter((page) => auth.hasPermission(page.permission));
}

/**
 * Frame of the administration (/admin): a tab bar with the subpages the user may open, the subpage below. Every
 * subpage has its own route and guard.
 */
@Component({
  imports: [MatTabsModule, RouterLink, RouterLinkActive, RouterOutlet],
  selector: 'app-admin',
  template: `
    <nav mat-tab-nav-bar [tabPanel]="panel" aria-label="Administration" class="admin-tabs">
      <a mat-tab-link routerLink="/admin" routerLinkActive #overview="routerLinkActive" [routerLinkActiveOptions]="{ exact: true }" [active]="overview.isActive" id="admin-tab-overview">Übersicht</a>
      @for (page of pages(); track page.path) {
        <a mat-tab-link [routerLink]="'/admin/' + page.path" routerLinkActive #link="routerLinkActive" [active]="link.isActive" [id]="'admin-tab-' + page.path">{{ page.label }}</a>
      }
    </nav>
    <mat-tab-nav-panel #panel>
      <router-outlet />
    </mat-tab-nav-panel>
  `,
})
export class AdminPage {
  private readonly auth = inject(AuthService);
  protected readonly pages = computed(() => visiblePages(this.auth));
}

/** Start page of the administration: the subpages by category with a short description. */
@Component({
  imports: [MatCardModule, RouterLink],
  selector: 'app-admin-overview',
  template: `
    <h1>Administration</h1>
    @for (group of groups(); track group.category) {
      <section class="admin-group">
        <h2>{{ group.category }}</h2>
        <div class="admin-cards">
          @for (page of group.pages; track page.path) {
            <a class="admin-card" [routerLink]="'/admin/' + page.path" [id]="'admin-card-' + page.path">
              <mat-card appearance="outlined">
                <mat-card-content>
                  <h3>{{ page.label }}</h3>
                  <p class="muted">{{ page.description }}</p>
                </mat-card-content>
              </mat-card>
            </a>
          }
        </div>
      </section>
    }
  `,
})
export class AdminOverviewPage {
  private readonly auth = inject(AuthService);

  protected readonly groups = computed(() => {
    const pages = visiblePages(this.auth);
    const categories = [...new Set(pages.map((page) => page.category))];
    return categories.map((category) => ({ category, pages: pages.filter((page) => page.category === category) }));
  });
}
