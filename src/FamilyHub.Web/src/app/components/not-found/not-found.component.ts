import { Component, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink } from '@angular/router';
import { SearchX } from 'lucide';
import { EmptyStateComponent } from '../../shared/ui/empty-state/empty-state.component';

/** Неверный или устаревший адрес (удалённая запись, опечатка в ссылке из чата). */
@Component({
  selector: 'app-not-found',
  standalone: true,
  imports: [RouterLink, EmptyStateComponent],
  changeDetection: ChangeDetectionStrategy.Eager,
  template: `
    <h1 class="visually-hidden">Страница не найдена</h1>
    <app-empty-state
      [icon]="searchX"
      title="Страница не найдена"
      text="Возможно, ссылка устарела или запись уже удалили. Проверьте адрес или вернитесь на Главную.">
      <a class="btn btn-primary" routerLink="/home">На Главную</a>
    </app-empty-state>
  `,
})
export class NotFoundComponent {
  protected readonly searchX = SearchX;
}
