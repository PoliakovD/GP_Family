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

    /** Редизайн v3 — «один поиск на экране»: страница со своим локальным полем поиска
     * (Анализы/Врачи/Аптечка) подавляет общий поиск шапки, чтобы не показывать два поля разом.
     * Сбрасывается вместе с action() в clear() — иначе «утечёт» на следующий экран без своего
     * поиска (тот же класс бага, для которого уже существует эта симметрия у action). */
    readonly suppressGlobalSearch = signal(false);

    set(action: PageAction): void {
        this.action.set(action);
    }

    setSearchSuppressed(v: boolean): void {
        this.suppressGlobalSearch.set(v);
    }

    clear(): void {
        this.action.set(null);
        this.suppressGlobalSearch.set(false);
    }
}
