// Цветовая индикация срока годности медикамента — вынесена из MedicationsPanelComponent, чтобы не
// дублировать пороги 30/120 дней в плоском списке результатов поиска Аптечки (MedicationsTabComponent).

/** ≤1 мес — красный, 2-4 мес — жёлтый, дальше — зелёный, без даты — фиолетовый. */
export function expiryClass(expiryDate: string | null | undefined): string {
  if (!expiryDate) return 'expiry-none';

  const daysLeft = Math.floor((new Date(expiryDate).getTime() - Date.now()) / (1000 * 60 * 60 * 24));
  if (daysLeft <= 30) return 'expiry-danger';
  if (daysLeft <= 120) return 'expiry-warning';
  return 'expiry-ok';
}
