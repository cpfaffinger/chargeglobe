import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import {
  ActivatedRouteSnapshot,
  Router,
  RouterStateSnapshot,
  UrlTree,
  provideRouter,
} from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { AuthService } from '../auth/auth.service';
import { SetupPage } from '../pages/setup.page';
import { Setting } from '../settings/settings.service';
import { Viewport } from '../design/theme.service';
import { ME, fakeAuth, fakeViewport, flush, submit, text, type, validationError } from '../testing';
import { SetupService, loadSetupState, requireSetupDone, setupAccess } from './setup.service';

@Component({ template: 'target' })
class TargetPage {}

function setting(key: string, overrides: Partial<Setting> = {}): Setting {
  return {
    key,
    type: 'string',
    description: `Description of ${key}.`,
    default: null,
    allowedValues: [],
    min: null,
    max: null,
    scopeKinds: ['global'],
    scopes: ['global'],
    value: null,
    stored: [],
    ...overrides,
  };
}

const provider = (key: string, type: Setting['type'] = 'string') =>
  setting(key, { type, scopeKinds: ['provider'], scopes: ['provider:econtrol'] });

const SETTINGS: Setting[] = [
  setting('map.style_url'),
  provider('provider.enabled', 'bool'),
  provider('provider.api_key', 'secret'),
  provider('provider.referer'),
  setting('mail.enabled', { type: 'bool' }),
  setting('mail.smtp.host'),
  setting('mail.smtp.port', { type: 'int' }),
  setting('mail.from.address'),
];

describe('SetupService', () => {
  let http: HttpTestingController;
  let setup: SetupService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    setup = TestBed.inject(SetupService);
  });

  afterEach(() => http.verify());

  it('reads the setup state before the first navigation and forgets it once the administrator exists', async () => {
    const loaded = TestBed.runInInjectionContext(loadSetupState);
    http.expectOne('/api/v1/setup').flush({ required: true });
    await loaded;
    expect(setup.required()).toBeTrue();

    setup.done();

    expect(setup.required()).toBeFalse();
  });

  it('treats an unreachable API as set up, so that the map stays usable', async () => {
    const loaded = setup.load();
    http
      .expectOne('/api/v1/setup')
      .flush('Bad Gateway', { status: 502, statusText: 'Bad Gateway' });
    await loaded;

    expect(setup.required()).toBeFalse();
  });
});

describe('setup guards', () => {
  let auth: ReturnType<typeof fakeAuth>;
  let http: HttpTestingController;

  const state = { url: '/setup' } as RouterStateSnapshot;
  const route = {} as ActivatedRouteSnapshot;
  const url = (result: unknown) => TestBed.inject(Router).serializeUrl(result as UrlTree);

  async function required(value: boolean): Promise<void> {
    const loaded = TestBed.inject(SetupService).load();
    http.expectOne('/api/v1/setup').flush({ required: value });
    await loaded;
  }

  beforeEach(() => {
    auth = fakeAuth();
    auth.hasPermission.and.callFake(
      (permission) => auth.me()?.permissions.includes(permission) ?? false,
    );
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: AuthService, useValue: auth },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  it('sends every page to the wizard while the instance is not set up', async () => {
    await required(true);
    expect(url(TestBed.runInInjectionContext(() => requireSetupDone(route, state)))).toBe('/setup');

    await required(false);
    expect(TestBed.runInInjectionContext(() => requireSetupDone(route, state))).toBeTrue();
  });

  it('opens the wizard to everybody before the setup and to settings:manage afterwards', async () => {
    const access = () => TestBed.runInInjectionContext(() => setupAccess(route, state));
    await required(true);
    expect(access()).toBeTrue();

    await required(false);
    expect(url(access())).toBe('/login?returnUrl=%2Fsetup');
    auth.me.set({ ...ME, permissions: ['evse:read'] });
    expect(url(access())).toBe('/');
    auth.me.set({ ...ME, permissions: ['settings:manage'] });
    expect(access()).toBeTrue();
  });
});

describe('AuthService.createInitialAdmin', () => {
  it('creates the first administrator and signs it in', async () => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const http = TestBed.inject(HttpTestingController);
    const auth = TestBed.inject(AuthService);

    const created = auth.createInitialAdmin('a1b2', 'owner@example.org', 'secret password');
    const request = http.expectOne('/api/v1/setup/admin');
    expect(request.request.body).toEqual({
      setupToken: 'a1b2',
      email: 'owner@example.org',
      password: 'secret password',
    });
    request.flush({ accessToken: 'token-1' });
    await flush();
    http.expectOne('/api/v1/me').flush(ME);
    await created;

    expect(auth.accessToken()).toBe('token-1');
    expect(auth.isAuthenticated()).toBeTrue();
    http.verify();
  });
});

