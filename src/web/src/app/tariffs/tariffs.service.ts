import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/** Element of GET /api/v1/emps: a charging card or app. */
export interface Emp {
  key: string;
  name: string;
  website: string | null;
  /** Number of tariffs. */
  tariffs: number;
}

/** Body of PUT /api/v1/tariffs/{id}; amounts null if the tariff has no such part. */
export interface TariffInput {
  emp: string;
  name: string;
  currency: string;
  validFrom: string;
  validTo: string | null;
  pricePerKwhAc: number | null;
  pricePerKwhDc: number | null;
  pricePerMinute: number | null;
  sessionFee: number | null;
  blockingFeePerMinute: number | null;
  blockingAfterMinutes: number | null;
  monthlyFee: number | null;
  roamingOnly: boolean;
  /** Only at the EVSEs of these operators; empty = not limited. */
  operatorIds: string[];
}

/** Element of GET /api/v1/tariffs. */
export interface Tariff extends TariffInput {
  id: string;
}

/** Client of the EMPs and tariffs (price:read to read, tariff:write to change). */
@Injectable({ providedIn: 'root' })
export class TariffsService {
  private readonly http = inject(HttpClient);

  async emps(): Promise<Emp[]> {
    return (await firstValueFrom(this.http.get<{ data: Emp[] }>('/api/v1/emps'))).data;
  }

  async putEmp(key: string, name: string, website: string | null): Promise<Emp> {
    return (await firstValueFrom(this.http.put<{ data: Emp }>(`/api/v1/emps/${encodeURIComponent(key)}`, { name, website }))).data;
  }

  deleteEmp(key: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`/api/v1/emps/${encodeURIComponent(key)}`));
  }

  async tariffs(): Promise<Tariff[]> {
    return (await firstValueFrom(this.http.get<{ data: Tariff[] }>('/api/v1/tariffs'))).data;
  }

  /** Creates or replaces the tariff; the client chooses the id of a new one. */
  async putTariff(id: string, tariff: TariffInput): Promise<Tariff> {
    return (await firstValueFrom(this.http.put<{ data: Tariff }>(`/api/v1/tariffs/${id}`, tariff))).data;
  }

  deleteTariff(id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`/api/v1/tariffs/${id}`));
  }
}
