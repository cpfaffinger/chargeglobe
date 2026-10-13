import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { App } from './app';
import { appConfig } from './app.config';
import { routes } from './app.routes';
import { AuthService } from './auth/auth.service';
import { ME, fakeAuth, flush, text } from './testing';

describe('App with its routes', () => {
  let auth: ReturnType<typeof fakeAuth>;
  let fixture: ComponentFixture<App>;
  let http: HttpTestingController;
  let router: Router;

  const root = () => fixture.nativeElement as HTMLElement;

  async function navigate(url: string): Promise<void> {
    await router.navigateByUrl(url);
    await flush();
    await fixture.whenStable();
  }

  beforeEach(async () => {
    auth = fakeAuth();
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter(routes),
        { provide: AuthService, useValue: auth },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(App);
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
  });

  afterEach(() => http.verify());

  describe('start page', () => {
    it('is reachable anonymously and shows the map placeholder and a placeholder status', async () => {
      await navigate('/');

      expect(root().querySelector('#map-placeholder')).not.toBeNull();
      expect(text(root(), '#api-status')).toBe('API: …');
      expect(text(root(), '#db-status')).toBe('DB: …');
      expect(text(root(), '#anonymous')).toContain('Anmelden oder Konto anlegen');
      expect(root().querySelector('#session')).toBeNull();
      expect(root().querySelector('#nav-login')).not.toBeNull();
      expect(root().querySelector('#nav-register')).not.toBeNull();
      expect(root().querySelector('#nav-account')).toBeNull();
      http.expectOne('/api/v1/health');
    });

    it('shows the API and database state reported by the API', async () => {
      await navigate('/');
      http.expectOne('/api/v1/health').flush({ status: 'ok', db: 'unavailable' });
      await fixture.whenStable();

      expect(text(root(), '#api-status')).toBe('API: ok');
      expect(text(root(), '#db-status')).toBe('DB: unavailable');
    });

    it('shows "API: unavailable" when the request fails', async () => {
      await navigate('/');
      http.expectOne('/api/v1/health').flush('Bad Gateway', { status: 502, statusText: 'Bad Gateway' });
      await fixture.whenStable();

      expect(text(root(), '#api-status')).toBe('API: unavailable');
      expect(text(root(), '#db-status')).toBe('DB: unknown');
    });

    it('shows e-mail, permissions and a logout button after login', async () => {
      auth.me.set(ME);
      auth.logout.and.resolveTo();
      await navigate('/');
      http.expectOne('/api/v1/health').flush({ status: 'ok', db: 'ok' });

      expect(text(root(), '#me-email')).toBe('anna@example.org');
      expect(text(root(), '#me-permissions')).toBe('Berechtigungen: apikey:manage, evse:read, price:read, route:read');
      expect(root().querySelector('#anonymous')).toBeNull();
      expect(root().querySelector('#nav-account')).not.toBeNull();
      expect(root().querySelector('#nav-login')).toBeNull();

      root().querySelector<HTMLButtonElement>('#logout')!.click();

      expect(auth.logout).toHaveBeenCalledTimes(1);
    });
  });

  describe('routing', () => {
    it('redirects anonymous visitors from /account to the login', async () => {
      await navigate('/account');

      expect(router.url).toBe('/login?returnUrl=%2Faccount');
      expect(text(root(), 'h2')).toBe('Anmelden');
    });

    it('opens /admin/settings only with settings:read and shows the link only then', async () => {
      auth.me.set(ME);
      auth.hasPermission.and.returnValue(false);

      await navigate('/admin/settings');

      expect(router.url).toBe('/');
      expect(root().querySelector('#nav-settings')).toBeNull();
      http.expectOne('/api/v1/health');

      auth.hasPermission.and.callFake((permission) => permission === 'settings:read');
      await navigate('/admin/settings');

      expect(router.url).toBe('/admin/settings');
      expect(text(root(), 'h2')).toBe('Einstellungen');
      expect(root().querySelector('#nav-settings')).not.toBeNull();
      http.expectOne('/api/v1/settings').flush([]);
    });

    it('redirects anonymous visitors from /admin/settings to the login', async () => {
      await navigate('/admin/settings');

      expect(router.url).toBe('/login?returnUrl=%2Fadmin%2Fsettings');
    });

    it('opens /account for signed-in users', async () => {
      auth.me.set(ME);

      await navigate('/account');

      expect(router.url).toBe('/account');
      expect(text(root(), '#account-email')).toBe('anna@example.org');
    });

    it('serves the account pages without login', async () => {
      for (const [url, heading] of [
        ['/register', 'Konto anlegen'],
        ['/login', 'Anmelden'],
        ['/forgot-password', 'Passwort vergessen'],
        ['/reset-password', 'Neues Passwort vergeben'],
      ]) {
        await navigate(url);

        expect(router.url).toBe(url);
        expect(text(root(), 'h2')).toBe(heading);
      }
    });

    it('sends unknown paths to the start page', async () => {
      await navigate('/does-not-exist');

      expect(router.url).toBe('/');
      http.expectOne('/api/v1/health');
    });
  });
});

describe('appConfig', () => {
  it('provides HTTP client with interceptor, session restore and the router', () => {
    expect(appConfig.providers.length).toBe(4);
  });
});
