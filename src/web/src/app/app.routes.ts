import { Routes } from '@angular/router';
import { requirePermission } from './auth/permission.guard';
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
  // Signed-in users only; anonymous visitors are redirected to /login.
  {
    path: 'account',
    loadComponent: () => import('./pages/account.page').then((page) => page.AccountPage),
    canActivate: [requirePermission()],
  },
  // Mirrors x-required-permission of GET /api/v1/settings.
  {
    path: 'admin/settings',
    loadComponent: () => import('./pages/admin-settings.page').then((page) => page.AdminSettingsPage),
    canActivate: [requirePermission('settings:read')],
  },
  // Mirrors x-required-permission of the /api/v1/mail endpoints.
  {
    path: 'admin/mail',
    loadComponent: () => import('./pages/admin-mail.page').then((page) => page.AdminMailPage),
    canActivate: [requirePermission('settings:manage')],
  },
  { path: '**', redirectTo: '' },
];

export const routes: Routes = [
  // Open while no user exists; afterwards the onboarding of the remaining settings for settings:manage.
  {
    path: 'setup',
    loadComponent: () => import('./pages/setup.page').then((page) => page.SetupPage),
    canActivate: [setupAccess],
  },
  { path: '', canActivateChild: [requireSetupDone], children: pages },
];
