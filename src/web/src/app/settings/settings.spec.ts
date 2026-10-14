import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideRouter } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { AdminSettingsPage } from '../pages/admin-settings.page';
import { ME, fakeAuth, flush, text, validationError } from '../testing';
import { SettingRow } from './setting-row';
import { Setting, SettingsService } from './settings.service';

function setting(overrides: Partial<Setting>): Setting {
  return {
    key: 'mail.smtp.host',
    type: 'string',
    description: 'Host name of the SMTP server.',
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

const stored = (value: unknown, scope = 'global') => [{ scope, value, updatedAt: '2026-10-14T08:15:00Z', updatedBy: ME.id }];

/** Provider-scoped settings of a source adapter (stored per "provider:<key>"). */
const providerSetting = (overrides: Partial<Setting>): Setting =>
  setting({ scopeKinds: ['provider'], scopes: ['provider:econtrol'], ...overrides });

describe('SettingsService', () => {
  let service: SettingsService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(SettingsService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('lists the settings', async () => {
    const list = service.list();
    http.expectOne('/api/v1/settings').flush([setting({})]);

    expect(await list).toEqual([setting({})]);
  });

  it('stores a value for a scope with PUT', async () => {
    const updated = service.update('mail.smtp.port', 2525, 'global');
    const request = http.expectOne('/api/v1/settings/mail.smtp.port');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ value: 2525, scope: 'global' });
    request.flush(setting({ key: 'mail.smtp.port', value: 2525 }));

    expect((await updated).value).toBe(2525);
  });
});

describe('SettingRow', () => {
  let fixture: ComponentFixture<SettingRow>;
  let service: jasmine.SpyObj<SettingsService>;
  let emitted: Setting[];
  let snackBar: jasmine.Spy;

  const root = () => fixture.nativeElement as HTMLElement;
  const control = <T extends HTMLElement = HTMLInputElement>(selector = 'input') => root().querySelector<T>(selector)!;
  const toggle = () => root().querySelector<HTMLButtonElement>('button[role=switch]')!;
  const button = (name: 'save' | 'reset') => root().querySelector<HTMLButtonElement>(`button.${name}`)!;

  async function render(value: Setting, canManage = true, scope = 'global'): Promise<void> {
    fixture = TestBed.createComponent(SettingRow);
    fixture.componentRef.setInput('setting', value);
    fixture.componentRef.setInput('scope', scope);
    fixture.componentRef.setInput('canManage', canManage);
    emitted = [];
    fixture.componentInstance.changed.subscribe((updated) => emitted.push(updated));
    await fixture.whenStable();
  }

  async function settle(): Promise<void> {
    await flush();
    await fixture.whenStable();
  }

  /** Types into the input and lets the row re-render (enables the save button). */
  async function enter(value: string): Promise<void> {
    control().value = value;
    control().dispatchEvent(new Event('input'));
    await fixture.whenStable();
  }

  beforeEach(() => {
    service = jasmine.createSpyObj<SettingsService>('SettingsService', ['list', 'update']);
    TestBed.configureTestingModule({ providers: [{ provide: SettingsService, useValue: service }] });
    snackBar = spyOn(TestBed.inject(MatSnackBar), 'open');
  });

  it('renders a text input for a string and saves the edited value', async () => {
    const updated = setting({ value: 'smtp.example.org', stored: stored('smtp.example.org') });
    service.update.and.resolveTo(updated);
    await render(setting({}));

    expect(text(root(), 'h3')).toBe('mail.smtp.host');
    expect(text(root(), '.description')).toBe('Host name of the SMTP server.');
    expect(control().type).toBe('text');
    expect(control().value).toBe('');
    expect(text(root(), '.state')).toBe('Standardwert aktiv');
    expect(button('save').disabled).toBeTrue();
    expect(button('reset').disabled).toBeTrue();

    await enter('smtp.example.org');
    expect(button('save').disabled).toBeFalse();
    button('save').click();
    await settle();

    expect(service.update).toHaveBeenCalledOnceWith('mail.smtp.host', 'smtp.example.org', 'global');
    expect(emitted).toEqual([updated]);
    expect(snackBar).toHaveBeenCalledOnceWith('mail.smtp.host gespeichert', 'OK', { duration: 4000 });
    expect(button('save').disabled).toBeTrue();
  });

  it('renders a number input with bounds and sends numbers', async () => {
    service.update.and.resolveTo(setting({}));
    await render(setting({ key: 'mail.smtp.port', type: 'int', default: 587, value: 587, min: 1, max: 65535 }));

    expect(control().type).toBe('number');
    expect(control().value).toBe('587');
    expect(control().min).toBe('1');
    expect(control().max).toBe('65535');

    await enter('2525');
    button('save').click();
    await settle();
    expect(service.update).toHaveBeenCalledWith('mail.smtp.port', 2525, 'global');

    await enter('');
    button('save').click();
    await settle();
    expect(service.update).toHaveBeenCalledWith('mail.smtp.port', null, 'global');
  });

  it('renders a slide toggle for a bool', async () => {
    service.update.and.resolveTo(setting({}));
    await render(setting({ key: 'mail.enabled', type: 'bool', default: false, value: false }));

    expect(toggle().getAttribute('aria-checked')).toBe('false');
    expect(toggle().getAttribute('aria-label')).toBe('mail.enabled');

    toggle().click();
    await fixture.whenStable();
    button('save').click();
    await settle();

    expect(service.update).toHaveBeenCalledOnceWith('mail.enabled', true, 'global');
  });

  it('renders a select with the allowed values of an enum', async () => {
    service.update.and.resolveTo(setting({}));
    await render(
      setting({
        key: 'mail.smtp.security',
        type: 'enum',
        default: 'starttls',
        value: 'starttls',
        allowedValues: ['plain', 'starttls', 'ssl'],
      }),
    );
    const select = control<HTMLSelectElement>('select');

    expect(Array.from(select.options).map((option) => option.value)).toEqual(['plain', 'starttls', 'ssl']);
    expect(select.value).toBe('starttls');

    select.value = 'ssl';
    select.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    button('save').click();
    await settle();

    expect(service.update).toHaveBeenCalledOnceWith('mail.smtp.security', 'ssl', 'global');
  });

  it('never prefills a secret and only tells whether one is set', async () => {
    await render(setting({ key: 'mail.smtp.password', type: 'secret' }));
    expect(control().type).toBe('password');
    expect(control().value).toBe('');
    expect(control().placeholder).toBe('nicht gesetzt');

    await render(setting({ key: 'mail.smtp.password', type: 'secret', value: '********', stored: stored('********') }));
    expect(control().value).toBe('');
    expect(control().placeholder).toBe('gesetzt');
    expect(text(root(), '.state')).toBe('Eigener Wert gespeichert');
  });

  it('resets a stored value to the default', async () => {
    const reset = setting({ key: 'mail.smtp.port', type: 'int', default: 587, value: 587 });
    service.update.and.resolveTo(reset);
    await render(setting({ key: 'mail.smtp.port', type: 'int', default: 587, value: 2525, stored: stored(2525) }));

    expect(button('reset').disabled).toBeFalse();
    button('reset').click();
    await settle();

    expect(service.update).toHaveBeenCalledOnceWith('mail.smtp.port', null, 'global');
    expect(emitted).toEqual([reset]);
    expect(snackBar).toHaveBeenCalledOnceWith('mail.smtp.port auf den Standardwert zurückgesetzt', 'OK', { duration: 4000 });
  });

  it('shows the field errors of a rejected value and keeps the draft', async () => {
    service.update.and.rejectWith(validationError('Must be an integer between 1 and 65535.'));
    await render(setting({ key: 'mail.smtp.port', type: 'int', default: 587, value: 587 }));

    await enter('70000');
    button('save').click();
    await settle();

    expect(text(root(), '.error')).toBe('Must be an integer between 1 and 65535.');
    expect(control().value).toBe('70000');
    expect(snackBar).not.toHaveBeenCalled();
    expect(emitted).toEqual([]);
  });

  it('shows and stores the value of a provider scope', async () => {
    const enabled = providerSetting({ key: 'provider.enabled', type: 'bool', default: false, value: false, stored: stored(true, 'provider:econtrol') });
    service.update.and.resolveTo(enabled);
    await render(providerSetting({ key: 'provider.enabled', type: 'bool', default: false, value: false }), true, 'provider:econtrol');

    expect(text(root(), 'h3')).toBe('provider.enabled provider:econtrol');
    expect(toggle().getAttribute('aria-checked')).toBe('false');
    expect(text(root(), '.state')).toBe('Standardwert aktiv');

    toggle().click();
    await fixture.whenStable();
    button('save').click();
    await settle();

    expect(service.update).toHaveBeenCalledOnceWith('provider.enabled', true, 'provider:econtrol');
    expect(snackBar).toHaveBeenCalledOnceWith('provider.enabled (provider:econtrol) gespeichert', 'OK', { duration: 4000 });

    await render(enabled, true, 'provider:econtrol');
    expect(toggle().getAttribute('aria-checked')).toBe('true');
    expect(text(root(), '.state')).toBe('Eigener Wert gespeichert');
    expect(button('reset').disabled).toBeFalse();
  });

  it('tells per provider scope whether a secret is set', async () => {
    const apiKey = providerSetting({ key: 'provider.api_key', type: 'secret', stored: stored('********', 'provider:econtrol') });

    await render(apiKey, true, 'provider:econtrol');
    expect(control().value).toBe('');
    expect(control().placeholder).toBe('gesetzt');

    await render(apiKey, true, 'provider:other');
    expect(control().placeholder).toBe('nicht gesetzt');
    expect(button('reset').disabled).toBeTrue();
  });

  it('is read-only without settings:manage', async () => {
    await render(setting({ value: 'smtp.example.org', stored: stored('smtp.example.org') }), false);

    expect(control().disabled).toBeTrue();
    expect(control().value).toBe('smtp.example.org');
    expect(root().querySelector('button')).toBeNull();
  });
});

describe('AdminSettingsPage', () => {
  let auth: ReturnType<typeof fakeAuth>;
  let service: jasmine.SpyObj<SettingsService>;
  let fixture: ComponentFixture<AdminSettingsPage>;

  const root = () => fixture.nativeElement as HTMLElement;

  async function render(): Promise<void> {
    fixture = TestBed.createComponent(AdminSettingsPage);
    await flush();
    await fixture.whenStable();
  }

  beforeEach(() => {
    auth = fakeAuth(ME);
    service = jasmine.createSpyObj<SettingsService>('SettingsService', ['list', 'update']);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: auth },
        { provide: SettingsService, useValue: service },
      ],
    });
  });

  it('renders one row per setting and replaces a row after it was saved', async () => {
    auth.hasPermission.and.callFake((permission) => permission === 'settings:manage');
    service.list.and.resolveTo([
      setting({}),
      setting({ key: 'mail.smtp.port', type: 'int', default: 587, value: 2525, stored: stored(2525) }),
    ]);
    service.update.and.resolveTo(setting({ key: 'mail.smtp.port', type: 'int', default: 587, value: 587 }));
    await render();

    expect(Array.from(root().querySelectorAll('h3')).map((heading) => heading.textContent?.trim())).toEqual([
      'mail.smtp.host',
      'mail.smtp.port',
    ]);
    expect(root().querySelectorAll('button.save').length).toBe(2);
    expect(root().querySelector('#mail-link a')?.getAttribute('href')).toBe('/admin/mail');

    root().querySelectorAll<HTMLButtonElement>('button.reset')[1].click();
    await flush();
    await fixture.whenStable();

    expect(service.update).toHaveBeenCalledOnceWith('mail.smtp.port', null, 'global');
    expect(root().querySelectorAll<HTMLInputElement>('app-setting-row input')[1].value).toBe('587');
    expect(Array.from(root().querySelectorAll('.state')).map((state) => state.textContent?.trim().split(/\s+/)[0])).toEqual([
      'Standardwert',
      'Standardwert',
    ]);
  });

  it('renders one row per scope of a setting', async () => {
    auth.hasPermission.and.returnValue(true);
    service.list.and.resolveTo([providerSetting({ key: 'provider.referer', scopes: ['provider:econtrol', 'provider:other'] })]);
    await render();

    expect(Array.from(root().querySelectorAll('h3 .scope')).map((scope) => scope.textContent)).toEqual([
      'provider:econtrol',
      'provider:other',
    ]);
  });

  it('shows the settings read-only with settings:read only', async () => {
    auth.hasPermission.and.returnValue(false);
    service.list.and.resolveTo([setting({})]);
    await render();

    expect(root().querySelectorAll('app-setting-row').length).toBe(1);
    expect(root().querySelector('button')).toBeNull();
    expect(root().querySelector('#mail-link')).toBeNull();
  });

  it('groups the settings by key and filters them by key, scope or description', async () => {
    auth.hasPermission.and.returnValue(false);
    service.list.and.resolveTo([
      setting({}),
      setting({ key: 'map.style_url', description: 'URL of the base map in light mode.' }),
      providerSetting({ key: 'provider.referer', description: 'Referer sent with every request.' }),
      setting({ key: 'legacy.flag', description: 'Something without a group.' }),
    ]);
    await render();
    const titles = () => Array.from(root().querySelectorAll('mat-panel-title')).map((title) => title.textContent!.trim());
    const keys = () => Array.from(root().querySelectorAll('app-setting-row h3')).map((heading) => heading.firstChild!.textContent!.trim());
    const search = root().querySelector<HTMLInputElement>('#settings-search input')!;
    const find = async (term: string) => {
      search.value = term;
      search.dispatchEvent(new Event('input'));
      await fixture.whenStable();
    };

    expect(titles()).toEqual(['Datenquellen', 'E-Mail (SMTP)', 'Karte', 'Weitere']);
    expect(root().querySelectorAll('mat-expansion-panel.mat-expanded').length).toBe(1);

    await find('LIGHT MODE');
    expect(titles()).toEqual(['Karte']);
    expect(keys()).toEqual(['map.style_url']);
    expect(root().querySelectorAll('mat-expansion-panel.mat-expanded').length).toBe(1);

    await find('provider:econtrol');
    expect(keys()).toEqual(['provider.referer']);

    await find('gibt es nicht');
    expect(titles()).toEqual([]);
    expect(text(root(), '#no-match')).toBe('Keine Einstellung passt zu „gibt es nicht“.');
  });

  it('shows the error when the settings cannot be loaded', async () => {
    service.list.and.rejectWith(validationError('Missing permission: settings:read.'));
    await render();

    expect(text(root(), '.error')).toBe('Missing permission: settings:read.');
    expect(root().querySelector('app-setting-row')).toBeNull();
  });
});
