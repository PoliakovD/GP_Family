import { Component, OnInit, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AdminApiService, AdminAttention, PipelineJobType, WebSearchTopic } from '../../../services/admin-api.service';
import { AttentionCardComponent } from '../../../shared/attention-card/attention-card.component';
import { ToastService } from '../../../shared/toast/toast.service';
import { ConfirmService } from '../../../shared/confirm/confirm.service';

/** byType-ключи (см. AdminAttentionService.BuildReasonsAsync на бэкенде) — тот же дискриминатор,
 * что PipelineJobType. */
const TYPE_LABELS: Record<string, string> = {
  'lab-analyte': 'показатели',
  medication: 'медикаменты',
  'visit-medication': 'медикаменты из заключений',
  extraction: 'извлечение из документов',
};

/**
 * Инбокс «Требует внимания» (§3/§7 плана) — точка входа админки в разбор падений конвейера
 * обогащения: причины отказа сгруппированы по всем четырём конвейерам, плюс частотные отброшенные
 * домены с действием в один клик («Доверить и перезапустить»). Раньше упавшую задачу приходилось
 * искать вручную на вкладке «Пайплайн» → «Задачи», не зная заранее, что вообще сломано (см. план,
 * Context).
 */
@Component({
  selector: 'app-admin-attention',
  standalone: true,
  imports: [AttentionCardComponent],
  templateUrl: './admin-attention.component.html',
})
export class AdminAttentionComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  readonly attention = signal<AdminAttention | null>(null);
  readonly loading = signal(true);
  readonly busyDomain = signal<string | null>(null);
  readonly purgeBusy = signal(false);

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    try {
      this.attention.set(await this.api.getAttention());
    } catch {
      this.toast.error('Не удалось загрузить сводку.');
    } finally {
      this.loading.set(false);
    }
  }

  typeLabel(type: string): string {
    return TYPE_LABELS[type] ?? type;
  }

  private byTypeEntries(byType: Record<string, number>): [string, number][] {
    return Object.entries(byType).sort((a, b) => b[1] - a[1]);
  }

  /** "9 задач · показатели: 9" — предвычислено в TS, а не в шаблоне: выражения Angular-шаблонов
   * не поддерживают деструктуризацию параметров стрелочной функции ([t, n]) => …. */
  reasonSubtitle(r: AdminAttention['reasons'][number]): string {
    const breakdown = this.byTypeEntries(r.byType)
      .map((entry) => `${this.typeLabel(entry[0])}: ${entry[1]}`)
      .join(', ');
    return `${r.count} задач · ${breakdown}`;
  }

  /** Открывает список задач, отфильтрованный по самому частому типу этой причины — большинство
   * причин в этом справочнике на практике встречаются у одного типа конвейера за раз. */
  openJobs(reason: string, byType: Record<string, number>): void {
    const [topType] = this.byTypeEntries(byType)[0] ?? ['lab-analyte'];
    void this.router.navigate(['/admin/pipeline'], {
      queryParams: { tab: 'jobs', type: topType as PipelineJobType, status: 'Failed', reason },
    });
  }

  /** Задачи "Unclassified" упали ДО появления структурной причины отказа (см.
   * EnrichmentFailureReason) — разбирать их по одной незачем, причины у них нет и не появится;
   * единственное осмысленное действие — очистить список от них насовсем. */
  async purgeUnclassified(): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Удалить задачи без объяснения причины?',
      message: 'Эти задачи упали до того, как конвейер начал записывать причину отказа — разобрать их по одной уже нельзя. Будут удалены насовсем во всех четырёх конвейерах.',
      confirmText: 'Удалить',
      danger: true,
    });
    if (!ok) return;

    this.purgeBusy.set(true);
    try {
      const result = await this.api.purgeUnclassifiedJobs();
      this.toast.success(`Удалено задач: ${result.totalDeleted}.`);
      await this.load();
    } catch {
      this.toast.error('Не удалось удалить задачи.');
    } finally {
      this.purgeBusy.set(false);
    }
  }

  async trustAndRetry(domain: string, topic: string): Promise<void> {
    const topicValue = topic === 'LabAnalyte' ? WebSearchTopic.LabAnalyte : WebSearchTopic.Medication;
    const ok = await this.confirm.confirm({
      title: 'Доверить домен и перезапустить?',
      message: `«${domain}» станет доверенным для темы «${topic}», все Failed-задачи этой темы с причиной «нет доверенных сниппетов» будут перезапущены.`,
      confirmText: 'Доверить и перезапустить',
    });
    if (!ok) return;

    this.busyDomain.set(domain);
    try {
      const result = await this.api.trustDomainsAndRetry(topicValue, [domain]);
      this.toast.success(`Домен добавлен, перезапущено задач: ${result.retriedCount}.`);
      await this.load();
    } catch {
      this.toast.error('Не удалось доверить домен и перезапустить задачи.');
    } finally {
      this.busyDomain.set(null);
    }
  }
}
