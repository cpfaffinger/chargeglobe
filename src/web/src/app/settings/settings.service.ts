import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type SettingType = 'string' | 'int' | 'number' | 'bool' | 'enum' | 'secret';

/** A value stored for one scope; secrets are masked by the API. */
export interface StoredSettingValue {
  scope: string;
  value: unknown;
  updatedAt: string;
  updatedBy: string | null;
}

/** Element of GET /api/v1/settings: catalog entry plus current values. */
export interface Setting {
  key: string;
  type: SettingType;
  description: string;
  default: unknown;
  allowedValues: string[];
  min: number | null;
  max: number | null;
  scopeKinds: string[];
  /** Concrete scopes a value can be stored for, e.g. "global" or "provider:econtrol". */
  scopes: string[];
  /** Effective global value; for a secret "********" if set, else null. */
  value: unknown;
  stored: StoredSettingValue[];
}

/** Client of the central config service (settings:read / settings:manage). */
@Injectable({ providedIn: 'root' })
export class SettingsService {
  private readonly http = inject(HttpClient);

  list(): Promise<Setting[]> {
    return firstValueFrom(this.http.get<Setting[]>('/api/v1/settings'));
  }

  /** Stores the value for the scope; null resets the setting to its default there. */
  update(key: string, value: unknown, scope: string): Promise<Setting> {
    return firstValueFrom(
      this.http.put<Setting>(`/api/v1/settings/${encodeURIComponent(key)}`, { value, scope }),
    );
  }
}
