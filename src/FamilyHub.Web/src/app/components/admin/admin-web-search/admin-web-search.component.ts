import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { AdminApiService, TrustedDomain, WebSearchTopicValue } from '../../../services/admin-api.service';
import { ConfirmService } from '../../../shared/confirm/confirm.service';
import { ToastService } from '../../../shared/toast/toast.service';
import { AdminTopicState } from '../shared/admin-topic-state';
import { TopicSwitchComponent } from '../shared/topic-switch.component';
import { WebSearchValveStore } from '../shared/web-search-valve.store';

/**
 * «Настройки → Веб-поиск»: всё, что определяет, КУДА и ЗА ДЕНЬГИ ходит поиск — вентиль платного
 * поиска (ADR-0005 §9) и доверенные домены по темам. Раньше вентиль жил на вкладке «Вызовы поиска»
 * (по названию — журнал), а домены — на первой вкладке «Обогащения»; теперь они рядом, и вентиль
 * переключается только здесь (в остальных местах — баннер со ссылкой сюда).
 *
 * Домены: переключатель и порядок сохраняются сразу (с «Отменить» для переключателя), удаление
 * переспрашивается. Порядок значим только для анализов (приоритет источника при конфликте норм,
 * см. ReferenceRangeMerger), но UI один на обе темы.
 */
@Component({
  selector: 'app-admin-web-search',
  standalone: true,
  imports: [FormsModule, DatePipe, TopicSwitchComponent],
  templateUrl: './admin-web-search.component.html',
})
export class AdminWebSearchComponent implements OnInit {
  private readonly api = inject(AdminApiService);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  private readonly route = inject(ActivatedRoute);

  readonly valveStore = inject(WebSearchValveStore);
  readonly topicState = inject(AdminTopicState);

  readonly domains = signal<TrustedDomain[]>([]);
  readonly newDomain = signal('');
  readonly domainsLoading = signal(true);
  readonly domainsBusy = signal(false);
  readonly domainsError = signal<string | null>(null);

  ngOnInit(): void {
    this.topicState.syncFromRoute(this.route);
    void this.valveStore.load();
    void this.loadDomains();
  }

  async selectTopic(topic: WebSearchTopicValue): Promise<void> {
    this.topicState.select(topic, this.route);
    await this.loadDomains();
  }

  async loadDomains(): Promise<void> {
    this.domainsLoading.set(true);
    this.domainsError.set(null);
    try {
      this.domains.set(await this.api.getTrustedDomains(this.topicState.topic()));
    } catch {
      this.domainsError.set('Не удалось загрузить список доверенных доменов.');
    } finally {
      this.domainsLoading.set(false);
    }
  }

  async addDomain(): Promise<void> {
    const domain = this.newDomain().trim();
    if (!domain) return;

    this.domainsBusy.set(true);
    try {
      await this.api.addTrustedDomain(this.topicState.topic(), domain);
      this.newDomain.set('');
      await this.loadDomains();
      this.toast.success('Домен добавлен.');
    } catch {
      this.toast.error('Не удалось добавить домен — возможно, он уже в списке.');
    } finally {
      this.domainsBusy.set(false);
    }
  }

  async toggleDomain(d: TrustedDomain, event: Event): Promise<void> {
    // См. AdminPipelineStepsComponent.toggle — нативный чекбокс возвращаем вручную при ошибке.
    const input = event.target as HTMLInputElement;
    const enable = !d.isEnabled;

    this.domainsBusy.set(true);
    try {
      await this.api.setTrustedDomainEnabled(d.id, enable);
      await this.loadDomains();
      this.toast.successWithUndo(
        enable ? `«${d.domain}» включён.` : `«${d.domain}» выключен.`,
        () => this.undoToggle(d, !enable),
      );
    } catch {
      input.checked = d.isEnabled;
      this.toast.error('Не удалось изменить домен.');
    } finally {
      this.domainsBusy.set(false);
    }
  }

  private async undoToggle(d: TrustedDomain, enabled: boolean): Promise<void> {
    try {
      await this.api.setTrustedDomainEnabled(d.id, enabled);
      await this.loadDomains();
      this.toast.info('Изменение отменено.');
    } catch {
      this.toast.error('Не удалось отменить изменение.');
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

  /** Простые стрелки вверх/вниз вместо drag-and-drop. */
  async moveDomain(index: number, direction: -1 | 1): Promise<void> {
    const list = [...this.domains()];
    const target = index + direction;
    if (target < 0 || target >= list.length) return;

    [list[index], list[target]] = [list[target], list[index]];
    this.domains.set(list);

    this.domainsBusy.set(true);
    try {
      await this.api.reorderTrustedDomains(this.topicState.topic(), list.map((d) => d.id));
    } catch {
      this.toast.error('Не удалось сохранить порядок.');
      await this.loadDomains();
    } finally {
      this.domainsBusy.set(false);
    }
  }
}
