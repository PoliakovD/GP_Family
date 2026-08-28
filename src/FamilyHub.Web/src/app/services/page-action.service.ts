import {Injectable, signal} from '@angular/core';

/** Контекстное действие в верхней строке каркаса — редизайн v2 (PR2). Топбар не знает про
 * внутренности конкретной страницы: страница сама выставляет своё действие в ngOnInit и
 * обязательно очищает его в ngOnDestroy (иначе оно "протечёт" на следующий экран без действия). */
export interface PageAction {
    label: string;
    icon?: string; // класс Phosphor-иконки, например "ph-bold ph-plus"
    handler: () => void;
}

@Injectable({providedIn: 'root'})
export class PageActionService {
    readonly action = signal<PageAction | null>(null);

    set(action: PageAction): void {
        this.action.set(action);
    }

    clear(): void {
        this.action.set(null);
    }
}