describe('SetupPage', () => {
  let auth: ReturnType<typeof fakeAuth>;
  let http: HttpTestingController;
  let harness: RouterTestingHarness;
  let viewport: ReturnType<typeof fakeViewport>;

  const page = () => harness.routeNativeElement!;
  const current = () => text(page(), '.mat-step-header:has(.mat-step-icon-selected) .mat-step-text-label');
  const rowKeys = (step: string) =>
    Array.from(page().querySelectorAll(`#${step} app-setting-row h3`)).map((heading) =>
      heading.textContent!.replace(/\s+/g, ' ').trim(),
    );

  async function settle(): Promise<void> {
    await flush();
    await harness.fixture.whenStable();
  }

  async function open(setupRequired: boolean): Promise<void> {
    const loaded = TestBed.inject(SetupService).load();
    http.expectOne('/api/v1/setup').flush({ required: setupRequired });
    await loaded;
    harness = await RouterTestingHarness.create('/setup');
    await settle();
  }

  function fillAdminForm(repeat = 'secret password'): void {
    type(page(), 'input[name=setupToken]', ' a1b2 ');
    type(page(), 'input[type=email]', ' owner@example.org ');
    type(page(), 'input[name=password]', 'secret password');
    type(page(), 'input[name=repeat]', repeat);
    submit(page());
  }

  async function click(selector: string): Promise<void> {
    page().querySelector<HTMLElement>(selector)!.click();
    await settle();
  }

  beforeEach(() => {
    auth = fakeAuth();
    viewport = fakeViewport();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: auth },
        { provide: Viewport, useValue: viewport },
        provideRouter([
          { path: 'setup', component: SetupPage },
          { path: '', component: TargetPage },
          { path: 'admin/settings', component: TargetPage },
          { path: 'admin/mail', component: TargetPage },
        ]),
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('creates the administrator, then walks through data source and e-mail to the end', async () => {
    auth.createInitialAdmin.and.resolveTo();
    await open(true);
    expect(page().querySelector('.mat-stepper-horizontal')).not.toBeNull();
    expect(current()).toBe('Administrator');
    expect(text(page(), '.hint')).toContain('docker compose logs api');

    // The linear stepper does not open the next step before the administrator exists.
    page().querySelectorAll<HTMLElement>('.mat-step-header')[1].click();
    await settle();
    expect(current()).toBe('Administrator');

    fillAdminForm();
    await settle();

    expect(auth.createInitialAdmin).toHaveBeenCalledOnceWith('a1b2', 'owner@example.org', 'secret password');
    expect(TestBed.inject(SetupService).required()).toBeFalse();
    expect(current()).toBe('Datenquelle');
    expect(page().querySelector('#source-step mat-progress-bar')).not.toBeNull();
    http.expectOne('/api/v1/settings').flush(SETTINGS);
    await settle();
    expect(rowKeys('source-step')).toEqual([
      'provider.api_key provider:econtrol',
      'provider.referer provider:econtrol',
      'provider.enabled provider:econtrol',
    ]);

    // A saved row replaces its setting in the list of the page.
    type(page(), '#source-step app-setting-row:nth-of-type(2) input', 'example.org');
    await settle();
    await click('#source-step app-setting-row:nth-of-type(2) .save');
    const put = http.expectOne('/api/v1/settings/provider.referer');
    expect(put.request.body).toEqual({ value: 'example.org', scope: 'provider:econtrol' });
    put.flush({
      ...provider('provider.referer'),
      stored: [{ scope: 'provider:econtrol', value: 'example.org', updatedAt: '2026-10-14T08:00:00Z', updatedBy: ME.id }],
    });
    await settle();
    expect(text(page(), '#source-step app-setting-row:nth-of-type(2) .state')).toContain('Eigener Wert gespeichert');

    await click('#next-source');
    expect(current()).toBe('E-Mail');
    expect(rowKeys('mail-step')).toEqual(['mail.smtp.host', 'mail.smtp.port', 'mail.from.address', 'mail.enabled']);
    expect(page().querySelector('#mail-step a[href="/admin/mail"]')).not.toBeNull();

    await click('#back-mail');
    expect(current()).toBe('Datenquelle');
    await click('#next-source');
    await click('#next-mail');

    expect(current()).toBe('Fertig');
    expect(text(page(), '#setup-done')).toContain('Die Einrichtung ist abgeschlossen');
    await click('#to-map');
    expect(TestBed.inject(Router).url).toBe('/');
  });

  it('checks the form and the repeated password and shows the errors of the API', async () => {
    auth.createInitialAdmin.and.rejectWith(validationError('Does not match the setup token in the API log.'));
    await open(true);

    submit(page());
    await settle();
    expect(auth.createInitialAdmin).not.toHaveBeenCalled();
    expect(page().querySelectorAll('mat-error').length).toBe(4);

    fillAdminForm('another password');
    await settle();
    expect(text(page(), '.error')).toBe('Die beiden Passwörter stimmen nicht überein.');
    expect(auth.createInitialAdmin).not.toHaveBeenCalled();

    fillAdminForm();
    await settle();
    expect(text(page(), '.error')).toBe('Does not match the setup token in the API log.');
    expect(current()).toBe('Administrator');
    expect(TestBed.inject(SetupService).required()).toBeTrue();
  });

  it('starts at the data source on an instance that is set up already, vertical on small screens', async () => {
    viewport.mobile.set(true);
    await open(false);

    expect(page().querySelector('.mat-stepper-vertical')).not.toBeNull();
    expect(current()).toBe('Datenquelle');
    http
      .expectOne('/api/v1/settings')
      .flush({ title: 'Forbidden', detail: 'Missing permission: settings:read.' }, { status: 403, statusText: 'Forbidden' });
    await settle();
    expect(text(page(), '#source-step .error')).toBe('Missing permission: settings:read.');
  });
});
