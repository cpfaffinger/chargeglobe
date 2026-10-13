import { Routes } from '@angular/router';
import { requirePermission } from './auth/permission.guard';
import { AccountPage } from './pages/account.page';
import { ConfirmEmailPage } from './pages/confirm-email.page';
import { ForgotPasswordPage } from './pages/forgot-password.page';
import { HomePage } from './pages/home.page';
import { LoginPage } from './pages/login.page';
import { RegisterPage } from './pages/register.page';
import { ResetPasswordPage } from './pages/reset-password.page';

export const routes: Routes = [
  { path: '', component: HomePage, pathMatch: 'full' },
  { path: 'register', component: RegisterPage },
  { path: 'confirm-email', component: ConfirmEmailPage },
  { path: 'login', component: LoginPage },
  { path: 'forgot-password', component: ForgotPasswordPage },
  { path: 'reset-password', component: ResetPasswordPage },
  // Signed-in users only; anonymous visitors are redirected to /login.
  { path: 'account', component: AccountPage, canActivate: [requirePermission()] },
  { path: '**', redirectTo: '' },
];
