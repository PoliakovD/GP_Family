import { Injectable, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { WebSearchTopic, WebSearchTopicValue } from '../../../services/admin-api.service';

/**
 * Выбранная тема (Медикаменты / Анализы) — общая для страниц админки, где данные зависят от темы
 * веб-поиска: доверенные домены, кэш поиска, журнал вызовов, прогрев. Выбор переживает переход
 * между этими страницами (раньше оба «переключателя» жили внутри одного компонента с вкладками),
 * а `?topic=` в URL сохраняет его при F5 и в пересылаемых ссылках.
 */
@Injectable({ providedIn: 'root' })
export class AdminTopicState {
  private readonly router = inject(Router);

  readonly topic = signal<WebSearchTopicValue>(WebSearchTopic.Medication);

  /** Читает `?topic=` при входе на страницу; без параметра остаётся прежний выбор. */
  syncFromRoute(route: ActivatedRoute): void {
    const param = route.snapshot.queryParamMap.get('topic');
    if (param === '0' || param === '1') this.topic.set(Number(param) as WebSearchTopicValue);
  }

  /** Меняет тему и пишет её в URL (replaceUrl — переключение темы не засоряет историю «назад»). */
  select(topic: WebSearchTopicValue, route: ActivatedRoute, extra: Record<string, string | null> = {}): void {
    this.topic.set(topic);
    void this.router.navigate([], {
      relativeTo: route,
      queryParams: { topic: String(topic), ...extra },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }
}
