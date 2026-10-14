import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { App } from './app';
import { appConfig } from './app.config';
import { routes } from './app.routes';
import { AuthService } from './auth/auth.service';
import { BrowserCapabilities } from './map/browser-capabilities';
import { MapEngine } from './map/map-engine';
import { MAP_CONFIG, ME, fakeAuth, fakeBrowser, fakeMapEngine, flush, text } from './testing';

describe('App with its routes', () => {
  let auth: ReturnType<typeof fakeAuth>;
  let map: ReturnType<typeof fakeMapEngine>;
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
    map = fakeMapEngine();
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter(routes),
        { provide: AuthService, useValue: auth },
        { provide: MapEngine, useValue: map.engine },
        { provide: BrowserCapabilities, useValue: fakeBrowser() },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(App);
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
  });

  afterEach(() => http.verify());

  describe('start page', () => {
    it('is reachable anonymously and shows the map with the configuration of the API', async () => {
      await navigate('/');
      http.expectOne('/api/v1/map/config').flush(MAP_CONFIG);
      await flush();
      await fixture.whenStable();

      expect(router.url).toBe('/');
      expect(root().querySelector('app-home #map')).not.toBeNull();
      expect(map.engine.create).toHaveBeenCalledOnceWith(jasmine.objectContaining({ zoom: 7 }));
      expect(root().querySelector('#nav-login')).not.toBeNull();
      expect(root().querySelector('#nav-register')).not.toBeNull();
      expect(root().querySelector('#nav-account')).toBeNull();
      expect(root().querySelector('#logout')).toBeNull();
    });
  });

  describe('header', () => {
    it('shows the e-mail as link to the account and a logout button after login', async () => {
      auth.me.set(ME);
      await navigate('/');
      http.expectOne('/api/v1/map/config');

      expect(text(root(), '#nav-account')).toBe('anna@example.org');
      expect(root().querySelector('#nav-account')!.getAttribute('href')).toBe('/account');
      expect(root().querySelector('#nav-login')).toBeNull();
      expect(root().querySelector('#nav-register')).toBeNull();
    });

    it('logs out and returns to the map', async () => {
      auth.me.set(ME);
      auth.logout.and.callFake(async () => auth.me.set(null));
      await navigate('/account');
      http.expectOne('/api/v1/health');

      root().querySelector<HTMLButtonElement>('#logout')!.click();
      await flush();
      await fixture.whenStable();

      expect(auth.logout).toHaveBeenCalledTimes(1);
      expect(router.url).toBe('/');
      expect(root().querySelector('#nav-login')).not.toBeNull();
      http.expectOne('/api/v1/map/config');
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
      http.expectOne('/api/v1/map/config');

      auth.hasPermission.and.callFake((permission) => permission === 'settings:read');
      await navigate('/admin/settings');

      expect(router.url).toBe('/admin/settings');
      expect(text(root(), 'h2')).toBe('Einstellungen');
      expect(root().querySelector('#nav-settings')).not.toBeNull();
      http.expectOne('/api/v1/settings').flush([]);
    });

    it('opens /admin/mail only with settings:manage and shows the link only then', async () => {
      auth.me.set(ME);
      auth.hasPermission.and.callFake((permission) => permission === 'settings:read');

      await navigate('/admin/mail');

      expect(router.url).toBe('/');
      expect(root().querySelector('#nav-mail')).toBeNull();
      http.expectOne('/api/v1/map/config');

      auth.hasPermission.and.returnValue(true);
      await navigate('/admin/mail');

      expect(router.url).toBe('/admin/mail');
      expect(text(root(), 'h2')).toBe('E-Mail-Versand');
      expect(root().querySelector('#nav-mail')).not.toBeNull();
      http.expectOne('/api/v1/mail/outbox?page=1&pageSize=25').flush({ data: [], meta: { total: 0, page: 1, pageSize: 25 } });
    });

    it('redirects anonymous visitors from /admin/mail to the login', async () => {
      await navigate('/admin/mail');

      expect(router.url).toBe('/login?returnUrl=%2Fadmin%2Fmail');
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
      http.expectOne('/api/v1/health');
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
      http.expectOne('/api/v1/map/config');
    });
  });
});

describe('appConfig', () => {
  it('provides HTTP client with interceptor, session restore, setup state and the router', () => {
    expect(appConfig.providers.length).toBe(5);
  });
});
