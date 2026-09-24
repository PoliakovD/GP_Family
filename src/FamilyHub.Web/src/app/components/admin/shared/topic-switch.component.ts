import { Component, input, output } from '@angular/core';
import { WebSearchTopic, WebSearchTopicValue } from '../../../services/admin-api.service';

/**
 * Переключатель «Медикаменты / Анализы» для страниц, где данные зависят от темы веб-поиска
 * (доверенные домены, кэш поиска, журнал вызовов, прогрев). Сам состояние не хранит — страница
 * берёт выбор из AdminTopicState (он же синхронизирует его с `?topic=`).
 */
@Component({
  selector: 'app-topic-switch',
  standalone: true,
  template: `
    <div class="seg">
      <button type="button" class="seg-opt" [class.active]="topic() === WebSearchTopic.Medication"
              (click)="topicChange.emit(WebSearchTopic.Medication)">
        Медикаменты
      </button>
      <button type="button" class="seg-opt" [class.active]="topic() === WebSearchTopic.LabAnalyte"
              (click)="topicChange.emit(WebSearchTopic.LabAnalyte)">
        Анализы
      </button>
    </div>
  `,
})
export class TopicSwitchComponent {
  protected readonly WebSearchTopic = WebSearchTopic;

  readonly topic = input.required<WebSearchTopicValue>();
  readonly topicChange = output<WebSearchTopicValue>();
}
