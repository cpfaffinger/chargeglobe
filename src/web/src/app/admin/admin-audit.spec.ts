import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AdminAuditPage, dayStart } from '../pages/admin-audit.page';
import { flush, submit, text, type } from '../testing';
import { AdminService, AuditEntry, AuditPage } from './admin.service';

function entry(overrides: Partial<AuditEntry> = {}): AuditEntry {
  return {
    id: 7,
    occurredAt: '2026-10-16T19:40:00Z',
    action: 'apikey.revoke',
    target: 'api_key:k-1',
    actor: { userId: 'u-1', email: 'admin@example.org', apiKeyId: null, apiKeyName: null },
    ...overrides,
  };
}

const page = (data: AuditEntry[], nextCursor: string | null = null): AuditPage => ({
  data,
  meta: { returned: data.length, limit: 100, nextCursor },
});

describe('AdminAuditPage', () => {
  let service: jasmine.SpyObj<AdminService>;
  let fixture: ComponentFixture<AdminAuditPage>;

  const root = () => fixture.nativeElement as HTMLElement;
  const rows = () => Array.from(root().querySelectorAll('#audit tbody tr'));

  async function render(): Promise<void> {
    fixture = TestBed.createComponent(AdminAuditPage);
    await settle();
  }

  async function settle(): Promise<void> {
    await flush();
    await fixture.whenStable();
  }

  beforeEach(() => {
    service = jasmine.createSpyObj<AdminService>('AdminService', ['auditLog']);
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: AdminService, useValue: service }] });
  });

  it('lists the newest entries with action, target and actor', async () => {
    service.auditLog.and.resolveTo(
      page([
        entry(),
        entry({ id: 6, action: 'favorite.add', target: 'loc-1', actor: { userId: 'u-2', email: null, apiKeyId: 'k-9', apiKeyName: 'Import' } }),
        entry({ id: 5, action: 'auth.login', actor: { userId: null, email: null, apiKeyId: 'k-8', apiKeyName: null } }),
        entry({ id: 4, action: 'setup.admin', actor: { userId: null, email: null, apiKeyId: null, apiKeyName: null } }),
      ]),
    );
    await render();

    expect(service.auditLog).toHaveBeenCalledWith({ actor: '', action: '', entity: '', from: '', to: '' }, null);
    expect(rows().map((row) => text(row, '.action'))).toEqual(['API-Key widerrufen', 'favorite.add', 'Anmeldung', 'setup.admin']);
    expect(text(rows()[0], 'td')).toMatch(/^16\.10\.2026 \d{2}:40:00$/);
    expect(text(rows()[0], '.target')).toBe('api_key:k-1');
    expect(rows().map((row) => text(row, '.actor'))).toEqual([
      'admin@example.org',
      'u-2 über API-Key „Import“',
      'System über API-Key „k-8“',
      'System',
    ]);
    expect(root().querySelector('#audit-more')).toBeNull();
  });

  it('searches with the filters, a range of days, loads older entries and resets', async () => {
    service.auditLog.and.resolveTo(page([entry()], '7'));
    await render();

    type(root(), '#audit-actor', ' bob ');
    type(root(), '#audit-action', 'apikey.');
    type(root(), '#audit-entity', 'api_key:');
    type(root(), '#audit-from', '2026-10-16');
    type(root(), '#audit-to', '2026-10-16');
    service.auditLog.and.resolveTo(page([entry({ id: 9 })], '9'));
    submit(root());
    await settle();
    expect(service.auditLog).toHaveBeenCalledWith(
      { actor: 'bob', action: 'apikey.', entity: 'api_key:', from: dayStart('2026-10-16'), to: dayStart('2026-10-17') },
      null,
    );
    expect(rows().length).toBe(1);

    service.auditLog.and.resolveTo(page([entry({ id: 8 })]));
    root().querySelector<HTMLButtonElement>('#audit-more')!.click();
    await settle();
    expect(service.auditLog.calls.mostRecent().args[1]).toBe('9');
    expect(rows().length).toBe(2);
    expect(root().querySelector('#audit-more')).toBeNull();

    service.auditLog.and.resolveTo(page([]));
    root().querySelector<HTMLButtonElement>('#audit-reset')!.click();
    await settle();
    expect(service.auditLog).toHaveBeenCalledWith({ actor: '', action: '', entity: '', from: '', to: '' }, null);
    expect((root().querySelector('#audit-actor') as HTMLInputElement).value).toBe('');
    expect(text(root(), '#audit-empty')).toBe('Keine Einträge für diese Filter.');
  });

  it('shows errors of the API', async () => {
    service.auditLog.and.rejectWith(new HttpErrorResponse({ status: 403, statusText: 'Forbidden' }));
    await render();

    expect(root().querySelector('.error[role="alert"]')).not.toBeNull();
  });

  it('turns a day into the start of that day or of the next one in local time', () => {
    expect(new Date(dayStart('2026-10-16')).getDate()).toBe(16);
    expect(new Date(dayStart('2026-10-16')).getHours()).toBe(0);
    expect(new Date(dayStart('2026-10-31', true)).getDate()).toBe(1);
  });
});
