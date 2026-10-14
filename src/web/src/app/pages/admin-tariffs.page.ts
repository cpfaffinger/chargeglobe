import { DatePipe, formatDate } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar } from '@angular/material/snack-bar';
import { attempt, attemptValid } from '../auth/problem';
import { money } from '../evses/prices';
import { OperatorSummary, OperatorsService } from '../map/operators.service';
import { Emp, Tariff, TariffInput, TariffsService } from '../tariffs/tariffs.service';

/** Key of an EMP as the API accepts it. */
export const EMP_KEY = /^[a-z0-9][a-z0-9-]{1,39}$/;

/** An ISO time as the value of an <input type="date"> (local day). */
export const dateInput = (iso: string | null): string => (iso === null ? '' : formatDate(iso, 'yyyy-MM-dd', 'en-US'));

/** The value of an <input type="date"> as ISO time of the local midnight; null if empty. */
export const isoDate = (value: string): string | null => (value ? new Date(`${value}T00:00:00`).toISOString() : null);

const amount = [Validators.min(0), Validators.max(1000)];

/**
 * Charging cards and apps (EMPs) with their tariffs for the price comparison of the EVSE pages (tariff:write): a table
 * and a form for the EMPs, a table and a form for the tariffs with where they apply (own network by operators,
 * roaming or everywhere).
 */
