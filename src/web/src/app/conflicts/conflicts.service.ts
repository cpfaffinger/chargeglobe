import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/** Element of GET /api/v1/conflicts. */
export interface Conflict {
  id: string;
  entityType: 'LOCATION' | 'EVSE';
  entityId: string | null;
  /** Name and address of the location or the EVSE-ID; null if it no longer exists. */
  entityLabel: string | null;
  /** identity: a record fits several locations; evse_id: two notations of one EVSE-ID. */
  attribute: string;
  values: Record<string, unknown>;
  detectedAt: string;
  resolvedAt: string | null;
  resolution: string | null;
}

export interface ConflictsResponse {
  data: Conflict[];
  meta: { returned: number; limit: number; open: number };
}

/** Conflicts between sources (conflict:resolve). */
@Injectable({ providedIn: 'root' })
export class ConflictsService {
  private readonly http = inject(HttpClient);

  list(resolved: boolean): Promise<ConflictsResponse> {
    const params: Record<string, string | number> = { limit: 200 };
    if (resolved) {
      params['resolved'] = 'true';
    }
    return firstValueFrom(this.http.get<ConflictsResponse>('/api/v1/conflicts', { params }));
  }
}

/** The values of a conflict in short German, e.g. "ocm 148527 passt zu 2 Standorten". */
export function describeConflict(conflict: Conflict): string {
  const values = conflict.values;
  if (conflict.attribute === 'identity' && Array.isArray(values['candidates'])) {
    return `${values['source']} ${values['externalId']} passt zu ${values['candidates'].length} Standorten`;
  }
  return Object.entries(values)
    .map(([key, value]) => `${key}: ${typeof value === 'string' ? value : JSON.stringify(value)}`)
    .join(' · ');
}
