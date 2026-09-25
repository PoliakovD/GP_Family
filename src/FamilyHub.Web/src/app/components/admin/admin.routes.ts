import { RedirectFunction, Route, Routes } from '@angular/router';
import { unsavedChangesGuard } from '../../services/unsaved-changes.guard';
import type { AdminTab } from './admin-section/admin-section.component';
import {
  redirectCatalog,
  redirectEnrichment,
  redirectPage,
  redirectPipeline,
} from './admin-legacy-redirects';

/**
 * Дочерние роуты `/admin` (грузятся лениво из app.routes.ts, гард `adminGuard` стоит на родителе).
 *
 * Шесть разделов верхнего уровня; у каждого, кроме «Требует внимания», — вторая ступень вкладок
 * (`AdminSectionComponent` рисует её из `data.tabs`). Страницы со сохраняемыми формами
 * (ИИ-модель, Промпты, Учётки БД/MinIO — показанный один раз секрет) закрыты `unsavedChangesGuard`.
 *
 * Старые адреса (`/admin/keys`, `/admin/pipeline?tab=…` и т.д.) сохранены как редиректы — см.
 * admin-legacy-redirects.ts.
 */

const loadSection = () =>
  import('./admin-section/admin-section.component').then((m) => m.AdminSectionComponent);

/** Раздел со второй ступенью вкладок; пустой путь ведёт на первую вкладку (или на `defaultRedirect`). */
function section(
  path: string,
  tabs: AdminTab[],
  children: Route[],
  defaultRedirect: string | RedirectFunction = tabs[0].path,
): Route {
  return {
    path,
    loadComponent: loadSection,
    data: { tabs },
    children: [{ path: '', pathMatch: 'full', redirectTo: defaultRedirect }, ...children],
  };
}

const MONITORING_TABS: AdminTab[] = [
  { path: 'overview', label: 'Обзор' },
  { path: 'storage', label: 'Хранилище' },
  { path: 'services', label: 'Сервисы' },
];

const SECURITY_TABS: AdminTab[] = [
  { path: 'keys', label: 'Ключи и ротация' },
  { path: 'credentials', label: 'Учётки БД/MinIO' },
  { path: 'stats', label: 'Статистика' },
];

const SETTINGS_TABS: AdminTab[] = [
  { path: 'ai-model', label: 'ИИ-модель' },
  { path: 'pipeline-steps', label: 'Шаги пайплайна' },
  { path: 'prompts', label: 'Промпты' },
  { path: 'web-search', label: 'Веб-поиск' },
  { path: 'env', label: 'Конфиг env' },
];

const OPERATIONS_TABS: AdminTab[] = [
  { path: 'jobs', label: 'Задачи' },
  { path: 'warmup', label: 'Прогрев' },
  { path: 'search-cache', label: 'Кэш поиска' },
  { path: 'rebuilds', label: 'Пересборки' },
  { path: 'search-calls', label: 'Журнал вызовов' },
];

const CATALOG_TABS: AdminTab[] = [
  { path: 'analytes', label: 'Показатели' },
  { path: 'medications', label: 'Медикаменты' },
  { path: 'specimens', label: 'Биоматериалы' },
];

const loadCatalog = () =>
  import('./admin-catalog/admin-catalog.component').then((m) => m.AdminCatalogComponent);

