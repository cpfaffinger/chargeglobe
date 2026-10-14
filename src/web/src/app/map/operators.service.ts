import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/** Element of GET /api/v1/operators. */
export interface OperatorSummary {
  id: string;
  name: string;
  operatorId: string | null;
  /** Number of active locations. */
  locations: number;
}

/** Operators for the operator filter, most locations first (public). */
@Injectable({ providedIn: 'root' })
export class OperatorsService {
  private readonly http = inject(HttpClient);

  /** Operators whose name or operator ID contains the query; all for an empty one. */
  search(query: string, limit = 20): Promise<OperatorSummary[]> {
    const params: Record<string, string | number> = { limit };
    if (query) {
      params['q'] = query;
    }
    return this.get(params);
  }

  /** The operators with these ids (names of a saved filter). */
  byIds(ids: string[]): Promise<OperatorSummary[]> {
    return this.get({ id: ids, limit: 100 });
  }

  private async get(params: Record<string, string | number | string[]>): Promise<OperatorSummary[]> {
    return (await firstValueFrom(this.http.get<{ data: OperatorSummary[] }>('/api/v1/operators', { params }))).data;
  }
}
