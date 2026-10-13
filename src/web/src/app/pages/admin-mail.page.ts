import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { attempt } from '../auth/problem';
import { MailOutboxPage, MailService, MailStatus, OUTBOX_PAGE_SIZE } from '../mail/mail.service';

/**
 * Admin page of the mail service (settings:manage): test mail with the current SMTP
 * settings and the delivery status of the outbox.
 */
@Component({
  imports: [DatePipe, ReactiveFormsModule, RouterLink],
  selector: 'app-admin-mail',
  template: `
    <h2>E-Mail-Versand</h2>
    <p>
      Der SMTP-Zugang wird unter <a routerLink="/admin/settings">Einstellungen</a> gepflegt (<code>mail.*</code>).
      Die Testmail funktioniert auch, solange <code>mail.enabled</code> ausgeschaltet ist.
    </p>

    <section id="test-mail">
      <h3>Testmail</h3>
      <form [formGroup]="form" (ngSubmit)="sendTest()">
        <label>Empfänger <input type="email" formControlName="to" autocomplete="email" required /></label>
        <button type="submit" [disabled]="sending()">Testmail senden</button>
      </form>
      @if (result(); as message) {
        <p id="test-result" role="status">{{ message }}</p>
      }
      @for (error of sendErrors(); track error) {
        <p class="error" role="alert">{{ error }}</p>
      }
    </section>

    <section id="outbox">
      <h3>Postausgang</h3>
      <label>
        Status
        <select id="status-filter" (change)="filter($event)">
          <option value="">alle</option>
          <option value="QUEUED">wartend (QUEUED)</option>
          <option value="SENT">versendet (SENT)</option>
          <option value="FAILED">fehlgeschlagen (FAILED)</option>
        </select>
      </label>
      <button type="button" id="reload" [disabled]="loading()" (click)="load()">Aktualisieren</button>
      @for (error of loadErrors(); track error) {
        <p class="error" role="alert">{{ error }}</p>
      }
      @if (outbox(); as current) {
        <table>
          <thead>
            <tr>
              <th>Erstellt</th>
              <th>Empfänger</th>
              <th>Betreff</th>
              <th>Status</th>
              <th>Versuche</th>
              <th>Hinweis</th>
            </tr>
          </thead>
          <tbody>
            @for (mail of current.data; track mail.id) {
              <tr>
                <td>{{ mail.createdAt | date: 'dd.MM.yyyy HH:mm' }}</td>
                <td>{{ mail.to }}</td>
                <td>{{ mail.subject }}</td>
                <td class="status">{{ mail.status }}</td>
                <td>{{ mail.attempts }}</td>
                <td class="note">
                  @if (mail.sentAt) {
                    versendet {{ mail.sentAt | date: 'dd.MM.yyyy HH:mm' }}
                  } @else if (mail.lastError) {
                    {{ mail.lastError }}
                    @if (mail.nextAttemptAt) {
                      · nächster Versuch {{ mail.nextAttemptAt | date: 'dd.MM.yyyy HH:mm' }}
                    }
                  }
                </td>
              </tr>
            } @empty {
              <tr>
                <td colspan="6" id="outbox-empty">Keine Einträge.</td>
              </tr>
            }
          </tbody>
        </table>
        <p id="paging">
          <button type="button" id="previous" [disabled]="loading() || page() <= 1" (click)="go(-1)">Zurück</button>
          Seite {{ page() }} von {{ pageCount() }} ({{ current.meta.total }} Einträge)
          <button type="button" id="next" [disabled]="loading() || page() >= pageCount()" (click)="go(1)">
            Weiter
          </button>
        </p>
      }
    </section>
  `,
})
export class AdminMailPage {
  private readonly mail = inject(MailService);

  protected readonly form = inject(FormBuilder).nonNullable.group({ to: '' });
  protected readonly sending = signal(false);
  protected readonly sendErrors = signal<string[]>([]);
  protected readonly result = signal<string | null>(null);

  protected readonly status = signal<MailStatus | ''>('');
  protected readonly page = signal(1);
  protected readonly outbox = signal<MailOutboxPage | null>(null);
  protected readonly loading = signal(false);
  protected readonly loadErrors = signal<string[]>([]);
  protected readonly pageCount = computed(() =>
    Math.max(1, Math.ceil((this.outbox()?.meta.total ?? 0) / OUTBOX_PAGE_SIZE)),
  );

  constructor() {
    void this.load();
  }

  protected sendTest(): Promise<void> {
    this.result.set(null);
    return attempt(this.sending, this.sendErrors, async () => {
      this.result.set(await this.mail.sendTest(this.form.getRawValue().to));
    });
  }

  protected filter(event: Event): Promise<void> {
    this.status.set((event.target as HTMLSelectElement).value as MailStatus | '');
    this.page.set(1);
    return this.load();
  }

  protected go(delta: number): Promise<void> {
    this.page.update((page) => page + delta);
    return this.load();
  }

  protected load(): Promise<void> {
    return attempt(this.loading, this.loadErrors, async () => {
      this.outbox.set(await this.mail.outbox(this.status(), this.page()));
    });
  }
}
