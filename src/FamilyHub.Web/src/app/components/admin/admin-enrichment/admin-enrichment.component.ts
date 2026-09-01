import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  AdminApiService,
  SearchCacheDetail,
  SearchCacheRow,
  TrustedDomain,
  WebSearchTopic,
  WebSearchTopicValue,
} from '../../../services/admin-api.service';
import { ToastService } from '../../../shared/toast/toast.service';
import { ConfirmService } from '../../../shared/confirm/confirm.service';

const PAGE_SIZE = 25;

/**
 * Пересборка enrich-пайплайна — управление доверенными доменами и кэшем сырых результатов поиска
 * (провайдер больше не фильтрует по домену сам, кэш хранит ВСЕ сниппеты, см. class doc
 * AdminEnrichmentEndpoints на бэкенде). Один компонент с двумя вкладками (не вложенные роуты —
 * страница второстепенная, обе вкладки делят выбор темы, отдельные URL не нужны).
 */
@Component({
  selector: 'app-admin-enrichment',
  standalone: true,
  imports: [FormsModule, DatePipe],
  templateUrl: './admin-enrichment.component.html',
})
export class AdminEnrichmentComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);

  readonly WebSearchTopic = WebSearchTopic;

  readonly tab = signal<'domains' | 'cache'>('domains');
  readonly topic = signal<WebSearchTopicValue>(WebSearchTopic.Medication);

  readonly domains = signal<TrustedDomain[]>([]);
  readonly newDomain = signal('');
  readonly domainsLoading = signal(true);
  readonly domainsBusy = signal(false);

  readonly cacheRows = signal<SearchCacheRow[]>([]);
  readonly cacheTotal = signal(0);
  readonly cacheQuery = signal('');
  readonly cacheLoading = signal(false);
  readonly cacheDetail = signal<SearchCacheDetail | null>(null);
  readonly cacheDetailLoading = signal(false);

  ngOnInit(): void {
    void this.loadDomains();
  }

  selectTab(tab: 'domains' | 'cache'): void {
    this.tab.set(tab);
    if (tab === 'cache' && this.cacheRows().length === 0) void this.loadCache();
  }

  async selectTopic(topic: WebSearchTopicValue): Promise<void> {
    this.topic.set(topic);
    this.cacheDetail.set(null);
    await Promise.all([this.loadDomains(), this.tab() === 'cache' ? this.loadCache() : Promise.resolve()]);
  }

  async loadDomains(): Promise<void> {
    this.domainsLoading.set(true);
    try {
      this.domains.set(await this.api.getTrustedDomains(this.topic()));
    } catch {
      this.toast.error('Не удалось загрузить список доверенных доменов.');
    } finally {
      this.domainsLoading.set(false);
    }
  }

  async addDomain(): Promise<void> {
    const domain = this.newDomain().trim();
    if (!domain) return;

    this.domainsBusy.set(true);
    try {
      await this.api.addTrustedDomain(this.topic(), domain);
      this.newDomain.set('');
      await this.loadDomains();
      this.toast.success('Домен добавлен.');
    } catch {
      this.toast.error('Не удалось добавить домен — возможно, он уже в списке.');
    } finally {
      this.domainsBusy.set(false);
    }
  }

  async toggleDomain(d: TrustedDomain): Promise<void> {
    this.domainsBusy.set(true);
    try {
      await this.api.setTrustedDomainEnabled(d.id, !d.isEnabled);
      await this.loadDomains();
    } catch {
      this.toast.error('Не удалось изменить домен.');
    } finally {
      this.domainsBusy.set(false);
    }
  }

  async deleteDomain(d: TrustedDomain): Promise<void> {
    const ok = await this.confirm.confirm({
      title: 'Удалить домен?',
      message: `«${d.domain}» будет удалён из списка. Уже закэшированные сниппеты с этого домена останутся в кэше, просто перестанут учитываться по умолчанию.`,
      confirmText: 'Удалить',
      danger: true,
    });
    if (!ok) return;

    this.domainsBusy.set(true);
    try {
      await this.api.deleteTrustedDomain(d.id);
      await this.loadDomains();
    } catch {
      this.toast.error('Не удалось удалить домен.');
    } finally {
      this.domainsBusy.set(false);
    }
  }

  /** Простые стрелки вверх/вниз вместо drag-and-drop — порядок значим только для LabAnalyte
   * (приоритет источника при конфликте норм, см. ReferenceRangeMerger), но UI один на обе темы. */
  async moveDomain(index: number, direction: -1 | 1): Promise<void> {
    const list = [...this.domains()];
    const target = index + direction;
    if (target < 0 || target >= list.length) return;

    [list[index], list[target]] = [list[target], list[index]];
    this.domains.set(list);

    this.domainsBusy.set(true);
    try {
      await this.api.reorderTrustedDomains(this.topic(), list.map((d) => d.id));
    } catch {
      this.toast.error('Не удалось сохранить порядок.');
      await this.loadDomains();
    } finally {
      this.domainsBusy.set(false);
    }
  }

  async loadCache(reset = true): Promise<void> {
    this.cacheLoading.set(true);
    try {
      const skip = reset ? 0 : this.cacheRows().length;
      const page = await this.api.getSearchCache(this.topic(), this.cacheQuery(), skip, PAGE_SIZE);
      this.cacheRows.set(reset ? page.rows : [...this.cacheRows(), ...page.rows]);
      this.cacheTotal.set(page.total);
    } catch {
      this.toast.error('Не удалось загрузить кэш поиска.');
    } finally {
      this.cacheLoading.set(false);
    }
  }

  async openCacheRow(row: SearchCacheRow): Promise<void> {
    this.cacheDetailLoading.set(true);
    this.cacheDetail.set(null);
    try {
      this.cacheDetail.set(await this.api.getSearchCacheDetail(row.id, this.topic()));
    } catch {
      this.toast.error('Не удалось загрузить сниппеты.');
    } finally {
      this.cacheDetailLoading.set(false);
    }
  }

  closeCacheDetail(): void {
    this.cacheDetail.set(null);
  }

  /** Тройной клик по чекбоксу: не задано → включено (override=true) → выключено (override=false) →
   * не задано (override=null, снова решает домен) — проще, чем два отдельных элемента управления. */
  async cycleSnippetOverride(url: string): Promise<void> {
    const detail = this.cacheDetail();
    if (!detail) return;

    const snippet = detail.snippets.find((s) => s.url === url);
    if (!snippet) return;

    const nextOverride = snippet.override === null ? true : snippet.override === true ? false : null;

    try {
      await this.api.setSnippetOverride(detail.id, this.topic(), url, nextOverride);
      this.cacheDetail.set(await this.api.getSearchCacheDetail(detail.id, this.topic()));
    } catch {
      this.toast.error('Не удалось изменить сниппет.');
    }
  }
}
