import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorIntl, MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { attempt, attemptValid } from '../auth/problem';
import { AppIcon } from '../design/icon';
import { GermanPaginatorIntl } from '../design/paginator-intl';
import { MailOutboxPage, MailService, MailStatus, OUTBOX_PAGE_SIZE } from '../mail/mail.service';

/**
 * Admin page of the mail service (settings:manage): test mail with the current SMTP
 * settings and the delivery status of the outbox (table with server-side paging).
 */
@Component({
  imports: [
    AppIcon,
    DatePipe,
    MatButtonModule,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
    MatPaginatorModule,
    MatTableModule,
    ReactiveFormsModule,
    RouterLink,
  ],
  selector: 'app-admin-mail',
  providers: [{ provide: MatPaginatorIntl, useClass: GermanPaginatorIntl }],
  template: `
    <h1>E-Mail-Versand</h1>
    <p class="muted">
      Der SMTP-Zugang wird unter <a routerLink="/admin/settings">Einstellungen</a> gepflegt (<code>mail.*</code>).
      Die Testmail funktioniert auch, solange <code>mail.enabled</code> ausgeschaltet ist.
    </p>

    <mat-card appearance="outlined">
      <mat-card-content>
        <section id="test-mail">
          <h2>Testmail</h2>
          <form [formGroup]="form" (ngSubmit)="sendTest()">
            <mat-form-field subscriptSizing="dynamic">
              <mat-label>Empfänger</mat-label>
              <input matInput type="email" formControlName="to" autocomplete="email" required />
              <mat-error>Bitte eine gültige E-Mail-Adresse eingeben.</mat-error>
            </mat-form-field>
            <button matButton="filled" type="submit" [disabled]="sending()">Testmail senden</button>
          </form>
          @if (result(); as message) {
            <p id="test-result" role="status">{{ message }}</p>
          }
          @for (error of sendErrors(); track error) {
            <p class="error" role="alert">{{ error }}</p>
          }
        </section>
      </mat-card-content>
    </mat-card>

    <section id="outbox">
      <h2>Postausgang</h2>
      <div class="outbox-filter">
        <mat-form-field subscriptSizing="dynamic">
          <mat-label>Status</mat-label>
          <select matNativeControl id="status-filter" (change)="filter($event)">
            <option value="">alle</option>
            <option value="QUEUED">wartend (QUEUED)</option>
            <option value="SENT">versendet (SENT)</option>
            <option value="FAILED">fehlgeschlagen (FAILED)</option>
          </select>
        </mat-form-field>
        <button matButton type="button" id="reload" [disabled]="loading()" (click)="load()"><app-icon name="refresh" />Aktualisieren</button>
      </div>
      @for (error of loadErrors(); track error) {
        <p class="error" role="alert">{{ error }}</p>
      }
      @if (outbox(); as current) {
        <div class="table-box">
          <table mat-table [dataSource]="current.data">
            <ng-container matColumnDef="createdAt">
              <th mat-header-cell *matHeaderCellDef>Erstellt</th>
              <td mat-cell *matCellDef="let mail">{{ mail.createdAt | date: 'dd.MM.yyyy HH:mm' }}</td>
            </ng-container>
            <ng-container matColumnDef="to">
              <th mat-header-cell *matHeaderCellDef>Empfänger</th>
              <td mat-cell *matCellDef="let mail">{{ mail.to }}</td>
            </ng-container>
            <ng-container matColumnDef="subject">
              <th mat-header-cell *matHeaderCellDef>Betreff</th>
              <td mat-cell *matCellDef="let mail">{{ mail.subject }}</td>
            </ng-container>
            <ng-container matColumnDef="status">
              <th mat-header-cell *matHeaderCellDef>Status</th>
              <td mat-cell *matCellDef="let mail" class="status">{{ mail.status }}</td>
            </ng-container>
            <ng-container matColumnDef="attempts">
              <th mat-header-cell *matHeaderCellDef>Versuche</th>
              <td mat-cell *matCellDef="let mail">{{ mail.attempts }}</td>
            </ng-container>
            <ng-container matColumnDef="note">
              <th mat-header-cell *matHeaderCellDef>Hinweis</th>
              <td mat-cell *matCellDef="let mail" class="note">
                @if (mail.sentAt) {
                  versendet {{ mail.sentAt | date: 'dd.MM.yyyy HH:mm' }}
                } @else if (mail.lastError) {
                  {{ mail.lastError }}
                  @if (mail.nextAttemptAt) {
                    · nächster Versuch {{ mail.nextAttemptAt | date: 'dd.MM.yyyy HH:mm' }}
                  }
                }
              </td>
            </ng-container>
            <tr mat-header-row *matHeaderRowDef="columns"></tr>
            <tr mat-row *matRowDef="let row; columns: columns"></tr>
            <tr class="mat-mdc-row no-data" *matNoDataRow>
              <td class="mat-mdc-cell" [attr.colspan]="columns.length" id="outbox-empty">Keine Einträge.</td>
            </tr>
          </table>
        </div>
        <mat-paginator
          [length]="current.meta.total"
          [pageIndex]="page() - 1"
          [pageSize]="pageSize"
          [hidePageSize]="true"
          [disabled]="loading()"
          aria-label="Seiten des Postausgangs"
          (page)="go($event)"
        />
      }
    </section>
  `,
})
export class AdminMailPage {
  private readonly mail = inject(MailService);

  protected readonly columns = ['createdAt', 'to', 'subject', 'status', 'attempts', 'note'];
  protected readonly pageSize = OUTBOX_PAGE_SIZE;
  protected readonly form = inject(FormBuilder).nonNullable.group({ to: ['', [Validators.required, Validators.email]] });
  protected readonly sending = signal(false);
  protected readonly sendErrors = signal<string[]>([]);
  protected readonly result = signal<string | null>(null);

  protected readonly status = signal<MailStatus | ''>('');
  protected readonly page = signal(1);
  protected readonly outbox = signal<MailOutboxPage | null>(null);
  protected readonly loading = signal(false);
  protected readonly loadErrors = signal<string[]>([]);

  constructor() {
    void this.load();
  }

  protected sendTest(): Promise<void> {
    this.result.set(null);
    return attemptValid(this.form, this.sending, this.sendErrors, async () => {
      this.result.set(await this.mail.sendTest(this.form.getRawValue().to));
    });
  }

  protected filter(event: Event): Promise<void> {
    this.status.set((event.target as HTMLSelectElement).value as MailStatus | '');
    this.page.set(1);
    return this.load();
  }

  protected go(event: PageEvent): Promise<void> {
    this.page.set(event.pageIndex + 1);
    return this.load();
  }

  protected load(): Promise<void> {
    return attempt(this.loading, this.loadErrors, async () => {
      this.outbox.set(await this.mail.outbox(this.status(), this.page()));
    });
  }
}
