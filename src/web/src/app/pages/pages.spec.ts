import { CardsStore } from '../preferences/cards.store';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
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
import { LOGIN_PROBLEMS, LoginPage } from './login.page';
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

    it('checks the form before anything is sent', async () => {
      await open('/login');

      type(page(), 'input[type=email]', 'no address');
      submit(page());
      await settle();

      expect(auth.login).not.toHaveBeenCalled();
      expect(Array.from(page().querySelectorAll('mat-error')).map((error) => error.textContent!.trim())).toEqual([
        'Bitte eine E-Mail-Adresse eingeben.',
        'Bitte das Passwort eingeben.',
      ]);
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

    const refused = (code: string) =>
      new HttpErrorResponse({ status: code === 'invalid_credentials' ? 401 : 403, error: { title: 'Login refused', code } });

    it('explains the code of a refused login in German', async () => {
      await open('/login');

      for (const code of ['invalid_credentials', 'account_pending_approval', 'account_disabled']) {
        auth.login.and.rejectWith(refused(code));
        await logIn();
        expect(text(page(), '.error')).toBe(LOGIN_PROBLEMS[code]);
        expect(page().querySelector('#resend')).toBeNull();
      }

      auth.login.and.rejectWith(refused('something_new'));
      await logIn();
      expect(text(page(), '.error')).toBe('Login refused');
    });

    it('sends the confirmation mail again for an unconfirmed account', async () => {
      auth.login.and.rejectWith(refused('account_not_confirmed'));
      auth.resendConfirmation.and.resolveTo('queued');
      await open('/login');
      await logIn();

      expect(text(page(), '.error')).toBe(LOGIN_PROBLEMS['account_not_confirmed']);
      page().querySelector<HTMLButtonElement>('#resend')!.click();
      await settle();

      expect(auth.resendConfirmation).toHaveBeenCalledOnceWith('anna@example.org', 'de');
      expect(page().querySelector('#resend')).toBeNull();
      expect(page().querySelector('.error')).toBeNull();
      expect(text(page(), '#resent')).toBe('Wir haben dir eine neue Bestätigungsmail geschickt. Bitte prüfe auch den Spam-Ordner.');

      // A new attempt clears the message.
      await logIn();
      expect(page().querySelector('#resent')).toBeNull();
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

    it('changes the password after checking the form and explains a wrong current password', async () => {
      await open('/account');
      http.expectOne('/api/v1/health').flush({ status: 'ok', db: 'ok' });
      const form = page().querySelector<HTMLElement>('#password-change')!;
      const fill = (current: string, next: string, repeat: string) => {
        type(form, 'input[formControlName=currentPassword]', current);
        type(form, 'input[formControlName=newPassword]', next);
        type(form, 'input[formControlName=repeat]', repeat);
        submit(form);
      };

      submit(form);
      await settle();
      expect(form.querySelectorAll('mat-error').length).toBe(3);

      fill('old secret password', 'new secret password', 'other secret password');
      await settle();
      expect(text(form, '.error')).toBe('Die beiden neuen Passwörter stimmen nicht überein.');
      expect(auth.changePassword).not.toHaveBeenCalled();

      auth.changePassword.and.rejectWith(
        new HttpErrorResponse({ status: 422, error: { title: 'Validation failed', errors: { currentPassword: ['The current password is wrong.'] } } }),
      );
      fill('old secret password', 'new secret password', 'new secret password');
      await settle();
      expect(text(form, '.error')).toBe('Das aktuelle Passwort ist falsch.');

      auth.changePassword.and.rejectWith(validationError('Must differ from the current password.'));
      fill('old secret password', 'new secret password', 'new secret password');
      await settle();
      expect(text(form, '.error')).toBe('Must differ from the current password.');

      auth.changePassword.and.resolveTo();
      fill('old secret password', 'new secret password', 'new secret password');
      await settle();
      expect(auth.changePassword).toHaveBeenCalledWith('old secret password', 'new secret password');
      expect(text(form, '#password-changed')).toBe('Dein Passwort ist geändert. Auf anderen Geräten bist du abgemeldet.');
      expect(form.querySelector('.error')).toBeNull();
      expect(form.querySelector<HTMLInputElement>('input[formControlName=newPassword]')!.value).toBe('');
    });

    it('deletes the own account after the password and a confirmation and explains a wrong password', async () => {
      await open('/account');
      http.expectOne('/api/v1/health').flush({ status: 'ok', db: 'ok' });
      const form = page().querySelector<HTMLElement>('#account-delete')!;
      const confirm = spyOn(window, 'confirm').and.returnValue(false);

      submit(form);
      await settle();
      expect(form.querySelectorAll('mat-error').length).toBe(1);
      expect(confirm).not.toHaveBeenCalled();

      type(form, 'input[formControlName=password]', 'secret password');
      submit(form);
      await settle();
      expect(confirm).toHaveBeenCalled();
      expect(auth.deleteAccount).not.toHaveBeenCalled();

      confirm.and.returnValue(true);
      auth.deleteAccount.and.rejectWith(
        new HttpErrorResponse({ status: 422, error: { title: 'Validation failed', errors: { password: ['The password is not correct.'] } } }),
      );
      submit(form);
      await settle();
      expect(text(form, '.error')).toBe('Das Passwort ist falsch.');

      auth.deleteAccount.and.rejectWith(new HttpErrorResponse({ status: 409, error: { title: 'Conflict', detail: 'Last manager.' } }));
      submit(form);
      await settle();
      expect(text(form, '.error')).toBe('Last manager.');

      auth.deleteAccount.and.resolveTo();
      submit(form);
      await settle();
      expect(auth.deleteAccount).toHaveBeenCalledWith('secret password');
      expect(TestBed.inject(Router).url).toBe('/');
    });

    it('lets users with price:read choose their own charging cards', async () => {
      auth.hasPermission.and.callFake((permission) => permission === 'price:read');
      await open('/account');
      http.expectOne('/api/v1/health').flush({ status: 'ok', db: 'ok' });
      expect(text(page(), '#own-cards li')).toBe('Anbieter werden geladen …');
      http.expectOne('/api/v1/emps').flush({
        data: [
          { key: 'smatrics', name: 'SMATRICS', website: null, country: 'AT', tariffs: 1 },
          { key: 'oeamtc', name: 'ÖAMTC ePower', website: null, country: 'AT', tariffs: 1 },
        ],
      });
      await settle();
      const cards = TestBed.inject(CardsStore);
      cards.set(['oeamtc']);
      await settle();

      const box = (key: string) => page().querySelector<HTMLInputElement>(`mat-checkbox[data-card="${key}"] input`)!;
      expect([box('smatrics').checked, box('oeamtc').checked]).toEqual([false, true]);
      box('smatrics').click();
      await settle();
      expect(cards.cards()).toEqual(['oeamtc', 'smatrics']);
      box('oeamtc').click();
      await settle();
      expect(cards.cards()).toEqual(['smatrics']);
    });

    it('says when no EMP exists yet', async () => {
      auth.hasPermission.and.callFake((permission) => permission === 'price:read');
      await open('/account');
      http.expectOne('/api/v1/health').flush({ status: 'ok', db: 'ok' });
      http.expectOne('/api/v1/emps').flush({ data: [] });
      await settle();
      expect(text(page(), '#own-cards li')).toBe('Noch keine Ladekarten-Anbieter erfasst.');
    });

    it('says when the list of EMPs cannot be loaded', async () => {
      auth.hasPermission.and.callFake((permission) => permission === 'price:read');
      await open('/account');
      http.expectOne('/api/v1/health').flush({ status: 'ok', db: 'ok' });
      http.expectOne('/api/v1/emps').flush({ title: 'Error', detail: 'Database down.' }, { status: 503, statusText: 'Error' });
      await settle();
      expect(text(page(), '#own-cards .error')).toBe('Database down.');
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
