import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { AdminApiService, EnrichmentReviewConfigRequest } from '../../../services/admin-api.service';
import { ToastService } from '../../../shared/toast/toast.service';
import { parseThreshold } from './review-helpers';

/** Значения по умолчанию — те же, что EnrichmentReviewConfig.Default*MinConfidence на бэкенде. */
export const REVIEW_CONFIG_DEFAULTS: EnrichmentReviewConfigRequest = {
  medicationQueryMinConfidence: 0.7,
  analyteQueryMinConfidence: 0.7,
  medicationResultMinConfidence: 0.8,
  analyteResultMinConfidence: 0.8,
};

type ConfigKey = keyof EnrichmentReviewConfigRequest;

interface ConfigRow {
  title: string;
  query: ConfigKey;
  result: ConfigKey;
}


/**
 * «Настройки confidence» (ADR-0018): четыре порога — препараты и показатели x этап запроса (страж легитимности/
 * правдоподобности перед платным поиском) и этап результата (суммаризатор). Ниже порога (или без оценки): поиск
 * подсвечивается и идёт в очереди первым, а результат НЕ пишется в справочник до решения админа. Применяются на
 * следующей же задаче (читаются без кэша).
 */
@Component({
  selector: 'app-review-config',
  imports: [FormsModule, DatePipe],
  template: `
    <div class="card">
      <div class="card-title">Настройки confidence</div>
      <div class="card-body">
        <p class="text-muted mb-3">
          «Запрос» — оценка стража перед платным поиском; «Результат» — самооценка суммаризатора. Уверенность ниже порога или
          отсутствующая считается недостаточной: поиск подсвечивается и идёт первым, результат не записывается в справочник,
          пока админ не одобрит или не поправит его.
        </p>
        @if (loading()) {
          <p class="text-muted">Загрузка…</p>
        } @else if (error()) {
          <div class="alert-danger">{{ error() }}</div>
        } @else {
          <table class="table mb-3">
            <thead><tr><th></th><th>Запрос (страж)</th><th>Результат (суммаризатор)</th></tr></thead>
            <tbody>
              @for (row of rows; track row.title) {
                <tr>
                  <td><strong>{{ row.title }}</strong></td>
                  <td>
                    <div class="review-threshold">
                      <input type="range" min="0" max="1" step="0.05" [attr.aria-label]="row.title + ': порог запроса'"
                             [ngModel]="value(row.query)" (ngModelChange)="set(row.query, $event)" [name]="row.query + 'r'" />
                      <input class="input" type="number" min="0" max="1" step="0.05" style="width: 80px;"
                             [ngModel]="value(row.query)" (ngModelChange)="set(row.query, $event)" [name]="row.query + 'n'" />
                    </div>
                  </td>
                  <td>
                    <div class="review-threshold">
                      <input type="range" min="0" max="1" step="0.05" [attr.aria-label]="row.title + ': порог результата'"
                             [ngModel]="value(row.result)" (ngModelChange)="set(row.result, $event)" [name]="row.result + 'r'" />
                      <input class="input" type="number" min="0" max="1" step="0.05" style="width: 80px;"
                             [ngModel]="value(row.result)" (ngModelChange)="set(row.result, $event)" [name]="row.result + 'n'" />
                    </div>
                  </td>
                </tr>
              }
            </tbody>
          </table>
          @if (invalid()) {
            <p class="text-danger">Каждый порог должен быть числом от 0 до 1.</p>
          }
          <div class="d-flex gap-2" style="align-items: center; flex-wrap: wrap;">
            <button type="button" class="btn btn-primary btn-sm" [disabled]="busy() || invalid()" (click)="save()">Сохранить</button>
            <button type="button" class="btn btn-secondary btn-sm" [disabled]="busy()" (click)="resetDefaults()">По умолчанию (70% / 80%)</button>
            @if (updatedAt()) { <span class="text-muted">Сохранено {{ updatedAt() | date: 'dd.MM.yyyy HH:mm' }}</span> }
          </div>
        }
      </div>
    </div>
  `,
  styles: [`
    .review-threshold { display: flex; align-items: center; gap: 8px; }
    .review-threshold input[type='range'] { flex: 1; min-width: 100px; accent-color: var(--color-accent); }
  `],
})
export class ReviewConfigComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);

  readonly rows: ConfigRow[] = [
    { title: 'Препараты', query: 'medicationQueryMinConfidence', result: 'medicationResultMinConfidence' },
    { title: 'Показатели анализов', query: 'analyteQueryMinConfidence', result: 'analyteResultMinConfidence' },
  ];

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);
  readonly updatedAt = signal<string | null>(null);
  private readonly values = signal<Record<ConfigKey, number | string>>({ ...REVIEW_CONFIG_DEFAULTS });

  ngOnInit(): void {
    void this.load();
  }

  value(key: ConfigKey): number | string {
    return this.values()[key];
  }

  set(key: ConfigKey, raw: string | number): void {
    this.values.update((v) => ({ ...v, [key]: raw }));
  }

  invalid(): boolean {
    return (Object.keys(REVIEW_CONFIG_DEFAULTS) as ConfigKey[]).some((k) => parseThreshold(this.values()[k]) === null);
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const cfg = await this.api.getReviewConfig();
      this.values.set({
        medicationQueryMinConfidence: cfg.medicationQueryMinConfidence,
        analyteQueryMinConfidence: cfg.analyteQueryMinConfidence,
        medicationResultMinConfidence: cfg.medicationResultMinConfidence,
        analyteResultMinConfidence: cfg.analyteResultMinConfidence,
      });
      this.updatedAt.set(cfg.updatedAt);
    } catch {
      this.error.set('Не удалось загрузить пороги.');
    } finally {
      this.loading.set(false);
    }
  }

  resetDefaults(): void {
    this.values.set({ ...REVIEW_CONFIG_DEFAULTS });
  }

  async save(): Promise<void> {
    if (this.invalid()) return;
    const v = this.values();
    const request = {} as EnrichmentReviewConfigRequest;
    for (const key of Object.keys(REVIEW_CONFIG_DEFAULTS) as ConfigKey[]) request[key] = parseThreshold(v[key])!;

    this.busy.set(true);
    try {
      const saved = await this.api.setReviewConfig(request);
      this.updatedAt.set(saved.updatedAt);
      this.toast.success('Пороги сохранены — действуют со следующей задачи.');
    } catch {
      this.toast.error('Не удалось сохранить пороги.');
    } finally {
      this.busy.set(false);
    }
  }
}
