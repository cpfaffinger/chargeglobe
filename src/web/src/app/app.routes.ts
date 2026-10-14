import { Routes } from '@angular/router';
import { requirePermission } from './auth/permission.guard';
import { AccountPage } from './pages/account.page';
import { AdminMailPage } from './pages/admin-mail.page';
import { AdminSettingsPage } from './pages/admin-settings.page';
import { ConfirmEmailPage } from './pages/confirm-email.page';
import { ForgotPasswordPage } from './pages/forgot-password.page';
import { HomePage } from './pages/home.page';
import { LocationDetailPage } from './pages/location-detail.page';
import { LoginPage } from './pages/login.page';
import { RegisterPage } from './pages/register.page';
import { ResetPasswordPage } from './pages/reset-password.page';
import { SetupPage } from './pages/setup.page';
import { requireSetupDone, setupAccess } from './setup/setup.service';

/** Every page except the wizard waits for the initial setup (GET /api/v1/setup). */
const pages: Routes = [
  { path: '', component: HomePage, pathMatch: 'full' },
  { path: 'register', component: RegisterPage },
  { path: 'confirm-email', component: ConfirmEmailPage },
  { path: 'login', component: LoginPage },
  { path: 'forgot-password', component: ForgotPasswordPage },
  { path: 'reset-password', component: ResetPasswordPage },
  // Mirrors x-required-permission of the /api/v1/evses endpoints the page reads.
  { path: 'locations/:id', component: LocationDetailPage, canActivate: [requirePermission('evse:read')] },
  // Signed-in users only; anonymous visitors are redirected to /login.
  { path: 'account', component: AccountPage, canActivate: [requirePermission()] },
  // Mirrors x-required-permission of GET /api/v1/settings.
  { path: 'admin/settings', component: AdminSettingsPage, canActivate: [requirePermission('settings:read')] },
  // Mirrors x-required-permission of the /api/v1/mail endpoints.
  { path: 'admin/mail', component: AdminMailPage, canActivate: [requirePermission('settings:manage')] },
  { path: '**', redirectTo: '' },
];

export const routes: Routes = [
  // Open while no user exists; afterwards the onboarding of the remaining settings for settings:manage.
  { path: 'setup', component: SetupPage, canActivate: [setupAccess] },
  { path: '', canActivateChild: [requireSetupDone], children: pages },
];