export const ADMIN_ROUTES: Routes = [
  { path: '', redirectTo: 'attention', pathMatch: 'full' },

  {
    // Инбокс «Требует внимания» (§3/§7 плана) — точка входа: что сломано в конвейере
    // обогащения и почему, сгруппировано, вместо ручного разбора списка задач построчно.
    path: 'attention',
    loadComponent: () =>
      import('./admin-attention/admin-attention.component').then((m) => m.AdminAttentionComponent),
  },

  section('monitoring', MONITORING_TABS, [
    {
      path: 'overview',
      loadComponent: () => import('./admin-overview/admin-overview.component').then((m) => m.AdminOverviewComponent),
    },
    {
      path: 'storage',
      loadComponent: () => import('./admin-storage/admin-storage.component').then((m) => m.AdminStorageComponent),
    },
    {
      path: 'services',
      loadComponent: () => import('./admin-system/admin-system.component').then((m) => m.AdminSystemComponent),
    },
  ]),

  section('security', SECURITY_TABS, [
    {
      path: 'keys',
      loadComponent: () => import('./admin-keys/admin-keys.component').then((m) => m.AdminKeysComponent),
    },
    {
      // Одноразовый секрет, показанный после «Сгенерировать», нельзя потерять случайным уходом со страницы.
      path: 'credentials',
      canDeactivate: [unsavedChangesGuard],
      loadComponent: () =>
        import('./admin-credentials/admin-credentials.component').then((m) => m.AdminCredentialsComponent),
    },
    {
      path: 'stats',
      loadComponent: () =>
        import('./admin-security-stats/admin-security-stats.component').then((m) => m.AdminSecurityStatsComponent),
    },
  ]),

  section('settings', SETTINGS_TABS, [
    {
      path: 'ai-model',
      canDeactivate: [unsavedChangesGuard],
      loadComponent: () => import('./admin-ai-model/admin-ai-model.component').then((m) => m.AdminAiModelComponent),
    },
    {
      path: 'pipeline-steps',
      loadComponent: () =>
        import('./admin-pipeline-steps/admin-pipeline-steps.component').then((m) => m.AdminPipelineStepsComponent),
    },
    {
      path: 'prompts',
      canDeactivate: [unsavedChangesGuard],
      loadComponent: () => import('./admin-prompts/admin-prompts.component').then((m) => m.AdminPromptsComponent),
    },
    {
      path: 'web-search',
      loadComponent: () =>
        import('./admin-web-search/admin-web-search.component').then((m) => m.AdminWebSearchComponent),
    },
    {
      path: 'env',
      loadComponent: () => import('./admin-config-env/admin-config-env.component').then((m) => m.AdminConfigEnvComponent),
    },
  ]),

  section('operations', OPERATIONS_TABS, [
    {
      path: 'jobs',
      loadComponent: () => import('./admin-jobs/admin-jobs.component').then((m) => m.AdminJobsComponent),
    },
    {
      path: 'warmup',
      loadComponent: () => import('./admin-warmup/admin-warmup.component').then((m) => m.AdminWarmupComponent),
    },
    {
      path: 'search-cache',
      loadComponent: () =>
        import('./admin-search-cache/admin-search-cache.component').then((m) => m.AdminSearchCacheComponent),
    },
    {
      path: 'rebuilds',
      loadComponent: () => import('./admin-rebuilds/admin-rebuilds.component').then((m) => m.AdminRebuildsComponent),
    },
    {
      path: 'search-calls',
      loadComponent: () =>
        import('./admin-search-calls/admin-search-calls.component').then((m) => m.AdminSearchCallsComponent),
    },
  ]),

  // Ручная правка справочников после ИИ (§3 плана). Три страницы — один компонент, какая именно
  // открыта, определяет `data.tab`; старый `catalog?tab=…` обрабатывает redirectCatalog.
  section(
    'catalog',
    CATALOG_TABS,
    CATALOG_TABS.map((t) => ({ path: t.path, data: { tab: t.path }, loadComponent: loadCatalog })),
    redirectCatalog,
  ),

  // Старые адреса (до перестройки на шесть разделов) — закладки и пересланные ссылки живы.
  { path: 'overview', redirectTo: redirectPage('monitoring/overview') },
  { path: 'storage', redirectTo: redirectPage('monitoring/storage') },
  { path: 'system', redirectTo: redirectPage('monitoring/services') },
  { path: 'keys', redirectTo: redirectPage('security/keys') },
  { path: 'enrichment', redirectTo: redirectEnrichment },
  { path: 'pipeline', redirectTo: redirectPipeline },
];
