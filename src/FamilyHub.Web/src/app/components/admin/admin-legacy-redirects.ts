import { inject } from '@angular/core';
import { Params, RedirectFunction, Router, UrlTree } from '@angular/router';

/**
 * Редиректы со СТАРЫХ адресов админки на новые (перестройка на шесть разделов). Ссылки на
 * конкретную задачу / строку кэша / вызов поиска пересылали и складывали в закладки, поэтому
 * query-параметры сохраняются (кроме `tab` — его роль теперь играет сам путь).
 *
 * Функция в `redirectTo` выполняется в контексте внедрения — отсюда `inject(Router)`.
 */

function redirectTo(target: string, queryParams: Params): UrlTree {
  return inject(Router).createUrlTree(['/admin', ...target.split('/')], { queryParams });
}

function withoutTab(queryParams: Params): Params {
  const { tab: _tab, ...rest } = queryParams;
  return rest;
}

/** Страница, переехавшая целиком, — параметры остаются как были. */
export function redirectPage(target: string): RedirectFunction {
  return ({ queryParams }) => redirectTo(target, queryParams);
}

/** Старая страница «Обогащение» (?tab=domains|cache|rebuild|calls|warmup). Без `tab` открывалась
 * первая вкладка — доверенные домены; ссылки с ?row= / ?call= вели на кэш / журнал вызовов. */
export const redirectEnrichment: RedirectFunction = ({ queryParams }) => {
  const targets: Record<string, string> = {
    domains: 'settings/web-search',
    cache: 'operations/search-cache',
    rebuild: 'operations/rebuilds',
    calls: 'operations/search-calls',
    warmup: 'operations/warmup',
  };
  const tab: string | undefined =
    queryParams['tab'] ?? (queryParams['call'] ? 'calls' : queryParams['row'] ? 'cache' : undefined);
  return redirectTo(targets[tab ?? ''] ?? targets['domains'], withoutTab(queryParams));
};

/** Старая страница «Пайплайн» (?tab=steps|prompts|jobs|lmstudio). Параметры фильтра задач
 * (type/status/reason/job) без `tab` открывали список задач — так ссылки из «Требует внимания» и
 * работали. Без всего этого открывались «Шаги». */
export const redirectPipeline: RedirectFunction = ({ queryParams }) => {
  const targets: Record<string, string> = {
    steps: 'settings/pipeline-steps',
    prompts: 'settings/prompts',
    jobs: 'operations/jobs',
    lmstudio: 'settings/ai-model',
  };
  const hasJobFilter = ['type', 'status', 'reason', 'job'].some((k) => queryParams[k]);
  const tab: string | undefined = queryParams['tab'] ?? (hasJobFilter ? 'jobs' : undefined);
  return redirectTo(targets[tab ?? ''] ?? targets['steps'], withoutTab(queryParams));
};

/** Старый `catalog?tab=…` (три страницы справочника теперь — дочерние роуты). */
export const redirectCatalog: RedirectFunction = ({ queryParams }) => {
  const known = ['analytes', 'medications', 'specimens'];
  const tab: string | undefined = queryParams['tab'];
  return redirectTo(`catalog/${tab && known.includes(tab) ? tab : 'analytes'}`, withoutTab(queryParams));
};
