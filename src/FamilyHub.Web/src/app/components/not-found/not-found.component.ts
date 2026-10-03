import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

/** Неверный или устаревший адрес (удалённая запись, опечатка в ссылке из чата). */
@Component({
  selector: 'app-not-found',
  standalone: true,
  imports: [RouterLink],
  template: `
    <div class="card" style="max-width:480px;margin:40px auto">
      <h2 class="card-title mb-1">Страница не найдена</h2>
      <p class="muted mb-3">
        Возможно, ссылка устарела или запись уже удалили. Проверьте адрес или вернитесь на Главную.
      </p>
      <a class="btn btn-primary align-self-start" routerLink="/home">На Главную</a>
    </div>
  `,
})
export class NotFoundComponent {}
