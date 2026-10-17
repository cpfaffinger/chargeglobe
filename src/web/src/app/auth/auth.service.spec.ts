import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ME, flush } from '../testing';
import { AuthService, SKIP_REFRESH, restoreSession } from './auth.service';

describe('AuthService', () => {
  let auth: AuthService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    auth = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function signIn(): Promise<void> {
    const login = auth.login('anna@example.org', 'secret password');
    const request = http.expectOne('/api/v1/auth/login');
    expect(request.request.body).toEqual({ email: 'anna@example.org', password: 'secret password' });
    request.flush({ accessToken: 'token-1' });
    await flush();
    const me = http.expectOne('/api/v1/me');
    expect(me.request.context.get(SKIP_REFRESH)).toBeTrue();
    me.flush(ME);
    await login;
  }

  it('starts anonymous', () => {
    expect(auth.me()).toBeNull();
    expect(auth.isAuthenticated()).toBeFalse();
    expect(auth.accessToken()).toBeNull();
    expect(auth.hasPermission('evse:read')).toBeFalse();
  });

  it('keeps the access token in memory and loads the user after login', async () => {
    await signIn();

    expect(auth.accessToken()).toBe('token-1');
    expect(auth.me()).toEqual(ME);
    expect(auth.isAuthenticated()).toBeTrue();
    expect(auth.hasPermission('evse:read')).toBeTrue();
    expect(auth.hasPermission('user:manage')).toBeFalse();
  });

  it('rejects a failed login and stays anonymous', async () => {
    const login = auth.login('anna@example.org', 'wrong');
    http.expectOne('/api/v1/auth/login').flush({ title: 'Unauthorized' }, { status: 401, statusText: 'Unauthorized' });

    await expectAsync(login).toBeRejected();
    expect(auth.isAuthenticated()).toBeFalse();
  });

  it('restores the session from the refresh cookie with a single request for parallel calls', async () => {
    const first = TestBed.runInInjectionContext(restoreSession);
    const second = auth.refresh();
    expect(second).toBe(first);

    const request = http.expectOne('/api/v1/auth/refresh');
    expect(request.request.body).toBeNull();
    request.flush({ accessToken: 'token-2' });
    await flush();
    http.expectOne('/api/v1/me').flush(ME);

    expect(await first).toBeTrue();
    expect(auth.accessToken()).toBe('token-2');
    expect(auth.me()).toEqual(ME);

    // The next call starts a new request.
    const third = auth.refresh();
    expect(third).not.toBe(first);
    http.expectOne('/api/v1/auth/refresh').flush(null, { status: 401, statusText: 'Unauthorized' });
    expect(await third).toBeFalse();
  });

  it('becomes anonymous when the refresh fails', async () => {
    await signIn();

    const refreshed = auth.refresh();
    http.expectOne('/api/v1/auth/refresh').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(await refreshed).toBeFalse();
    expect(auth.accessToken()).toBeNull();
    expect(auth.me()).toBeNull();
  });

  it('drops the session on logout, even if the request fails', async () => {
    await signIn();

    const logout = auth.logout();
    http.expectOne('/api/v1/auth/logout').flush(null, { status: 500, statusText: 'Server Error' });
    await logout;

    expect(auth.accessToken()).toBeNull();
    expect(auth.isAuthenticated()).toBeFalse();
  });

  it('registers an account and returns the message of the API', async () => {
    const registered = auth.register('anna@example.org', 'secret password', 'de');
    const request = http.expectOne('/api/v1/auth/register');
    expect(request.request.body).toEqual({ email: 'anna@example.org', password: 'secret password', language: 'de' });
    request.flush({ message: 'queued' });

    expect(await registered).toBe('queued');
  });

  it('confirms an e-mail address', async () => {
    const confirmed = auth.confirmEmail('user-1', 'token-1');
    const request = http.expectOne('/api/v1/auth/confirm-email');
    expect(request.request.body).toEqual({ userId: 'user-1', token: 'token-1' });
    request.flush(null, { status: 204, statusText: 'No Content' });

    await expectAsync(confirmed).toBeResolved();
  });

  it('asks for a new confirmation mail', async () => {
    const requested = auth.resendConfirmation('anna@example.org', 'de');
    const request = http.expectOne('/api/v1/auth/confirm-email/resend');
    expect(request.request.body).toEqual({ email: 'anna@example.org', language: 'de' });
    request.flush({ message: 'queued' });

    expect(await requested).toBe('queued');
  });

  it('requests a password reset mail', async () => {
    const requested = auth.requestPasswordReset('anna@example.org', 'de');
    const request = http.expectOne('/api/v1/auth/password/forgot');
    expect(request.request.body).toEqual({ email: 'anna@example.org', language: 'de' });
    request.flush({ message: 'sent' });

    expect(await requested).toBe('sent');
  });

  it('changes the password of the signed-in account', async () => {
    const changed = auth.changePassword('old secret password', 'new secret password');
    const request = http.expectOne('/api/v1/auth/password/change');
    expect(request.request.body).toEqual({ currentPassword: 'old secret password', newPassword: 'new secret password' });
    request.flush(null, { status: 204, statusText: 'No Content' });

    await expectAsync(changed).toBeResolved();
  });

  it('deletes the own account with the password and ends the session', async () => {
    const deleted = auth.deleteAccount('secret password');
    const request = http.expectOne('/api/v1/me');
    expect([request.request.method, request.request.body]).toEqual(['DELETE', { password: 'secret password' }]);
    request.flush(null, { status: 204, statusText: 'No Content' });

    await deleted;
    expect(auth.isAuthenticated()).toBeFalse();
  });

  it('sets a new password with the reset token', async () => {
    const reset = auth.resetPassword('user-1', 'token-1', 'new secret password');
    const request = http.expectOne('/api/v1/auth/password/reset');
    expect(request.request.body).toEqual({ userId: 'user-1', token: 'token-1', newPassword: 'new secret password' });
    request.flush(null, { status: 204, statusText: 'No Content' });

    await expectAsync(reset).toBeResolved();
  });
});
