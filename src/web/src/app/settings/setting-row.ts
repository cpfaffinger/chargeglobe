import { Component, computed, inject, input, output, signal } from '@angular/core';
import { attempt } from '../auth/problem';
import { Setting, SettingsService } from './settings.service';

/** One setting, rendered from its catalog entry: the input control follows the type. */
@Component({
  selector: 'app-setting-row',
  template: `
    <h3>{{ setting().key }}</h3>
    <p class="description">{{ setting().description }}</p>
    <div class="control">
      @switch (setting().type) {
        @case ('bool') {
          <input
            type="checkbox"
            [checked]="current() === true"
            [disabled]="!canManage()"
            [attr.aria-label]="setting().key"
            (change)="edit($event)"
          />
        }
        @case ('enum') {
          <select [disabled]="!canManage()" [attr.aria-label]="setting().key" (change)="edit($event)">
            @for (option of setting().allowedValues; track option) {
              <option [value]="option" [selected]="option === current()">{{ option }}</option>
            }
          </select>
        }
        @default {
          <input
            [type]="inputType()"
            [value]="current() ?? ''"
            [min]="setting().min"
            [max]="setting().max"
            [placeholder]="placeholder()"
            [disabled]="!canManage()"
            [attr.aria-label]="setting().key"
            autocomplete="off"
            (input)="edit($event)"
          />
        }
      }
      @if (canManage()) {
        <button type="button" class="save" [disabled]="busy() || draft() === undefined" (click)="save()">
          Speichern
        </button>
        <button type="button" class="reset" [disabled]="busy() || setting().stored.length === 0" (click)="reset()">
          Zurücksetzen
        </button>
      }
    </div>
    <p class="state">
      @if (setting().stored.length === 0) {
        Standardwert aktiv
      } @else {
        Eigener Wert gespeichert
      }
      @if (saved()) {
        <span class="saved" role="status">· gespeichert</span>
      }
    </p>
    @for (error of errors(); track error) {
      <p class="error" role="alert">{{ error }}</p>
    }
  `,
})
export class SettingRow {
  private readonly settings = inject(SettingsService);

  readonly setting = input.required<Setting>();
  readonly canManage = input(false);

  /** Emits the setting as returned by the API after a successful save or reset. */
  readonly changed = output<Setting>();

  /** The edited value; undefined while the user has not changed anything. */
  protected readonly draft = signal<unknown>(undefined);
  protected readonly busy = signal(false);
  protected readonly errors = signal<string[]>([]);
  protected readonly saved = signal(false);

  protected readonly isSecret = computed(() => this.setting().type === 'secret');
  protected readonly isNumeric = computed(() => ['int', 'number'].includes(this.setting().type));

  /** A secret is never prefilled: the API only tells whether one is set. */
  protected readonly current = computed(() =>
    this.draft() !== undefined ? this.draft() : this.isSecret() ? '' : this.setting().value,
  );
  protected readonly inputType = computed(() =>
    this.isSecret() ? 'password' : this.isNumeric() ? 'number' : 'text',
  );
  protected readonly placeholder = computed(() =>
    this.isSecret() ? (this.setting().value === null ? 'nicht gesetzt' : 'gesetzt') : '',
  );

  protected edit(event: Event): void {
    const control = event.target as HTMLInputElement;
    this.saved.set(false);
    if (this.setting().type === 'bool') {
      this.draft.set(control.checked);
    } else if (this.isNumeric()) {
      this.draft.set(control.value === '' ? null : Number(control.value));
    } else {
      this.draft.set(control.value);
    }
  }

  protected save(): Promise<void> {
    return this.store(this.draft());
  }

  protected reset(): Promise<void> {
    return this.store(null);
  }

  private store(value: unknown): Promise<void> {
    return attempt(this.busy, this.errors, async () => {
      this.changed.emit(await this.settings.update(this.setting().key, value));
      this.draft.set(undefined);
      this.saved.set(true);
    });
  }
}
