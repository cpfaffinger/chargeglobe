import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/** Pause of a source: cooldown until a time, or blocked until one of its settings changes. */
export interface ProviderPause {
  failures: number;
  until: string | null;
  blocked: boolean;
  reason: string | null;
}

export interface TileSummary {
  total: number;
  due: number;
  hot: number;
  normal: number;
  quiet: number;
  onDemand: number;
  oldestPoll: string | null;
}

export interface PlanStatus {
  jobType: string;
  target: string;
  nextRunAt: string;
  leasedBy: string | null;
  lastRunAt: string | null;
  lastResult: string | null;
}

export interface ProviderCrawlStatus {
  key: string;
  name: string | null;
  enabled: boolean;
  budgetPerHour: number;
  requestsLastHour: number;
  pause: ProviderPause | null;
  tiles: TileSummary;
  plans: PlanStatus[];
}

/** GET /api/v1/admin/crawler/status (jobs:read). */
export interface CrawlerStatus {
  at: string;
  providers: ProviderCrawlStatus[];
  queue: { pending: number; failed: number };
}

export type CrawlerResult = 'RUNNING' | 'OK' | 'FAILED';

/** Element of GET /api/v1/admin/crawler/runs. */
export interface CrawlerRun {
  id: string;
  provider: string;
  jobType: string;
  target: string;
  startedAt: string;
  finishedAt: string | null;
  requests: number;
  records: number;
  result: CrawlerResult;
  error: string | null;
  cooldownUntil: string | null;
}

export const RUNS_SHOWN = 50;

/** Client of the crawler endpoints for administrators. */
@Injectable({ providedIn: 'root' })
export class CrawlerService {
  private readonly http = inject(HttpClient);

  async status(): Promise<CrawlerStatus> {
    return (await firstValueFrom(this.http.get<{ data: CrawlerStatus }>('/api/v1/admin/crawler/status'))).data;
  }

  async runs(): Promise<CrawlerRun[]> {
    const response = await firstValueFrom(
      this.http.get<{ data: CrawlerRun[] }>('/api/v1/admin/crawler/runs', { params: { limit: RUNS_SHOWN } }),
    );
    return response.data;
  }
}
