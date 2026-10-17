import { Component, computed, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSlideToggleChange, MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { attempt } from '../auth/problem';
import { Setting, SettingsService } from './settings.service';

/**
 * One setting in one scope, rendered from its catalog entry: the Material control follows the type (slide toggle,
 * native select, input). A successful save is confirmed by a snackbar; errors stay next to the setting.
 */
@Component({
  imports: [MatButtonModule, MatFormFieldModule, MatInputModule, MatSlideToggleModule],
  selector: 'app-setting-row',
  template: `
    <div>
      <h3>
        {{ setting().key }}
        @if (scope() !== 'global') {
          <span class="scope">{{ scope() }}</span>
        }
      </h3>
      <p class="description">{{ setting().description }}</p>
      <p class="state">
        @if (!storedValue()) {
          Standardwert aktiv
        } @else {
          Eigener Wert gespeichert
        }
      </p>
    </div>
    <div class="control">
      @switch (setting().type) {
        @case ('bool') {
          <mat-slide-toggle
            [checked]="current() === true"
            [disabled]="!canManage()"
            [aria-label]="setting().key"
            (change)="toggle($event)"
          />
        }
        @case ('enum') {
          <mat-form-field subscriptSizing="dynamic">
            <select matNativeControl [disabled]="!canManage()" [attr.aria-label]="setting().key" (change)="edit($event)">
              @for (option of setting().allowedValues; track option) {
                <option [value]="option" [selected]="option === current()">{{ option }}</option>
              }
            </select>
          </mat-form-field>
        }
        @default {
          <mat-form-field subscriptSizing="dynamic">
            <input
              matInput
              [type]="inputType()"
              [value]="current() ?? ''"
              [min]="setting().min"
              [max]="setting().max"
              [placeholder]="placeholder()"
              [disabled]="!canManage()"
              [attr.aria-label]="setting().key"
              [attr.autocomplete]="isSecret() ? 'new-password' : 'off'"
              (input)="edit($event)"
            />
          </mat-form-field>
        }
      }
      @if (canManage()) {
        <button matButton="tonal" type="button" class="save" [disabled]="busy() || draft() === undefined" (click)="save()">
          Speichern
        </button>
        <button matButton type="button" class="reset" [disabled]="busy() || !storedValue()" (click)="reset()">
          Zurücksetzen
        </button>
      }
    </div>
    @for (error of errors(); track error) {
      <p class="error" role="alert">{{ error }}</p>
    }
  `,
})
export class SettingRow {
  private readonly settings = inject(SettingsService);
  private readonly snackBar = inject(MatSnackBar);

  readonly setting = input.required<Setting>();
  /** "global", or e.g. "provider:econtrol" for the settings of a source adapter. */
  readonly scope = input('global');
  readonly canManage = input(false);

  /** Emits the setting as returned by the API after a successful save or reset. */
  readonly changed = output<Setting>();

  /** The edited value; undefined while the user has not changed anything. */
  protected readonly draft = signal<unknown>(undefined);
  protected readonly busy = signal(false);
  protected readonly errors = signal<string[]>([]);

  protected readonly isSecret = computed(() => this.setting().type === 'secret');
  protected readonly isNumeric = computed(() => ['int', 'number'].includes(this.setting().type));

  /** The value stored for this scope; undefined while the default applies. */
  protected readonly storedValue = computed(() => this.setting().stored.find((value) => value.scope === this.scope()));

  /** Global: the effective value from the API; other scopes: their stored value, else the default. */
  protected readonly effective = computed(() =>
    this.scope() === 'global' ? this.setting().value : (this.storedValue()?.value ?? this.setting().default),
  );

  /** A secret is never prefilled: the API only tells whether one is set. */
  protected readonly current = computed(() =>
    this.draft() !== undefined ? this.draft() : this.isSecret() ? '' : this.effective(),
  );
  protected readonly inputType = computed(() =>
    this.isSecret() ? 'password' : this.isNumeric() ? 'number' : 'text',
  );
  protected readonly placeholder = computed(() =>
    this.isSecret() ? (this.storedValue() ? 'gesetzt' : 'nicht gesetzt') : '',
  );

  protected toggle(event: MatSlideToggleChange): void {
    this.draft.set(event.checked);
  }

  protected edit(event: Event): void {
    const control = event.target as HTMLInputElement;
    if (this.isNumeric()) {
      this.draft.set(control.value === '' ? null : Number(control.value));
    } else {
      this.draft.set(control.value);
    }
  }

  protected save(): Promise<void> {
    return this.store(this.draft(), 'gespeichert');
  }

  protected reset(): Promise<void> {
    return this.store(null, 'auf den Standardwert zurückgesetzt');
  }

  private store(value: unknown, done: string): Promise<void> {
    return attempt(this.busy, this.errors, async () => {
      this.changed.emit(await this.settings.update(this.setting().key, value, this.scope()));
      this.draft.set(undefined);
      const scope = this.scope() === 'global' ? '' : ` (${this.scope()})`;
      this.snackBar.open(`${this.setting().key}${scope} ${done}`, 'OK', { duration: 4000 });
    });
  }
}
