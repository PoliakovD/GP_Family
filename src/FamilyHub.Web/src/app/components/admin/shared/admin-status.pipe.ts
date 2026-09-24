import { Pipe, PipeTransform } from '@angular/core';
import { statusLabel } from './admin-labels';

/** `{{ run.status | adminStatus }}` — русская подпись статуса задачи/прогона, см. admin-labels.ts. */
@Pipe({ name: 'adminStatus', standalone: true })
export class AdminStatusPipe implements PipeTransform {
  transform(status: string | null | undefined): string {
    return statusLabel(status);
  }
}
