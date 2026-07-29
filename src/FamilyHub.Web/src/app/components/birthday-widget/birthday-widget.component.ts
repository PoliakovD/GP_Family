import { Component, OnInit, effect, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiService } from '../../services/api.service';
import { FamilyStateService } from '../../services/family-state.service';
import type { Birthday } from '../../models/types';
import { SearchFieldComponent } from '../../shared/search-field/search-field.component';
import { matchesQuery } from '../../shared/util/local-filter';
import {
  birthdayMetaLabel,
  birthdayUrgencyLabel,
  daysUntilNextBirthday,
} from '../../shared/util/birthday-date';

const TOP_N = 3;

/**
 * «Дни рождения» на Главной (редизайн навигации, +встроенный поиск) — read-only срез по ВСЕМ
 * активным семьям пользователя, в отличие от BirthdaysPanelComponent (полное CRUD по одной
 * семье, семейный саб-таб / отдельная страница /birthdays). Датовая арифметика общая —
 * shared/util/birthday-date.ts, не дублируем. Без запроса — top-N ближайших; с запросом —
 * все совпавшие по имени (по всем семьям сразу, не только среди top-N), тоже по возрастанию
 * дней до наступления. Локальный фильтр, а не /api/search: набор уже загружен целиком (виджет
 * и так тянет birthdays по каждой активной семье), а строить отдельный HTTP-поиск ради
 * содержимого, которое уже в памяти, не нужно.
 */
@Component({
  selector: 'app-birthday-widget',
  standalone: true,
  imports: [RouterLink, SearchFieldComponent],
  templateUrl: './birthday-widget.component.html',
})
export class BirthdayWidgetComponent implements OnInit {
  private readonly api = inject(ApiService);
  private readonly state = inject(FamilyStateService);

  items: Birthday[] = [];
  loading = true;
  searchQuery = '';

  // undefined — ещё ни разу не загружали для текущего набора семей.
  private loadedFamilyIds: string | undefined = undefined;

  constructor() {
    // FamilyStateService.refresh() (шелл) может завершиться уже ПОСЛЕ монтирования виджета —
    // реагируем на появление/смену набора активных семей, а не только на ngOnInit.
    effect(() => {
      const key = this.state.activeFamilies().map((f) => f.id).sort().join(',');
      if (key === this.loadedFamilyIds) return;
      void this.refresh();
    });
  }

  ngOnInit(): void {
    void this.refresh();
  }

  async refresh(): Promise<void> {
    const families = this.state.activeFamilies();
    this.loadedFamilyIds = families.map((f) => f.id).sort().join(',');
    if (families.length === 0) {
      this.items = [];
      this.loading = false;
      return;
    }

    this.loading = true;
    try {
      const perFamily = await Promise.all(families.map((f) => this.api.getBirthdays(f.id)));
      this.items = perFamily.flat();
    } catch {
      // Виджет вспомогательный — при сбое просто не показываем ничего, без баннера ошибки
      // поверх Главной (полный список всё равно доступен на /birthdays).
      this.items = [];
    } finally {
      this.loading = false;
    }
  }

  urgencyLabel(item: Birthday): string {
    return birthdayUrgencyLabel(item.personName, item.date);
  }

  metaLabel(item: Birthday): string {
    return birthdayMetaLabel(item.date);
  }

  /** Топ-N ближайших по всем семьям, отсортированные по дням до наступления. */
  get upcoming(): Birthday[] {
    return this.items
      .slice()
      .sort((a, b) => daysUntilNextBirthday(a.date) - daysUntilNextBirthday(b.date))
      .slice(0, TOP_N);
  }

  /** Без запроса — top-N; с запросом — все совпавшие по имени, без ограничения TOP_N. */
  get displayed(): Birthday[] {
    const q = this.searchQuery.trim();
    if (!q) return this.upcoming;
    return this.items
      .filter((item) => matchesQuery(q, item.personName))
      .sort((a, b) => daysUntilNextBirthday(a.date) - daysUntilNextBirthday(b.date));
  }
}
