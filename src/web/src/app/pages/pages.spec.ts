import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { AuthService } from '../auth/auth.service';
import { ME, fakeAuth, flush, submit, text, type, validationError } from '../testing';
import { AccountPage } from './account.page';
import { ConfirmEmailPage } from './confirm-email.page';
import { ForgotPasswordPage } from './forgot-password.page';
import { LoginPage } from './login.page';
import { RegisterPage } from './register.page';
import { ResetPasswordPage } from './reset-password.page';

@Component({ template: 'target' })
class TargetPage {}

describe('account pages', () => {
  let auth: ReturnType<typeof fakeAuth>;
  let harness: RouterTestingHarness;

  const page = () => harness.routeNativeElement!;

  /** Opens the URL and waits until the page has rendered. */
  async function open(url: string): Promise<void> {
    harness = await RouterTestingHarness.create(url);
    await settle();
  }

  async function settle(): Promise<void> {
    await flush();
    await harness.fixture.whenStable();
  }

  beforeEach(() => {
    auth = fakeAuth();
    TestBed.configureTestingModule({
      providers: [
        // AccountPage (also the target of a login) requests the health; no request leaves the test.
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: auth },
        provideRouter([
          { path: '', component: TargetPage },
          { path: 'login', component: LoginPage },
          { path: 'register', component: RegisterPage },
          { path: 'confirm-email', component: ConfirmEmailPage },
          { path: 'forgot-password', component: ForgotPasswordPage },
          { path: 'reset-password', component: ResetPasswordPage },
          { path: 'account', component: AccountPage },
        ]),
      ],
    });
  });

  describe('LoginPage', () => {
    async function logIn(): Promise<void> {
      type(page(), 'input[type=email]', 'anna@example.org');
      type(page(), 'input[type=password]', 'secret password');
      submit(page());
      await settle();
    }

    it('logs in and returns to the page that required the login', async () => {
      auth.login.and.resolveTo();
      await open('/login?returnUrl=%2Faccount');

      await logIn();

      expect(auth.login).toHaveBeenCalledOnceWith('anna@example.org', 'secret password');
      expect(TestBed.inject(Router).url).toBe('/account');
    });

    it('goes to the start page without a return URL', async () => {
      auth.login.and.resolveTo();
      await open('/login');

      await logIn();

      expect(TestBed.inject(Router).url).toBe('/');
    });

    it('shows the error of a rejected login and stays on the page', async () => {
      auth.login.and.rejectWith(validationError('E-mail address or password is wrong.'));
      await open('/login');

      await logIn();

      expect(text(page(), '.error')).toBe('E-mail address or password is wrong.');
      expect(page().querySelector<HTMLButtonElement>('button')!.disabled).toBeFalse();
      expect(TestBed.inject(Router).url).toBe('/login');
    });
  });

  describe('RegisterPage', () => {
    async function register(): Promise<void> {
      await open('/register');
      type(page(), 'input[type=email]', 'anna@example.org');
      type(page(), 'input[type=password]', 'secret password');
      submit(page());
      await settle();
    }

    it('registers in German and confirms without revealing whether the address existed', async () => {
      auth.register.and.resolveTo('queued');

      await register();

      expect(auth.register).toHaveBeenCalledOnceWith('anna@example.org', 'secret password', 'de');
      expect(text(page(), '#register-done')).toContain('Falls die Adresse noch nicht registriert war');
      expect(page().querySelector('form')).toBeNull();
    });

    it('shows the field errors of the API', async () => {
      auth.register.and.rejectWith(validationError('Passwords must be at least 10 characters.'));

      await register();

      expect(text(page(), '.error')).toBe('Passwords must be at least 10 characters.');
      expect(page().querySelector('#register-done')).toBeNull();
    });
  });

  describe('ConfirmEmailPage', () => {
    it('confirms the address with the parameters of the mail link', async () => {
      auth.confirmEmail.and.resolveTo();

      await open('/confirm-email?userId=user-1&token=token-1');

      expect(auth.confirmEmail).toHaveBeenCalledOnceWith('user-1', 'token-1');
      expect(text(page(), '#confirm-done')).toBe('Deine E-Mail-Adresse ist bestätigt.');
    });

    it('shows the error for an invalid or missing token', async () => {
      auth.confirmEmail.and.rejectWith(validationError('The token is invalid, expired or was already used.'));

      await open('/confirm-email');

      expect(auth.confirmEmail).toHaveBeenCalledOnceWith('', '');
      expect(text(page(), '.error')).toBe('The token is invalid, expired or was already used.');
      expect(page().querySelector('#confirm-done')).toBeNull();
    });
  });

  describe('ForgotPasswordPage', () => {
    async function request(): Promise<void> {
      await open('/forgot-password');
      type(page(), 'input[type=email]', 'anna@example.org');
      submit(page());
      await settle();
    }

    it('requests the reset mail', async () => {
      auth.requestPasswordReset.and.resolveTo('sent');

      await request();

      expect(auth.requestPasswordReset).toHaveBeenCalledOnceWith('anna@example.org', 'de');
      expect(text(page(), '#forgot-done')).toContain('Falls zu der Adresse ein Konto gehört');
    });

    it('shows the error of the API', async () => {
      auth.requestPasswordReset.and.rejectWith(validationError('Must be a valid e-mail address.'));

      await request();

      expect(text(page(), '.error')).toBe('Must be a valid e-mail address.');
    });
  });

  describe('ResetPasswordPage', () => {
    async function reset(url: string): Promise<void> {
      await open(url);
      type(page(), 'input[type=password]', 'new secret password');
      submit(page());
      await settle();
    }

    it('sets the new password with the parameters of the mail link', async () => {
      auth.resetPassword.and.resolveTo();

      await reset('/reset-password?userId=user-1&token=token-1');

      expect(auth.resetPassword).toHaveBeenCalledOnceWith('user-1', 'token-1', 'new secret password');
      expect(text(page(), '#reset-done')).toBe('Dein Passwort wurde geändert.');
    });

    it('shows the error for an invalid or missing token', async () => {
      auth.resetPassword.and.rejectWith(validationError('The token is invalid, expired or was already used.'));

      await reset('/reset-password');

      expect(auth.resetPassword).toHaveBeenCalledOnceWith('', '', 'new secret password');
      expect(text(page(), '.error')).toBe('The token is invalid, expired or was already used.');
    });
  });

  describe('AccountPage', () => {
    let http: HttpTestingController;

    beforeEach(() => {
      http = TestBed.inject(HttpTestingController);
      auth.me.set(ME);
    });

    afterEach(() => http.verify());

    it('shows identity, roles and permissions of the signed-in user and the state of API and database', async () => {
      await open('/account');

      expect(text(page(), '#account-email')).toBe('anna@example.org');
      expect(text(page(), '#account-roles')).toBe('User');
      expect(text(page(), '#account-permissions')).toBe('apikey:manage, evse:read, price:read, route:read');
      expect(text(page(), '#api-status')).toBe('API: …');

      http.expectOne('/api/v1/health').flush({ status: 'ok', db: 'unavailable' });
      await settle();

      expect(text(page(), '#api-status')).toBe('API: ok');
      expect(text(page(), '#db-status')).toBe('DB: unavailable');
    });

    it('shows "API: unavailable" when the health request fails', async () => {
      await open('/account');

      http.expectOne('/api/v1/health').flush('Bad Gateway', { status: 502, statusText: 'Bad Gateway' });
      await settle();

      expect(text(page(), '#api-status')).toBe('API: unavailable');
      expect(text(page(), '#db-status')).toBe('DB: unknown');
    });
  });
});
