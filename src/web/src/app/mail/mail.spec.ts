import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AdminMailPage } from '../pages/admin-mail.page';
import { flush, submit, text, type, validationError } from '../testing';
import { MailOutboxItem, MailOutboxPage, MailService } from './mail.service';

function item(overrides: Partial<MailOutboxItem>): MailOutboxItem {
  return {
    id: '01990b7c-2a4e-7f10-b6d3-5c8e9a1f2b3c',
    to: 'anna@example.org',
    subject: 'chargeglobe: E-Mail-Adresse bestätigen',
    templateKey: 'confirm-email',
    status: 'QUEUED',
    attempts: 0,
    lastError: null,
    createdAt: '2026-10-14T08:15:00Z',
    sentAt: null,
    nextAttemptAt: null,
    ...overrides,
  };
}

const pageOf = (data: MailOutboxItem[], total = data.length, page = 1): MailOutboxPage => ({
  data,
  meta: { total, page, pageSize: 25 },
});

describe('MailService', () => {
  let service: MailService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(MailService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('sends a test mail and returns the message of the API', async () => {
    const sent = service.sendTest('anna@example.org');
    const request = http.expectOne('/api/v1/mail/test');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ to: 'anna@example.org' });
    request.flush({ message: 'accepted' });

    expect(await sent).toBe('accepted');
  });

  it('loads a page of the outbox, with the status filter only if one is set', async () => {
    const all = service.outbox('', 2);
    http.expectOne('/api/v1/mail/outbox?page=2&pageSize=25').flush(pageOf([]));
    expect(await all).toEqual(pageOf([]));

    const failed = service.outbox('FAILED', 1);
    http.expectOne('/api/v1/mail/outbox?page=1&pageSize=25&status=FAILED').flush(pageOf([item({})]));
    expect((await failed).data.length).toBe(1);
  });
});

describe('AdminMailPage', () => {
  let service: jasmine.SpyObj<MailService>;
  let fixture: ComponentFixture<AdminMailPage>;

  const root = () => fixture.nativeElement as HTMLElement;
  const button = (id: string) => root().querySelector<HTMLButtonElement>(`#${id}`)!;
  const rows = () => Array.from(root().querySelectorAll('tbody tr'));

  async function settle(): Promise<void> {
    await flush();
    await fixture.whenStable();
  }

  async function render(): Promise<void> {
    fixture = TestBed.createComponent(AdminMailPage);
    await settle();
  }

  beforeEach(() => {
    service = jasmine.createSpyObj<MailService>('MailService', ['sendTest', 'outbox']);
    service.outbox.and.resolveTo(pageOf([]));
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: MailService, useValue: service }],
    });
  });

  it('lists the outbox with status, attempts and the note of each mail', async () => {
    service.outbox.and.resolveTo(
      pageOf([
        item({ status: 'SENT', attempts: 1, sentAt: '2026-10-14T08:15:07Z' }),
        item({
          id: '2',
          to: 'bob@example.org',
          attempts: 2,
          lastError: 'Connection refused',
          nextAttemptAt: '2026-10-14T08:19:00Z',
        }),
        item({ id: '3', to: 'eve@example.org', status: 'FAILED', attempts: 5, lastError: 'Mailbox unavailable' }),
        item({ id: '4', to: 'new@example.org' }),
      ]),
    );
    await render();

    expect(service.outbox).toHaveBeenCalledOnceWith('', 1);
    expect(rows().length).toBe(4);
    expect(text(rows()[0], '.status')).toBe('SENT');
    expect(text(rows()[0], '.note')).toMatch(/^versendet \d{2}\.10\.2026 \d{2}:15$/);
    expect(text(rows()[1], '.note')).toMatch(/^Connection refused · nächster Versuch \d{2}\.10\.2026 \d{2}:19$/);
    expect(text(rows()[2], '.note')).toBe('Mailbox unavailable');
    expect(text(rows()[3], '.note')).toBe('');
    expect(text(root(), '#paging')).toContain('Seite 1 von 1 (4 Einträge)');
    expect(button('previous').disabled).toBeTrue();
    expect(button('next').disabled).toBeTrue();
  });

  it('shows an empty outbox and reloads on demand', async () => {
    await render();

    expect(text(root(), '#outbox-empty')).toBe('Keine Einträge.');

    button('reload').click();
    await settle();

    expect(service.outbox).toHaveBeenCalledTimes(2);
  });

  it('filters by status and starts again at the first page', async () => {
    service.outbox.and.resolveTo(pageOf([item({})], 60));
    await render();
    button('next').click();
    await settle();
    expect(service.outbox).toHaveBeenCalledWith('', 2);

    const filter = root().querySelector<HTMLSelectElement>('#status-filter')!;
    filter.value = 'FAILED';
    filter.dispatchEvent(new Event('change'));
    await settle();

    expect(service.outbox.calls.mostRecent().args).toEqual(['FAILED', 1]);
  });

  it('pages forward and back within the available pages', async () => {
    service.outbox.and.resolveTo(pageOf([item({})], 60));
    await render();

    expect(text(root(), '#paging')).toContain('Seite 1 von 3 (60 Einträge)');
    expect(button('previous').disabled).toBeTrue();

    button('next').click();
    await settle();
    button('next').click();
    await settle();
    expect(service.outbox.calls.mostRecent().args).toEqual(['', 3]);
    expect(button('next').disabled).toBeTrue();

    button('previous').click();
    await settle();
    expect(service.outbox.calls.mostRecent().args).toEqual(['', 2]);
  });

  it('shows the error when the outbox cannot be loaded', async () => {
    service.outbox.and.rejectWith(validationError('Missing permission: settings:manage.'));
    await render();

    expect(text(root(), '#outbox .error')).toBe('Missing permission: settings:manage.');
    expect(root().querySelector('table')).toBeNull();
  });

  it('sends a test mail and shows the result of the SMTP server', async () => {
    service.sendTest.and.resolveTo('The SMTP server smtp.example.org:587 accepted the test mail for anna@example.org.');
    await render();

    type(root(), 'input[type=email]', 'anna@example.org');
    submit(root());
    await settle();

    expect(service.sendTest).toHaveBeenCalledOnceWith('anna@example.org');
    expect(text(root(), '#test-result')).toContain('accepted the test mail');
    expect(root().querySelector('#test-mail .error')).toBeNull();
  });

  it('shows the SMTP error of a failed test mail and clears an earlier result', async () => {
    service.sendTest.and.resolveTo('accepted');
    await render();
    type(root(), 'input[type=email]', 'anna@example.org');
    submit(root());
    await settle();
    expect(text(root(), '#test-result')).toBe('accepted');

    service.sendTest.and.rejectWith(validationError('Connection refused'));
    submit(root());
    await settle();

    expect(text(root(), '#test-mail .error')).toBe('Connection refused');
    expect(root().querySelector('#test-result')).toBeNull();
  });
});
