import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { AdminRolesPage } from '../pages/admin-roles.page';
import { AdminUsersPage, SEARCH_DEBOUNCE_MS } from '../pages/admin-users.page';
import { ME, fakeAuth, flush, submit, text, type } from '../testing';
import { AdminService, ApiKeyInfo, RoleSummary, UserPage, UserSummary } from './admin.service';

function user(overrides: Partial<UserSummary> = {}): UserSummary {
  return {
    id: 'u-1',
    email: 'bob@example.org',
    state: 'ACTIVE',
    emailConfirmed: true,
    createdAt: '2026-10-14T08:15:00Z',
    approvedAt: '2026-10-14T08:15:00Z',
    disabledAt: null,
    roles: ['User'],
    extraPermissions: [],
    ...overrides,
  };
}

const pageOf = (data: UserSummary[], total = data.length): UserPage => ({ data, meta: { total, page: 1, pageSize: 25 } });

function role(overrides: Partial<RoleSummary> = {}): RoleSummary {
  return { name: 'User', permissions: ['evse:read'], users: 3, fullCatalog: false, default: true, ...overrides };
}

function apiKey(overrides: Partial<ApiKeyInfo> = {}): ApiKeyInfo {
  return {
    id: 'k-1',
    name: 'Import',
    prefix: '3fa9c2d1',
    permissions: ['evse:read', 'price:read'],
    createdAt: '2026-10-14T08:15:00Z',
    expiresAt: null,
    lastUsedAt: null,
    revokedAt: null,
    ...overrides,
  };
}

const conflict = (detail: string) => new HttpErrorResponse({ status: 409, error: { title: 'Conflict', detail } });

