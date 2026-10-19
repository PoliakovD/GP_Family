/**
 * Автопроверки правил CLAUDE.md «UI / дизайн» на открытом экране (AUDIT=1 node visual/shots.mjs …):
 * горизонтальная прокрутка страницы, текст < 12px, тап-зона < 44px (только сенсорный экран: на десктопе
 * мышью компактные сегменты/кнопки 36px — по DESIGN §3), иконочная кнопка без имени,
 * контраст текста < 4,5:1 (3:1 для крупного). Возвращает список нарушений — по одному на элемент.
 */
export async function auditPage(page) {
  return page.evaluate(() => {
    const out = [];
    const W = document.documentElement.clientWidth;
    const visible = (el) => {
      const r = el.getBoundingClientRect();
      if (r.width === 0 || r.height === 0) return false;
      const cs = getComputedStyle(el);
      return cs.visibility !== 'hidden' && cs.display !== 'none' && Number(cs.opacity) > 0.05;
    };
    const label = (el) => {
      const cls = typeof el.className === 'string' ? el.className.trim().split(/\s+/).slice(0, 2).join('.') : '';
      const txt = (el.innerText || el.getAttribute('aria-label') || '').trim().replace(/\s+/g, ' ').slice(0, 40);
      return `${el.tagName.toLowerCase()}${cls ? '.' + cls : ''}${txt ? ` «${txt}»` : ''}`;
    };

    // 1. Страница шире экрана.
    if (document.documentElement.scrollWidth > W + 1) {
      const wide = [...document.querySelectorAll('body *')].filter((el) => {
        const r = el.getBoundingClientRect();
        if (r.right <= W + 1 || r.width === 0) return false;
        // Внутри своей горизонтальной прокрутки — это не выход страницы за экран.
        for (let p = el.parentElement; p; p = p.parentElement) {
          const ox = getComputedStyle(p).overflowX;
          if (ox === 'auto' || ox === 'scroll' || ox === 'hidden' || ox === 'clip') return false;
        }
        return true;
      });
      out.push({ rule: 'overflow', el: `страница ${document.documentElement.scrollWidth}px > ${W}px`, detail: wide.slice(0, 3).map(label).join(' | ') });
    }

    // 2. Мелкий текст и контраст — по элементам с собственным текстом.
    const parse = (c) => { const m = c.match(/rgba?\(([^)]+)\)/); if (!m) return null; const [r, g, b, a = 1] = m[1].split(/[ ,/]+/).filter(Boolean).map(Number); return { r, g, b, a }; };
    const lum = ({ r, g, b }) => { const f = (v) => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; }; return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b); };
    const bgOf = (el) => {
      for (let p = el; p; p = p.parentElement) {
        const cs = getComputedStyle(p);
        if (cs.backgroundImage !== 'none') return null; // градиент/картинка — не считаем
        const c = parse(cs.backgroundColor);
        if (c && c.a >= 0.95) return c;
        if (c && c.a > 0.05) return null; // полупрозрачная подложка — не считаем
      }
      return { r: 255, g: 255, b: 255, a: 1 };
    };
    for (const el of document.querySelectorAll('body *')) {
      const own = [...el.childNodes].some((n) => n.nodeType === 3 && n.textContent.trim().length > 1);
      if (!own || !visible(el)) continue;
      if (el.closest('[aria-hidden="true"], svg, app-dev-panel')) continue;
      const cs = getComputedStyle(el);
      const size = parseFloat(cs.fontSize);
      if (size < 12) out.push({ rule: 'text<12', el: label(el), detail: `${size}px` });
      const fg = parse(cs.color);
      const bg = bgOf(el);
      if (!fg || !bg || fg.a < 0.95) continue;
      if (el.closest('button:disabled, [aria-disabled="true"], input:disabled, .disabled')) continue;
      const [l1, l2] = [lum(fg), lum(bg)].sort((a, b) => b - a);
      const ratio = (l1 + 0.05) / (l2 + 0.05);
      const large = size >= 24 || (size >= 18.66 && Number(cs.fontWeight) >= 700);
      if (ratio < (large ? 3 : 4.5)) out.push({ rule: 'contrast', el: label(el), detail: ratio.toFixed(2) });
    }

    // 3. Интерактивные элементы: тап-зона и имя у иконочных кнопок.
    const touch = matchMedia('(pointer: coarse)').matches;
    const sel = 'button, a[href], [role="button"], [role="tab"], input:not([type="hidden"]), select, textarea, summary';
    for (const el of document.querySelectorAll(sel)) {
      if (!visible(el) || el.closest('app-dev-panel')) continue;
      const cs = getComputedStyle(el);
      const r = el.getBoundingClientRect();
      // Ссылка внутри строки текста — исключение WCAG 2.5.8 (inline); visually-hidden input — под своей кнопкой.
      const inline = cs.display === 'inline' && (el.tagName === 'A' || el.classList.contains('link-inline'));
      const hiddenInput = el.tagName === 'INPUT' && r.width <= 2 && r.height <= 2;
      // Скрытый input под label (switch/checkbox/radio) — тап-зона у label.
      const wrapped = el.tagName === 'INPUT' && el.closest('label');
      if (touch && !inline && !wrapped && !hiddenInput && (r.height < 43.5 || r.width < 43.5)) {
        // Зона расширена абсолютным псевдоэлементом (::after { inset: 0 } на всю строку) — считаем по нему.
        const pseudo = ['::before', '::after'].some((p) => { const ps = getComputedStyle(el, p); return ps.content !== 'none' && ps.position === 'absolute'; });
        if (!pseudo) out.push({ rule: 'tap<44', el: label(el), detail: `${Math.round(r.width)}×${Math.round(r.height)}` });
      }
      if ((el.tagName === 'BUTTON' || el.getAttribute('role') === 'button') && !el.innerText.trim()) {
        const named = el.getAttribute('aria-label') || el.getAttribute('aria-labelledby') || el.getAttribute('title');
        if (!named) out.push({ rule: 'no-name', el: label(el), detail: el.outerHTML.slice(0, 80) });
      }
    }
    return out;
  });
}
