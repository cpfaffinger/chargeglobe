import { Routes } from '@angular/router';
import { ADMIN_PERMISSIONS } from './admin/admin-pages';
import { requireAnyPermission, requirePermission } from './auth/permission.guard';
import { HomePage } from './pages/home.page';
import { requireSetupDone, setupAccess } from './setup/setup.service';

// The map is the start page and part of the initial bundle; every other page is a lazy chunk, so the Material
// components only it uses (stepper, table, expansion panels, …) load with it.

/** Every page except the wizard waits for the initial setup (GET /api/v1/setup). */
const pages: Routes = [
  { path: '', component: HomePage, pathMatch: 'full' },
  { path: 'register', loadComponent: () => import('./pages/register.page').then((page) => page.RegisterPage) },
  { path: 'confirm-email', loadComponent: () => import('./pages/confirm-email.page').then((page) => page.ConfirmEmailPage) },
  { path: 'login', loadComponent: () => import('./pages/login.page').then((page) => page.LoginPage) },
  {
    path: 'forgot-password',
    loadComponent: () => import('./pages/forgot-password.page').then((page) => page.ForgotPasswordPage),
  },
  { path: 'reset-password', loadComponent: () => import('./pages/reset-password.page').then((page) => page.ResetPasswordPage) },
  // Mirrors x-required-permission of the /api/v1/evses endpoints the page reads.
  {
    path: 'locations/:id',
    loadComponent: () => import('./pages/location-detail.page').then((page) => page.LocationDetailPage),
    canActivate: [requirePermission('evse:read')],
  },
  // Any signed-in user; anonymous visitors are redirected to /login.
  {
    path: 'favorites',
    loadComponent: () => import('./pages/favorites.page').then((page) => page.FavoritesPage),
    canActivate: [requirePermission()],
  },
  {
    path: 'stats',
    loadComponent: () => import('./pages/stats.page').then((page) => page.StatsPage),
    canActivate: [requirePermission('evse:read')],
  },
  {
    path: 'evses/:evseId',
    loadComponent: () => import('./pages/evse-detail.page').then((page) => page.EvseDetailPage),
    canActivate: [requirePermission('evse:read')],
  },
  // Signed-in users only; anonymous visitors are redirected to /login.
  {
    path: 'account',
    loadComponent: () => import('./pages/account.page').then((page) => page.AccountPage),
    canActivate: [requirePermission()],
  },
  // Administration: the frame opens with any admin permission; every subpage mirrors x-required-permission of the
  // endpoints it reads.
  {
    path: 'admin',
    loadComponent: () => import('./pages/admin.page').then((page) => page.AdminPage),
    canActivate: [requireAnyPermission(ADMIN_PERMISSIONS)],
    children: [
      { path: '', loadComponent: () => import('./pages/admin.page').then((page) => page.AdminOverviewPage) },
      {
        path: 'users',
        loadComponent: () => import('./pages/admin-users.page').then((page) => page.AdminUsersPage),
        canActivate: [requirePermission('user:read')],
      },
      {
        path: 'roles',
        loadComponent: () => import('./pages/admin-roles.page').then((page) => page.AdminRolesPage),
        canActivate: [requirePermission('user:read')],
      },
      {
        path: 'crawler',
        loadComponent: () => import('./pages/admin-crawler.page').then((page) => page.AdminCrawlerPage),
        canActivate: [requirePermission('jobs:read')],
      },
      {
        path: 'conflicts',
        loadComponent: () => import('./pages/admin-conflicts.page').then((page) => page.AdminConflictsPage),
        canActivate: [requirePermission('conflict:resolve')],
      },
      {
        path: 'mail',
        loadComponent: () => import('./pages/admin-mail.page').then((page) => page.AdminMailPage),
        canActivate: [requirePermission('settings:manage')],
      },
      {
        path: 'settings',
        loadComponent: () => import('./pages/admin-settings.page').then((page) => page.AdminSettingsPage),
        canActivate: [requirePermission('settings:read')],
      },
      {
        path: 'tariffs',
        loadComponent: () => import('./pages/admin-tariffs.page').then((page) => page.AdminTariffsPage),
        canActivate: [requirePermission('tariff:write')],
      },
    ],
  },
  { path: '**', redirectTo: '' },
];

export const routes: Routes = [
  // Open only while no user exists; afterwards it leads to the map.
  {
    path: 'setup',
    loadComponent: () => import('./pages/setup.page').then((page) => page.SetupPage),
    canActivate: [setupAccess],
  },
  { path: '', canActivateChild: [requireSetupDone], children: pages },
];
