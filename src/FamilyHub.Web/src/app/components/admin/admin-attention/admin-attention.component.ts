import { Component, OnInit, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AdminApiService, AdminAttention, PipelineJobType, WebSearchTopic } from '../../../services/admin-api.service';
import { AttentionCardComponent } from '../../../shared/attention-card/attention-card.component';
import { ToastService } from '../../../shared/toast/toast.service';
import { ConfirmService } from '../../../shared/confirm/confirm.service';
import { WebSearchBannerComponent } from '../shared/web-search-banner.component';

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
 * искать вручную в списке задач («Операции → Задачи»), не зная заранее, что вообще сломано (см.
 * план, Context).
 *
 * Закрытый вентиль платного поиска (ADR-0005 §9) выглядел бы иначе как зависший конвейер без единой
 * ошибки, поэтому о нём напоминает баннер; переключается вентиль только в «Настройки → Веб-поиск».
 */
@Component({
  selector: 'app-admin-attention',
  standalone: true,
  imports: [AttentionCardComponent, WebSearchBannerComponent],
  templateUrl: './admin-attention.component.html',
})
export class AdminAttentionComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly router = inject(Router);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  readonly attention = signal<AdminAttention | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly busyDomain = signal<string | null>(null);
  readonly purgeBusy = signal(false);
  readonly dedupeBusy = signal(false);

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.attention.set(await this.api.getAttention());
    } catch {
      this.error.set('Не удалось загрузить сводку.');
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

  /** "9 задач (3 разных названия) · показатели: 9" — предвычислено в TS, а не в шаблоне:
   * выражения Angular-шаблонов не поддерживают деструктуризацию параметров стрелочной функции
   * ([t, n]) => …. Разница count/distinctCount — не бесплатная опечатка: если она есть, значит
   * одно и то же название падало по этой причине несколько раз подряд (см. dedupeFailed()). */
  reasonSubtitle(r: AdminAttention['reasons'][number]): string {
    const breakdown = this.byTypeEntries(r.byType)
      .map((entry) => `${this.typeLabel(entry[0])}: ${entry[1]}`)
      .join(', ');
    const distinct = r.count > r.distinctCount ? ` (${r.distinctCount} разных названий)` : '';
    return `${r.count} задач${distinct} · ${breakdown}`;
  }

  /** Есть хотя бы одна причина, где число задач больше числа разных названий — значит, в
   * системе накопились дубли Failed-строк (до фикса *RequestService их создавалось по одной на
   * каждый повторный прогон), кнопка «Схлопнуть дубли» имеет смысл показать. */
  hasDuplicates(): boolean {
    return (this.attention()?.reasons ?? []).some((r) => r.count > r.distinctCount);
  }

  /** Открывает список задач, отфильтрованный по самому частому типу этой причины — большинство
   * причин в этом справочнике на практике встречаются у одного типа конвейера за раз. */
  openJobs(reason: string, byType: Record<string, number>): void {
    const [topType] = this.byTypeEntries(byType)[0] ?? ['lab-analyte'];
    void this.router.navigate(['/admin/operations/jobs'], {
      queryParams: { type: topType as PipelineJobType, status: 'Failed', reason },
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

  /** Схлопывает УЖЕ накопленные дубли Failed/Skipped-строк — в каждой группе (название[,
   * биоматериал]) остаётся только самая свежая, остальные удаляются насовсем. Новых дублей
   * больше не создаётся (см. class doc *RequestService на бэкенде) — это чистка задним числом. */
  async dedupeFailed(): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Схлопнуть дубли Failed-задач?',
      message: 'Для каждого названия, упавшего несколько раз, останется только самая свежая задача — остальные будут удалены насовсем. Причина отказа последней попытки видна в её карточке.',
      confirmText: 'Схлопнуть',
      danger: true,
    });
    if (!ok) return;

    this.dedupeBusy.set(true);
    try {
      const result = await this.api.dedupeFailedJobs();
      this.toast.success(`Удалено дублей: ${result.totalDeleted}.`);
      await this.load();
    } catch {
      this.toast.error('Не удалось схлопнуть дубли.');
    } finally {
      this.dedupeBusy.set(false);
    }
  }

  /** Тема из инбокса приходит именем enum'а (`LabAnalyte`) — показываем ту же подпись, что на
   * переключателе темы («Анализы» / «Медикаменты»). */
  topicLabel(topic: string): string {
    return topic === 'LabAnalyte' ? 'Анализы' : 'Медикаменты';
  }

  async trustAndRetry(domain: string, topic: string): Promise<void> {
    const topicValue = topic === 'LabAnalyte' ? WebSearchTopic.LabAnalyte : WebSearchTopic.Medication;
    const ok = await this.confirm.confirm({
      title: 'Доверить домен и перезапустить?',
      message: `«${domain}» станет доверенным для темы «${this.topicLabel(topic)}», все упавшие задачи этой темы с причиной «нет доверенных сниппетов» будут перезапущены.`,
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
