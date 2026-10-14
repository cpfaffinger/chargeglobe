import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/** Response of GET /api/v1/map/config (settings map.*). */
export interface MapConfig {
  styleUrl: string;
  attribution: string;
  center: { latitude: number; longitude: number };
  zoom: number;
}

/** Loads the base map configuration; public, no login required. */
@Injectable({ providedIn: 'root' })
export class MapConfigService {
  private readonly http = inject(HttpClient);

  load(): Promise<MapConfig> {
    return firstValueFrom(this.http.get<MapConfig>('/api/v1/map/config'));
  }
}
