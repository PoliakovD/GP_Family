import { Component, OnInit, inject, input } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { WebSearchValveStore } from './web-search-valve.store';

/**
 * Баннер «платный поиск на паузе» — показывается только пока вентиль закрыт. Сам вентиль здесь
 * не переключается: одно место управления — «Настройки → Веб-поиск» (см. WebSearchValveStore).
 * Пауза — не поломка (конвейер возобновится сам при включении), но выглядит как зависший
 * конвейер без единой ошибки, поэтому о ней напоминаем везде, где админ смотрит на задачи.
 */
@Component({
    selector: 'app-web-search-banner',
    imports: [DatePipe, RouterLink],
    template: `
    @if (store.valve(); as valve) {
      @if (valve.isPaused) {
        <div class="card mb-3">
          <div class="card-body d-flex justify-content-between" style="align-items: center; flex-wrap: wrap; gap: 8px;">
            <span class="text-muted">
              Платный поиск на паузе{{ valve.pausedAt ? ' с ' + (valve.pausedAt | date: 'dd.MM.yyyy HH:mm') : '' }}
              @if (deferredTotal() !== null) { — отложено задач: {{ deferredTotal() }} }
              @if (valve.note) { ({{ valve.note }}) }.
              Новые платные вызовы откладываются, всё возобновится само после включения.
            </span>
            <a class="btn btn-secondary btn-sm" routerLink="/admin/settings/web-search">Настройки › Веб-поиск</a>
          </div>
        </div>
      }
    }
  `
})
export class WebSearchBannerComponent implements OnInit {
  readonly store = inject(WebSearchValveStore);

  /** Сколько задач сейчас отложено — известно только инбоксу «Требует внимания». */
  readonly deferredTotal = input<number | null>(null);

  ngOnInit(): void {
    void this.store.load();
  }
}