describe('AdminService', () => {
  let service: AdminService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(AdminService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('calls the user endpoints', async () => {
    const all = service.users('', 2);
    http.expectOne('/api/v1/users?page=2&pageSize=25').flush(pageOf([]));
    expect((await all).data).toEqual([]);

    const found = service.users('bob', 1);
    http.expectOne('/api/v1/users?page=1&pageSize=25&q=bob').flush(pageOf([user()]));
    expect((await found).data.length).toBe(1);

    const roles = service.setRoles('u-1', ['User']);
    const rolesRequest = http.expectOne('/api/v1/users/u-1/roles');
    expect([rolesRequest.request.method, rolesRequest.request.body]).toEqual(['PUT', { roles: ['User'] }]);
    rolesRequest.flush({ data: user() });
    expect((await roles).id).toBe('u-1');

    const approved = service.setApproved('u-1', true);
    const approvalRequest = http.expectOne('/api/v1/users/u-1/approval');
    expect([approvalRequest.request.method, approvalRequest.request.body]).toEqual(['PUT', { approved: true }]);
    approvalRequest.flush({ data: user() });
    expect((await approved).state).toBe('ACTIVE');
  });

  it('calls the remaining user and role endpoints', async () => {
    const enabled = service.setEnabled('u-1', false);
    const enabledRequest = http.expectOne('/api/v1/users/u-1/enabled');
    expect(enabledRequest.request.body).toEqual({ enabled: false });
    enabledRequest.flush({ data: user({ state: 'DISABLED' }) });
    expect((await enabled).state).toBe('DISABLED');

    const mail = service.sendConfirmation('u-1');
    const mailRequest = http.expectOne('/api/v1/users/u-1/confirmation-mail');
    expect(mailRequest.request.method).toBe('POST');
    mailRequest.flush({ message: 'queued' });
    await mail;

    const roles = service.roles();
    http.expectOne('/api/v1/roles').flush({ data: [role()] });
    expect((await roles)[0].name).toBe('User');

    const put = service.putRole('Support Team', ['evse:read']);
    const putRequest = http.expectOne('/api/v1/roles/Support%20Team');
    expect([putRequest.request.method, putRequest.request.body]).toEqual(['PUT', { permissions: ['evse:read'] }]);
    putRequest.flush({ data: role({ name: 'Support Team' }) });
    expect((await put).name).toBe('Support Team');

    const removed = service.deleteRole('Support Team');
    const deleteRequest = http.expectOne('/api/v1/roles/Support%20Team');
    expect(deleteRequest.request.method).toBe('DELETE');
    deleteRequest.flush(null);
    await removed;

    const permissions = service.permissions();
    http.expectOne('/api/v1/permissions').flush([{ key: 'evse:read', description: 'Read' }]);
    expect((await permissions)[0].key).toBe('evse:read');
  });

  it('sets the direct permissions and deletes an account', async () => {
    const set = service.setPermissions('u-1', ['jobs:read']);
    const setRequest = http.expectOne('/api/v1/users/u-1/permissions');
    expect([setRequest.request.method, setRequest.request.body]).toEqual(['PUT', { permissions: ['jobs:read'] }]);
    setRequest.flush({ data: user({ extraPermissions: ['jobs:read'] }) });
    expect((await set).extraPermissions).toEqual(['jobs:read']);

    const deleted = service.deleteUser('u-1');
    const deleteRequest = http.expectOne('/api/v1/users/u-1');
    expect(deleteRequest.request.method).toBe('DELETE');
    deleteRequest.flush(null);
    await deleted;
  });

  it('calls the API key and audit endpoints', async () => {
    const keys = service.apiKeys('u-1');
    http.expectOne('/api/v1/users/u-1/api-keys').flush([apiKey()]);
    expect((await keys)[0].prefix).toBe('3fa9c2d1');

    const revoked = service.revokeApiKey('u-1', 'k-1');
    const revokeRequest = http.expectOne('/api/v1/users/u-1/api-keys/k-1');
    expect(revokeRequest.request.method).toBe('DELETE');
    revokeRequest.flush(null);
    await revoked;

    const first = service.auditLog({ actor: '', action: 'apikey.', entity: '', from: '', to: '' }, null);
    http.expectOne('/api/v1/audit-log?limit=100&action=apikey.').flush({ data: [], meta: { returned: 0, limit: 100, nextCursor: null } });
    expect((await first).data).toEqual([]);

    const older = service.auditLog({ actor: 'bob', action: '', entity: 'api_key:', from: '2026-10-15T22:00:00.000Z', to: '' }, '42');
    http
      .expectOne('/api/v1/audit-log?limit=100&actor=bob&entity=api_key:&from=2026-10-15T22:00:00.000Z&cursor=42')
      .flush({ data: [], meta: { returned: 0, limit: 100, nextCursor: null } });
    await older;
  });
});

describe('AdminUsersPage', () => {
  let service: jasmine.SpyObj<AdminService>;
  let auth: ReturnType<typeof fakeAuth>;
  let snackBar: jasmine.SpyObj<MatSnackBar>;
  let fixture: ComponentFixture<AdminUsersPage>;

  const root = () => fixture.nativeElement as HTMLElement;
  const rows = () => Array.from(root().querySelectorAll('#users tbody tr.mat-mdc-row:not(.no-data)'));

  async function settle(): Promise<void> {
    await flush();
    await fixture.whenStable();
  }

  async function render(query: string | null = null): Promise<void> {
    TestBed.overrideProvider(ActivatedRoute, { useValue: { snapshot: { queryParamMap: new Map(query ? [['q', query]] : []) } } });
    fixture = TestBed.createComponent(AdminUsersPage);
    await settle();
  }

  beforeEach(() => {
    service = jasmine.createSpyObj<AdminService>('AdminService', [
      'users',
      'roles',
      'setRoles',
      'setApproved',
      'setEnabled',
      'sendConfirmation',
      'apiKeys',
      'revokeApiKey',
      'permissions',
      'setPermissions',
      'deleteUser',
    ]);
    service.permissions.and.resolveTo([
      { key: 'jobs:read', description: 'Jobs' },
      { key: 'audit:read', description: 'Audit' },
    ]);
    service.users.and.resolveTo(pageOf([]));
    service.roles.and.resolveTo([role({ name: 'Admin', fullCatalog: true }), role()]);
    auth = fakeAuth(ME);
    auth.hasPermission.and.returnValue(true);
    snackBar = jasmine.createSpyObj<MatSnackBar>('MatSnackBar', ['open']);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: AdminService, useValue: service },
        { provide: AuthService, useValue: auth },
        { provide: MatSnackBar, useValue: snackBar },
      ],
    });
  });

  it('lists the accounts with state, roles and the actions of their state', async () => {
    service.users.and.resolveTo(
      pageOf([
        user({ id: 'a', state: 'PENDING_APPROVAL', approvedAt: null }),
        user({ id: 'b', state: 'PENDING_CONFIRMATION', emailConfirmed: false }),
        user({ id: 'c', state: 'DISABLED' }),
        user({ id: ME.id, email: ME.email }),
      ]),
    );
    await render('bob');

    expect(service.users).toHaveBeenCalledWith('bob', 1);
    expect((root().querySelector('#user-search') as HTMLInputElement).value).toBe('bob');
    expect(rows().map((row) => text(row, '.account-state'))).toEqual(['wartet auf Freischaltung', 'E-Mail unbestätigt', 'gesperrt', 'aktiv']);
    expect(rows()[0].querySelector('.approve')).not.toBeNull();
    expect(rows()[1].querySelector('.confirm-mail')).not.toBeNull();
    expect(rows()[2].querySelector('.enable')).not.toBeNull();
    // Nobody disables their own account.
    expect(rows()[3].querySelector('.disable')).toBeNull();
    expect(rows()[0].querySelector('.disable')).not.toBeNull();
    // Roles and direct permissions per account.
    expect(root().querySelectorAll('mat-select').length).toBe(8);
    expect(text(root(), '.mat-mdc-paginator-range-label')).toBe('Seite 1 von 1 (4 Einträge)');
  });

  it('shows the roles as text and no actions without user:manage', async () => {
    auth.hasPermission.and.callFake((permission) => permission === 'user:read');
    service.users.and.resolveTo(pageOf([user({ roles: ['Maintainer', 'User'] })]));
    await render();

    expect(service.roles).not.toHaveBeenCalled();
    expect(text(rows()[0], '.roles')).toBe('Maintainer, User');
    expect(rows()[0].querySelector('button:not(.show-keys)')).toBeNull();
    expect(service.permissions).not.toHaveBeenCalled();
    expect(text(rows()[0], '.extra')).toBe('–');

    // The keys of the account are visible, but cannot be revoked.
    service.apiKeys.and.resolveTo([apiKey(), apiKey({ id: 'k-2', revokedAt: '2026-10-16T19:00:00Z' })]);
    rows()[0].querySelector<HTMLButtonElement>('.show-keys')!.click();
    await settle();
    const keys = Array.from(root().querySelectorAll('#user-keys tbody tr'));
    expect(text(keys[0], 'td:last-child')).toBe('gültig');
    expect(text(keys[1], 'td:last-child')).toMatch(/^widerrufen 16\.10\.2026 \d{2}:00$/);
    expect(root().querySelector('#user-keys .revoke')).toBeNull();
  });

  it('grants direct permissions and deletes an account after a confirmation', async () => {
    service.users.and.resolveTo(pageOf([user({ id: 'a' }), user({ id: ME.id, email: ME.email })]));
    service.setPermissions.and.resolveTo(user({ id: 'a', extraPermissions: ['jobs:read'] }));
    service.apiKeys.and.resolveTo([]);
    service.deleteUser.and.resolveTo();
    await render();

    expect(rows()[1].querySelector('.delete')).toBeNull();
    rows()[0].querySelector<HTMLElement>('.extra .mat-mdc-select-trigger')!.click();
    await settle();
    const options = Array.from(document.querySelectorAll<HTMLElement>('mat-option'));
    expect(options.map((option) => option.textContent!.trim())).toEqual(['jobs:read', 'audit:read']);
    options[0].click();
    await settle();
    expect(service.setPermissions).toHaveBeenCalledWith('a', ['jobs:read']);
    expect(snackBar.open).toHaveBeenCalledWith('Zusatzrechte von bob@example.org gespeichert.', 'OK', { duration: 4000 });

    rows()[0].querySelector<HTMLButtonElement>('.show-keys')!.click();
    await settle();
    const confirm = spyOn(window, 'confirm').and.returnValue(false);
    rows()[0].querySelector<HTMLButtonElement>('.delete')!.click();
    await settle();
    expect(service.deleteUser).not.toHaveBeenCalled();

    confirm.and.returnValue(true);
    rows()[0].querySelector<HTMLButtonElement>('.delete')!.click();
    await settle();
    expect(service.deleteUser).toHaveBeenCalledWith('a');
    expect(snackBar.open).toHaveBeenCalledWith('bob@example.org ist gelöscht.', 'OK', { duration: 4000 });
    expect(root().querySelector('#user-keys')).toBeNull();
    expect(service.users).toHaveBeenCalledTimes(2);

    // Deleting an account whose keys are not open keeps the open list.
    service.users.and.resolveTo(pageOf([user({ id: 'b' }), user({ id: 'c', email: 'carl@example.org' })]));
    root().querySelector<HTMLButtonElement>('#reload')!.click();
    await settle();
    rows()[1].querySelector<HTMLButtonElement>('.show-keys')!.click();
    await settle();
    rows()[0].querySelector<HTMLButtonElement>('.delete')!.click();
    await settle();
    expect(text(root(), '#user-keys h2')).toBe('API-Keys von carl@example.org');
  });

  it('lists the API keys of an account without their values and revokes one', async () => {
    service.users.and.resolveTo(pageOf([user({ id: 'a' })]));
    service.apiKeys.and.resolveTo([apiKey({ expiresAt: '2026-12-31T23:00:00Z', lastUsedAt: '2026-10-16T18:00:00Z' })]);
    service.revokeApiKey.and.resolveTo();
    await render();

    rows()[0].querySelector<HTMLButtonElement>('.show-keys')!.click();
    await settle();
    expect(service.apiKeys).toHaveBeenCalledWith('a');
    expect(text(root(), '#user-keys h2')).toBe('API-Keys von bob@example.org');
    const cells = Array.from(root().querySelectorAll('#user-keys tbody tr td')).map((cell) => cell.textContent!.trim());
    expect(cells.slice(0, 3)).toEqual(['Import', '3fa9c2d1', 'evse:read, price:read']);
    expect(cells[4]).toMatch(/^(31\.12\.2026|01\.01\.2027) \d{2}:00$/);
    expect(cells[5]).toMatch(/^16\.10\.2026 \d{2}:00$/);

    service.apiKeys.and.resolveTo([apiKey({ revokedAt: '2026-10-16T19:00:00Z' })]);
    root().querySelector<HTMLButtonElement>('#user-keys .revoke')!.click();
    await settle();
    expect(service.revokeApiKey).toHaveBeenCalledWith('a', 'k-1');
    expect(snackBar.open).toHaveBeenCalledWith('API-Key „Import“ von bob@example.org ist widerrufen.', 'OK', { duration: 4000 });
    expect(root().querySelector('#user-keys .revoke')).toBeNull();

    service.apiKeys.and.resolveTo([]);
    rows()[0].querySelector<HTMLButtonElement>('.show-keys')!.click();
    await settle();
    expect(text(root(), '#keys-empty')).toBe('Dieses Konto hat keine API-Keys.');
    expect(Array.from(root().querySelectorAll('#user-keys tbody tr td')).length).toBe(1);
  });

  it('approves, disables, enables, assigns roles and sends the confirmation mail', async () => {
    service.users.and.resolveTo(pageOf([user({ id: 'a', state: 'PENDING_APPROVAL' }), user({ id: 'b', state: 'PENDING_CONFIRMATION' })]));
    service.setApproved.and.resolveTo(user({ id: 'a' }));
    service.setEnabled.and.resolveTo(user({ id: 'b', state: 'DISABLED' }));
    service.setRoles.and.resolveTo(user({ id: 'a', roles: ['Admin'] }));
    service.sendConfirmation.and.resolveTo();
    await render();

    rows()[0].querySelector<HTMLButtonElement>('.approve')!.click();
    await settle();
    expect(service.setApproved).toHaveBeenCalledWith('a', true);
    expect(text(rows()[0], '.account-state')).toBe('aktiv');
    expect(snackBar.open).toHaveBeenCalledWith('bob@example.org ist freigeschaltet.', 'OK', { duration: 4000 });

    rows()[1].querySelector<HTMLButtonElement>('.confirm-mail')!.click();
    await settle();
    expect(service.sendConfirmation).toHaveBeenCalledWith('b');

    rows()[1].querySelector<HTMLButtonElement>('.disable')!.click();
    await settle();
    expect(service.setEnabled).toHaveBeenCalledWith('b', false);
    expect(snackBar.open).toHaveBeenCalledWith('bob@example.org ist gesperrt.', 'OK', { duration: 4000 });

    service.setEnabled.and.resolveTo(user({ id: 'b' }));
    rows()[1].querySelector<HTMLButtonElement>('.enable')!.click();
    await settle();
    expect(service.setEnabled).toHaveBeenCalledWith('b', true);
    expect(snackBar.open).toHaveBeenCalledWith('bob@example.org ist entsperrt.', 'OK', { duration: 4000 });

    rows()[0].querySelector<HTMLElement>('.mat-mdc-select-trigger')!.click();
    await settle();
    document.querySelectorAll<HTMLElement>('mat-option')[0].click();
    await settle();
    expect(service.setRoles).toHaveBeenCalledWith('a', ['Admin', 'User']);
    expect(text(rows()[0], '.email')).toBe('bob@example.org');
  });

  it('reloads the list after a refused change and keeps the reason', async () => {
    service.users.and.resolveTo(pageOf([user({ id: 'a' })]));
    service.setEnabled.and.rejectWith(conflict('You cannot disable your own account.'));
    await render();

    rows()[0].querySelector<HTMLButtonElement>('.disable')!.click();
    await settle();

    expect(service.users).toHaveBeenCalledTimes(2);
    expect(text(root(), '.error')).toBe('You cannot disable your own account.');
  });

  it('searches after a pause in typing, pages and reloads on demand', async () => {
    service.users.and.resolveTo(pageOf([user()], 60));
    await render();
    jasmine.clock().install();
    try {
      type(root(), '#user-search', ' anna ');
      type(root(), '#user-search', ' anna@ ');
      jasmine.clock().tick(SEARCH_DEBOUNCE_MS);
    } finally {
      jasmine.clock().uninstall();
    }
    await settle();
    expect(service.users.calls.allArgs()).toEqual([['', 1], ['anna@', 1]]);

    root().querySelector<HTMLButtonElement>('.mat-mdc-paginator-navigation-next')!.click();
    await settle();
    expect(service.users).toHaveBeenCalledWith('anna@', 2);

    root().querySelector<HTMLButtonElement>('#reload')!.click();
    await settle();
    expect(service.users).toHaveBeenCalledTimes(4);
  });

  it('shows an empty list and stops the search timer when left', async () => {
    await render();

    expect(text(root(), '#users-empty')).toBe('Keine Konten gefunden.');
    type(root(), '#user-search', 'x');
    fixture.destroy();
    await new Promise((resolve) => setTimeout(resolve, SEARCH_DEBOUNCE_MS + 50));
    expect(service.users).toHaveBeenCalledTimes(1);
  });
});