@Component({
  imports: [DatePipe, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, ReactiveFormsModule],
  selector: 'app-admin-tariffs',
  styles: `
    .form-grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
      gap: 0 16px;
      align-items: start;
    }
    .form-grid .wide {
      grid-column: 1 / -1;
    }
    .actions {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      margin-bottom: 24px;
    }
    select[multiple] {
      min-height: 120px;
    }
  `,
  template: `
    <h1>Tarife</h1>
    <p class="muted">
      Tarife von Ladekarten und Apps (EMPs) für den Preisvergleich der Ladepunkte. Ein Tarif gilt mit Betreibern nur in deren Netz,
      sonst mit „nur Roaming“ an allen Ladepunkten mit Roaming, sonst überall; für Gleichstrom zählt der DC-Preis.
    </p>
    @for (error of errors(); track error) {
      <p class="error" role="alert">{{ error }}</p>
    }

    <h2>Anbieter</h2>
    <div class="table-box">
      <table class="data-table" id="emps">
        <thead>
          <tr><th scope="col">Schlüssel</th><th scope="col">Name</th><th scope="col">Website</th><th scope="col">Tarife</th><th scope="col"></th></tr>
        </thead>
        <tbody>
          @for (emp of emps(); track emp.key) {
            <tr [attr.data-emp]="emp.key">
              <td><code>{{ emp.key }}</code></td>
              <td>{{ emp.name }}</td>
              <td>{{ emp.website ?? '–' }}</td>
              <td>{{ emp.tariffs }}</td>
              <td>
                <button matButton type="button" class="edit" [disabled]="busy()" (click)="editEmp(emp)">Bearbeiten</button>
                @if (emp.tariffs === 0) {
                  <button matButton type="button" class="delete" [disabled]="busy()" (click)="removeEmp(emp)">Löschen</button>
                }
              </td>
            </tr>
          } @empty {
            <tr><td colspan="5" class="muted">Noch keine Anbieter.</td></tr>
          }
        </tbody>
      </table>
    </div>
    <form [formGroup]="empForm" (ngSubmit)="saveEmp()" id="emp-form" class="form-grid">
      <mat-form-field>
        <mat-label>Schlüssel</mat-label>
        <input matInput formControlName="key" id="emp-key" [readonly]="editingEmp()" required />
        <mat-hint>z. B. oeamtc</mat-hint>
        <mat-error>2–40 Zeichen: Kleinbuchstaben, Ziffern, „-“.</mat-error>
      </mat-form-field>
      <mat-form-field>
        <mat-label>Name</mat-label>
        <input matInput formControlName="name" id="emp-name" maxlength="100" required />
        <mat-error>Bitte einen Namen eingeben.</mat-error>
      </mat-form-field>
      <mat-form-field>
        <mat-label>Website</mat-label>
        <input matInput formControlName="website" id="emp-website" type="url" />
      </mat-form-field>
      <div class="actions wide">
        <button matButton="tonal" type="submit" [disabled]="busy()">{{ editingEmp() ? 'Anbieter speichern' : 'Anbieter anlegen' }}</button>
        @if (editingEmp()) {
          <button matButton type="button" id="emp-cancel" (click)="resetEmp()">Abbrechen</button>
        }
      </div>
    </form>

    <h2>Tarife</h2>
    <div class="table-box">
      <table class="data-table" id="tariffs">
        <thead>
          <tr>
            <th scope="col">Anbieter</th><th scope="col">Tarif</th><th scope="col">gültig</th><th scope="col">AC/kWh</th><th scope="col">DC/kWh</th>
            <th scope="col">weitere</th><th scope="col">Grundgebühr</th><th scope="col">gilt</th><th scope="col"></th>
          </tr>
        </thead>
        <tbody>
          @for (tariff of tariffs(); track tariff.id) {
            <tr [attr.data-tariff]="tariff.id">
              <td>{{ empName(tariff.emp) }}</td>
              <td>{{ tariff.name }}</td>
              <td>{{ tariff.validFrom | date: 'dd.MM.yyyy' }} – {{ tariff.validTo === null ? 'offen' : (tariff.validTo | date: 'dd.MM.yyyy') }}</td>
              <td>{{ show(tariff.pricePerKwhAc, tariff.currency) }}</td>
              <td>{{ show(tariff.pricePerKwhDc, tariff.currency) }}</td>
              <td>{{ extras(tariff) }}</td>
              <td>{{ show(tariff.monthlyFee, tariff.currency) }}</td>
              <td class="scope">{{ scope(tariff) }}</td>
              <td>
                <button matButton type="button" class="edit" [disabled]="busy()" (click)="editTariff(tariff)">Bearbeiten</button>
                <button matButton type="button" class="delete" [disabled]="busy()" (click)="removeTariff(tariff)">Löschen</button>
              </td>
            </tr>
          } @empty {
            <tr><td colspan="9" class="muted">Noch keine Tarife.</td></tr>
          }
        </tbody>
      </table>
    </div>
    <form [formGroup]="tariffForm" (ngSubmit)="saveTariff()" id="tariff-form" class="form-grid">
      <mat-form-field>
        <mat-label>Anbieter</mat-label>
        <select matNativeControl formControlName="emp" id="tariff-emp" required>
          @for (emp of emps(); track emp.key) {
            <option [value]="emp.key">{{ emp.name }}</option>
          }
        </select>
        <mat-error>Bitte einen Anbieter wählen.</mat-error>
      </mat-form-field>
      <mat-form-field>
        <mat-label>Tarif</mat-label>
        <input matInput formControlName="name" id="tariff-name" maxlength="100" required />
        <mat-error>Bitte einen Namen eingeben.</mat-error>
      </mat-form-field>
      <mat-form-field>
        <mat-label>Währung</mat-label>
        <input matInput formControlName="currency" id="tariff-currency" maxlength="3" required />
        <mat-error>ISO-Code, z. B. EUR.</mat-error>
      </mat-form-field>
      <mat-form-field>
        <mat-label>gültig ab</mat-label>
        <input matInput type="date" formControlName="validFrom" id="tariff-valid-from" required />
        <mat-error>Bitte ein Datum wählen.</mat-error>
      </mat-form-field>
      <mat-form-field>
        <mat-label>gültig bis (exklusiv)</mat-label>
        <input matInput type="date" formControlName="validTo" id="tariff-valid-to" />
      </mat-form-field>
      @for (field of amounts; track field.key) {
        <mat-form-field>
          <mat-label>{{ field.label }}</mat-label>
          <input matInput type="number" step="0.0001" min="0" max="1000" [formControlName]="field.key" [id]="'tariff-' + field.key" />
          <mat-error>0 bis 1000.</mat-error>
        </mat-form-field>
      }
      <mat-form-field>
        <mat-label>Blockiergebühr ab Minute</mat-label>
        <input matInput type="number" step="1" min="0" max="1440" formControlName="blockingAfterMinutes" id="tariff-blockingAfterMinutes" />
        <mat-error>0 bis 1440.</mat-error>
      </mat-form-field>
      <mat-form-field class="wide">
        <mat-label>nur bei Betreibern (eigenes Netz)</mat-label>
        <select matNativeControl multiple formControlName="operatorIds" id="tariff-operators">
          @for (operator of operators(); track operator.id) {
            <option [value]="operator.id">{{ operator.name }}{{ operator.operatorId ? ' (' + operator.operatorId + ')' : '' }}</option>
          }
        </select>
        <mat-hint>Mehrfachauswahl mit Strg; leer = nicht auf Betreiber beschränkt.</mat-hint>
      </mat-form-field>
      <mat-checkbox formControlName="roamingOnly" id="tariff-roaming" class="wide">nur an Ladepunkten mit Roaming</mat-checkbox>
      <div class="actions wide">
        <button matButton="tonal" type="submit" [disabled]="busy()">{{ editingTariff() ? 'Tarif speichern' : 'Tarif anlegen' }}</button>
        @if (editingTariff()) {
          <button matButton type="button" id="tariff-cancel" (click)="resetTariff()">Abbrechen</button>
        }
      </div>
    </form>
  `,
})
export class AdminTariffsPage {
  private readonly service = inject(TariffsService);
  private readonly operatorsService = inject(OperatorsService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly fb = inject(FormBuilder).nonNullable;

  protected readonly amounts = [
    { key: 'pricePerKwhAc', label: 'Preis AC je kWh' },
    { key: 'pricePerKwhDc', label: 'Preis DC je kWh' },
    { key: 'pricePerMinute', label: 'Preis je Minute' },
    { key: 'sessionFee', label: 'Startgebühr' },
    { key: 'blockingFeePerMinute', label: 'Blockiergebühr je Minute' },
    { key: 'monthlyFee', label: 'Grundgebühr je Monat' },
  ] as const;

  protected readonly empForm = this.fb.group({
    key: ['', [Validators.required, Validators.pattern(EMP_KEY)]],
    name: ['', [Validators.required, Validators.maxLength(100)]],
    website: [''],
  });

  protected readonly tariffForm = this.fb.group({
    emp: ['', Validators.required],
    name: ['', [Validators.required, Validators.maxLength(100)]],
    currency: ['EUR', [Validators.required, Validators.pattern(/^[A-Za-z]{3}$/)]],
    validFrom: [dateInput(new Date().toISOString()), Validators.required],
    validTo: [''],
    pricePerKwhAc: [null as number | null, amount],
    pricePerKwhDc: [null as number | null, amount],
    pricePerMinute: [null as number | null, amount],
    sessionFee: [null as number | null, amount],
    blockingFeePerMinute: [null as number | null, amount],
    blockingAfterMinutes: [null as number | null, [Validators.min(0), Validators.max(1440)]],
    monthlyFee: [null as number | null, amount],
    roamingOnly: [false],
    operatorIds: [[] as string[]],
  });

  protected readonly emps = signal<Emp[]>([]);
  protected readonly tariffs = signal<Tariff[]>([]);
  protected readonly operators = signal<OperatorSummary[]>([]);
  protected readonly editingEmp = signal(false);
  /** Id of the tariff in the form; null for a new one. */
  protected readonly editingTariff = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly errors = signal<string[]>([]);

  private readonly empNames = computed(() => new Map(this.emps().map((emp) => [emp.key, emp.name])));
  private readonly operatorNames = computed(() => new Map(this.operators().map((operator) => [operator.id, operator.name])));

  constructor() {
    void attempt(this.busy, this.errors, () => this.reload());
  }

  protected empName(key: string): string {
    return this.empNames().get(key) ?? key;
  }

  protected show(value: number | null, currency: string): string {
    return value === null ? '–' : money(value, currency);
  }

  /** Time price, start fee and blocking fee in short. */
  protected extras(tariff: Tariff): string {
    const parts = [
      tariff.pricePerMinute === null ? null : `${money(tariff.pricePerMinute, tariff.currency)}/min`,
      tariff.sessionFee === null ? null : `Start ${money(tariff.sessionFee, tariff.currency)}`,
      tariff.blockingFeePerMinute === null ? null : `Blockieren ${money(tariff.blockingFeePerMinute, tariff.currency)}/min ab ${tariff.blockingAfterMinutes}`,
    ].filter((part) => part !== null);
    return parts.length > 0 ? parts.join(' · ') : '–';
  }

  protected scope(tariff: Tariff): string {
    if (tariff.operatorIds.length > 0) {
      return `nur ${tariff.operatorIds.map((id) => this.operatorNames().get(id) ?? id).join(', ')}`;
    }
    return tariff.roamingOnly ? 'Roaming' : 'überall';
  }

  protected editEmp(emp: Emp): void {
    this.empForm.setValue({ key: emp.key, name: emp.name, website: emp.website ?? '' });
    this.editingEmp.set(true);
  }

  protected resetEmp(): void {
    this.empForm.reset();
    this.editingEmp.set(false);
  }

  protected saveEmp(): Promise<void> {
    return attemptValid(this.empForm, this.busy, this.errors, async () => {
      const { key, name, website } = this.empForm.getRawValue();
      await this.service.putEmp(key, name.trim(), website.trim() || null);
      this.snackBar.open(`Anbieter ${name.trim()} gespeichert.`, 'OK', { duration: 4000 });
      this.resetEmp();
      await this.reload();
    });
  }

  protected removeEmp(emp: Emp): Promise<void> {
    return attempt(this.busy, this.errors, async () => {
      await this.service.deleteEmp(emp.key);
      this.snackBar.open(`Anbieter ${emp.name} gelöscht.`, 'OK', { duration: 4000 });
      await this.reload();
    });
  }

  protected editTariff(tariff: Tariff): void {
    // patchValue ignores the id; the tariff carries every other control.
    this.tariffForm.patchValue({ ...tariff, validFrom: dateInput(tariff.validFrom), validTo: dateInput(tariff.validTo) });
    this.editingTariff.set(tariff.id);
  }

  protected resetTariff(): void {
    this.tariffForm.reset();
    this.editingTariff.set(null);
  }

  protected saveTariff(): Promise<void> {
    return attemptValid(this.tariffForm, this.busy, this.errors, async () => {
      const values = this.tariffForm.getRawValue();
      const tariff: TariffInput = {
        ...values,
        name: values.name.trim(),
        currency: values.currency.toUpperCase(),
        validFrom: isoDate(values.validFrom)!,
        validTo: isoDate(values.validTo),
      };
      await this.service.putTariff(this.editingTariff() ?? crypto.randomUUID(), tariff);
      this.snackBar.open(`Tarif ${tariff.name} gespeichert.`, 'OK', { duration: 4000 });
      this.resetTariff();
      await this.reload();
    });
  }

  protected removeTariff(tariff: Tariff): Promise<void> {
    return attempt(this.busy, this.errors, async () => {
      await this.service.deleteTariff(tariff.id);
      this.snackBar.open(`Tarif ${tariff.name} gelöscht.`, 'OK', { duration: 4000 });
      await this.reload();
    });
  }

  /** EMPs, tariffs and the operators to choose from: the 100 largest and those the tariffs name. */
  private async reload(): Promise<void> {
    const [emps, tariffs, operators] = await Promise.all([this.service.emps(), this.service.tariffs(), this.operatorsService.search('', 100)]);
    const known = new Set(operators.map((operator) => operator.id));
    const missing = [...new Set(tariffs.flatMap((tariff) => tariff.operatorIds))].filter((id) => !known.has(id));
    this.operators.set(missing.length > 0 ? [...operators, ...(await this.operatorsService.byIds(missing))] : operators);
    this.emps.set(emps);
    this.tariffs.set(tariffs);
  }
}
