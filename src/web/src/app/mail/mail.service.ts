import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type MailStatus = 'QUEUED' | 'SENT' | 'FAILED';

/** Element of GET /api/v1/mail/outbox; mail bodies are never part of it. */
export interface MailOutboxItem {
  id: string;
  to: string;
  subject: string;
  templateKey: string;
  status: MailStatus;
  attempts: number;
  lastError: string | null;
  createdAt: string;
  sentAt: string | null;
  nextAttemptAt: string | null;
}

export interface MailOutboxPage {
  data: MailOutboxItem[];
  meta: { total: number; page: number; pageSize: number };
}

export const OUTBOX_PAGE_SIZE = 25;

/** Client of the mail endpoints (settings:manage). */
@Injectable({ providedIn: 'root' })
export class MailService {
  private readonly http = inject(HttpClient);

  /** Sends a test mail with the current SMTP settings; resolves to the message of the API. */
  async sendTest(to: string): Promise<string> {
    const response = await firstValueFrom(
      this.http.post<{ message: string }>('/api/v1/mail/test', { to }),
    );
    return response.message;
  }

  /** One page of the outbox, newest first; an empty status means all. */
  outbox(status: MailStatus | '', page: number): Promise<MailOutboxPage> {
    const params: Record<string, string | number> = { page, pageSize: OUTBOX_PAGE_SIZE };
    if (status) {
      params['status'] = status;
    }

    return firstValueFrom(this.http.get<MailOutboxPage>('/api/v1/mail/outbox', { params }));
  }
}