describe('AdminRolesPage', () => {
  let service: jasmine.SpyObj<AdminService>;
  let auth: ReturnType<typeof fakeAuth>;
  let snackBar: jasmine.SpyObj<MatSnackBar>;
  let fixture: ComponentFixture<AdminRolesPage>;

  const root = () => fixture.nativeElement as HTMLElement;
  const box = (roleName: string, permission: string) =>
    root().querySelector<HTMLInputElement>(`mat-checkbox[data-role="${roleName}"][data-permission="${permission}"] input`)!;
  const column = (roleName: string) => root().querySelector(`[id="role-${roleName}"]`)!;

  async function settle(): Promise<void> {
    await flush();
    await fixture.whenStable();
  }

  async function render(): Promise<void> {
    fixture = TestBed.createComponent(AdminRolesPage);
    await settle();
  }

  beforeEach(() => {
    service = jasmine.createSpyObj<AdminService>('AdminService', ['roles', 'permissions', 'putRole', 'deleteRole']);
    service.permissions.and.resolveTo([
      { key: 'evse:read', description: 'Read charge points.' },
      { key: 'jobs:read', description: 'Inspect crawlers.' },
    ]);
    service.roles.and.resolveTo([
      role({ name: 'Admin', permissions: ['evse:read', 'jobs:read'], users: 1, fullCatalog: true }),
      role({ name: 'Support', permissions: ['evse:read'], users: 0, default: false }),
      role({ name: 'User', users: 1 }),
    ]);
    service.putRole.and.resolveTo(role());
    service.deleteRole.and.resolveTo();
    auth = fakeAuth(ME);
    auth.hasPermission.and.returnValue(true);
    snackBar = jasmine.createSpyObj<MatSnackBar>('MatSnackBar', ['open']);
    TestBed.configureTestingModule({
      providers: [{ provide: AdminService, useValue: service }, { provide: AuthService, useValue: auth }, { provide: MatSnackBar, useValue: snackBar }],
    });
  });

  it('shows the matrix of roles and permissions; Admin is fixed, only empty custom roles can be deleted', async () => {
    await render();

    expect(Array.from(root().querySelectorAll('.role-name')).map((name) => name.textContent)).toEqual(['Admin', 'Support', 'User']);
    expect(text(column('Admin'), '.muted')).toBe('1 Konto');
    expect(text(column('Support'), '.muted')).toBe('0 Konten');
    expect(box('Admin', 'jobs:read').checked).toBeTrue();
    expect(box('Admin', 'jobs:read').disabled).toBeTrue();
    expect(box('Support', 'jobs:read').checked).toBeFalse();
    expect(column('Admin').querySelector('.save')).toBeNull();
    expect(column('Support').querySelector('.delete')).not.toBeNull();
    expect(column('User').querySelector('.delete')).toBeNull();
    expect(column('Support').querySelector<HTMLButtonElement>('.save')!.disabled).toBeTrue();
  });

  it('saves the edited permissions of a role and keeps the drafts of the others', async () => {
    await render();

    box('Support', 'jobs:read').click();
    box('User', 'jobs:read').click();
    await settle();
    box('Support', 'jobs:read').click();
    box('Support', 'jobs:read').click();
    await settle();
    column('Support').querySelector<HTMLButtonElement>('.save')!.click();
    await settle();

    expect(service.putRole).toHaveBeenCalledOnceWith('Support', ['evse:read', 'jobs:read']);
    expect(snackBar.open).toHaveBeenCalledWith('Rolle Support gespeichert.', 'OK', { duration: 4000 });
    expect(box('User', 'jobs:read').checked).toBeTrue();
    expect(column('User').querySelector<HTMLButtonElement>('.save')!.disabled).toBeFalse();
  });

  it('creates and deletes roles and shows refusals', async () => {
    await render();

    submit(root());
    await settle();
    expect(service.putRole).not.toHaveBeenCalled();

    type(root(), '#new-role input', ' Support Team ');
    submit(root());
    await settle();
    expect(service.putRole).toHaveBeenCalledWith('Support Team', []);
    expect(snackBar.open).toHaveBeenCalledWith('Rolle Support Team angelegt.', 'OK', { duration: 4000 });

    column('Support').querySelector<HTMLButtonElement>('.delete')!.click();
    await settle();
    expect(service.deleteRole).toHaveBeenCalledWith('Support');
    expect(snackBar.open).toHaveBeenCalledWith('Rolle Support gelöscht.', 'OK', { duration: 4000 });

    service.deleteRole.and.rejectWith(conflict('The role still has accounts; assign them other roles first.'));
    column('Support').querySelector<HTMLButtonElement>('.delete')!.click();
    await settle();
    expect(text(root(), '.error')).toBe('The role still has accounts; assign them other roles first.');
  });

  it('shows the matrix read-only without role:manage', async () => {
    auth.hasPermission.and.returnValue(false);
    await render();

    expect(root().querySelector('#new-role')).toBeNull();
    expect(root().querySelector('.save')).toBeNull();
    expect(box('Support', 'evse:read').disabled).toBeTrue();
  });
});
