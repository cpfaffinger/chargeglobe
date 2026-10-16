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

export interface RegionFreshness {
  code: string;
  name: string;
  evses: number;
  withStatus: number;
  fresh20Percent: number;
  fresh60Percent: number;
}

export interface PriorityPoll {
  priority: 'HOT' | 'NORMAL' | 'QUIET';
  count: number;
  neverPolled: number;
  oldestPoll: string | null;
}

export interface SourceFreshness {
  key: string;
  requestsLastHour: number;
  budgetPerHour: number;
  points: PriorityPoll[];
}

export interface OperatorFaults {
  prefix: string;
  name: string | null;
  phases: number;
  evses: number;
  ongoing: number;
  medianMinutes: number | null;
}

/** GET /api/v1/admin/crawler/freshness (jobs:read), computed at most once a minute. */
export interface DataFreshness {
  at: string;
  regions: RegionFreshness[];
  sources: SourceFreshness[];
  operators: OperatorFaults[];
}

export const RUNS_SHOWN = 50;

/** Client of the crawler endpoints for administrators. */
@Injectable({ providedIn: 'root' })
export class CrawlerService {
  private readonly http = inject(HttpClient);

  async status(): Promise<CrawlerStatus> {
    return (await firstValueFrom(this.http.get<{ data: CrawlerStatus }>('/api/v1/admin/crawler/status'))).data;
  }

  async freshness(): Promise<DataFreshness> {
    return (await firstValueFrom(this.http.get<{ data: DataFreshness }>('/api/v1/admin/crawler/freshness'))).data;
  }

  async runs(): Promise<CrawlerRun[]> {
    const response = await firstValueFrom(
      this.http.get<{ data: CrawlerRun[] }>('/api/v1/admin/crawler/runs', { params: { limit: RUNS_SHOWN } }),
    );
    return response.data;
  }
}
