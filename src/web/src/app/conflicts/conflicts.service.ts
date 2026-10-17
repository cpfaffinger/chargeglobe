import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/** A location an identity conflict may belong to. */
export interface ConflictCandidate {
  locationId: string;
  /** Name and address; null if the location no longer exists. */
  label: string | null;
  /** Distance to the record of the source; null if unknown. */
  distanceMeters: number | null;
  operatorId: string | null;
  operatorName: string | null;
  /** Same operator as the record (by code or name); null if one side names none. */
  sameOperator: boolean | null;
  /** Active charge points of the location. */
  evses: number;
  /** Active charge points without an EVSE-ID of their source. */
  surrogateEvses: number;
  evseIds: string[];
  /** Keys of the sources that know the location. */
  sources: string[];
}

/** The record of the source that fits several locations, as the source delivered it. */
export interface ConflictRecord {
  source: string;
  externalId: string;
  name: string | null;
  address: string | null;
  postalCode: string | null;
  city: string | null;
  operatorId: string | null;
  operatorName: string | null;
  latitude: number | null;
  longitude: number | null;
  evses: { evseId: string | null; maxPowerKw: number | null }[];
}

/** What the heuristics would decide; absent when they do not dare. */
export interface ConflictSuggestion {
  action: ConflictAction;
  targetId: string | null;
  /** 0.5 to 1; the higher, the safer. */
  confidence: number;
  reason: string;
}

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
  /** The locations of an identity conflict, nearest first. */
  candidates: ConflictCandidate[];
  /** The record of an identity conflict; null until the source ran again. */
  record: ConflictRecord | null;
  suggestion: ConflictSuggestion | null;
}

export interface ConflictsResponse {
  data: Conflict[];
  meta: { returned: number; limit: number; open: number; nextCursor: string | null };
}

/** merge (identity, with a candidate), separate (identity), keep and same (evse_id). */
export type ConflictAction = 'merge' | 'separate' | 'keep' | 'same';

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

  /** Decides the conflict; a conflict resolved before answers with its state. */
  async resolve(id: string, action: ConflictAction, targetId: string | null = null): Promise<Conflict> {
    return (await firstValueFrom(this.http.post<{ data: Conflict }>(`/api/v1/conflicts/${id}/resolve`, { action, targetId }))).data;
  }
}

/** The values of a conflict in short German, e.g. "ocm 148527 passt zu 2 Standorten". */
export function describeConflict(conflict: Conflict): string {
  const values = conflict.values;
  if (conflict.attribute === 'identity' && Array.isArray(values['candidates'])) {
    const record = conflict.record;
    const detail = record ? ` (${[record.name, record.address, record.operatorName].filter((part) => part).join(', ')}; ${record.evses.length} Ladepunkte)` : '';
    return `${values['source']} ${values['externalId']} passt zu ${values['candidates'].length} Standorten${detail}`;
  }
  return Object.entries(values)
    .filter(([key]) => !['source', 'externalId', 'locationId'].includes(key))
    .map(([key, value]) => `${key}: ${typeof value === 'string' ? value : JSON.stringify(value)}`)
    .join(' · ');
}
