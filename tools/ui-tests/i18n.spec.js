// Localisation gaps the first i18n pass left behind: literals the dictionary already translates, and one spot
// that compared a localized label against the English word. Against the web demo, in Spanish.
const { test, expect } = require('@playwright/test');

async function show(page, widgets, display = { language: 'es' }) {
  await page.goto('/panel/');
  await page.evaluate(({ widgets, display }) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      display,
      pages: [{ id: 'one', title: 'One', widgets }],
    }));
  }, { widgets, display });
  await page.reload();
}

test('a small agenda shows relative times in the panel language', async ({ page }) => {
  await page.addInitScript(() => {
    // pin the run to local noon so "today" cannot roll over mid-test, and answer /api/calendar
    // with the fixture before the demo installs its own fetch mock (the setter keeps it as fallback)
    const noon = new Date(); noon.setHours(12, 0, 0, 0);
    const FIXED = noon.getTime();
    const RealDate = Date;
    window.Date = class extends RealDate {
      constructor(...args) { super(...(args.length ? args : [FIXED])); }
      static now() { return FIXED; }
    };
    const cal = JSON.stringify({
      updated: FIXED, errors: [],
      events: [{ title: 'Review', start: FIXED + 35 * 60e3, end: FIXED + 65 * 60e3, allDay: false, calendar: 'Work', color: '#60a5fa' }],
    });
    let inner = window.fetch;
    Object.defineProperty(window, 'fetch', {
      configurable: true,
      // the fallback is fixed when the wrapper is handed out: demo.js keeps the first one as its "real" fetch, and a
      // wrapper that looked up the live mock would send the demo's pass-through requests back to the demo forever
      get: () => {
        const fallback = inner;
        return (input, init) => {
          const url = typeof input === 'string' ? input : input.url;
          if (url.includes('/api/calendar')) {
            return Promise.resolve(new Response(cal, { status: 200, headers: { 'Content-Type': 'application/json' } }));
          }
          return fallback(input, init);
        };
      },
      set: (v) => { inner = v; },
    });
  });
  await show(page, [{ id: 'ag', type: 'agenda', x: 0, y: 0, w: 12, h: 1, options: {} }]);
  await page.evaluate(() => window.PhoneBoxDemo.emit('refresh'));
  const sub = page.locator('.ag-big-s').first();
  await expect(sub).toHaveText(/^en \d+ min/); // Spanish "en 35 min · 12:35", not "Hoy · 12:35"
  await expect(sub).not.toHaveText(/Hoy/);
});

test('the breathe widget greets in the panel language before the first tap', async ({ page }) => {
  await show(page, [{ id: 'br', type: 'breathe', x: 0, y: 0, w: 4, h: 2, options: {} }]);
  await expect(page.locator('.br-w')).toHaveText('Toca para iniciar');
});

// A copied English label can pass a key-coverage check, so check the translations and their placeholders too.
test('every translated key is really translated, and the rest fall back to English', async ({ page }) => {
  await page.goto('/panel/');
  const result = await page.evaluate(() => {
    const I18n = window.PhoneBoxI18n;
    const S = I18n.STRINGS;
    const en = S.en;
    const gaps = I18n.LANGUAGES.flatMap(({ code: lang }) =>
      lang === 'en' ? [] : Object.keys(en)
        .filter((k) => typeof S[lang]?.[k] !== 'string' || !S[lang][k].trim())
        .map((k) => `${lang}:${k}`));
    // a key whose text is identical to English is either a name that is not translated (Wi-Fi, USB, Client secret) or
    // padding, so a sentence copied over from English is a real gap in any of the dictionaries.
    const SAME_IS_FINE = new Set(['s.discord.clientSecret', 'status.wiFi', 'status.usb', 's.cd.clientId']);
    const copied = Object.keys(S).filter((lang) => lang !== 'en').flatMap((lang) =>
      Object.keys(S[lang])
        .filter((k) => S[lang][k] === en[k] && !SAME_IS_FINE.has(k) && en[k].length > 12 && /[a-z]{4,} [a-z]{3,}/.test(en[k]))
        .map((k) => `${lang}:${k}`));
    const placeholders = (text) => (text.match(/\{[\w.]+\}/g) || []).sort().join();
    const changedPlaceholders = Object.keys(S).filter((lang) => lang !== 'en').flatMap((lang) =>
      Object.keys(en).filter((k) => typeof S[lang][k] === 'string' &&
        placeholders(S[lang][k]) !== placeholders(en[k])).map((k) => `${lang}:${k}`));
    // An English key can ship before its translation.
    I18n.setLang('es');
    S.en['test.englishFallback'] = 'English fallback';
    const fallback = I18n.t('test.englishFallback');
    delete S.en['test.englishFallback'];
    I18n.setLang('pt-BR');
    const translated = I18n.t('s.js.addWidget');
    return { gaps, copied, changedPlaceholders, fallback, translated };
  });
  expect(result.gaps).toEqual([]);
  expect(result.copied).toEqual([]);
  expect(result.changedPlaceholders).toEqual([]);
  expect(result.fallback).toBe('English fallback');
  expect(result.translated).toBe('Adicionar um widget');
});

// Both remaining dictionaries are complete, so check each one exactly.
for (const lang of ['es', 'pt-BR']) {
test(`the ${lang} dictionary is complete`, async ({ page }) => {
  await page.goto('/panel/');
  const gaps = await page.evaluate((lang) => {
    const S = window.PhoneBoxI18n.STRINGS;
    return Object.keys(S.en).filter((k) => !(k in S[lang]));
  }, lang);
  expect(gaps).toEqual([]);
});
}

test('the widget catalog translates by its English text', async ({ page }) => {
  await page.goto('/panel/');
  const out = await page.evaluate(() => {
    const I18n = window.PhoneBoxI18n;
    I18n.setLang('es');
    return {
      name: I18n.tc('Clock'),
      desc: I18n.tc('Clock, weather, now playing and PC tiles.'),
      category: I18n.tc('Weather & sky'),
      untouched: I18n.tc('not in the catalog'),
      // the catalog is keyed by English, so another language keeps the English name rather than a wrong one
      other: (I18n.setLang('en'), I18n.tc('Clock')),
    };
  });
  expect(out.name).toBe('Reloj');
  expect(out.desc).toContain('Reloj');
  expect(out.category).toBe('Tiempo & cielo');
  expect(out.untouched).toBe('not in the catalog');
  expect(out.other).toBe('Clock');
});

test('the widget catalog translates by its English text (pt-BR)', async ({ page }) => {
  await page.goto('/panel/');
  const out = await page.evaluate(() => {
    const I18n = window.PhoneBoxI18n;
    I18n.setLang('pt-BR');
    return {
      name: I18n.tc('Clock'),
      desc: I18n.tc('Clock, weather, now playing and PC tiles.'),
      category: I18n.tc('Weather & sky'),
      untouched: I18n.tc('not in the catalog'),
    };
  });
  expect(out.name).toBe('Relógio');
  expect(out.desc).toContain('Relógio');
  expect(out.category).toBe('Clima & céu');
  expect(out.untouched).toBe('not in the catalog');
});

// A JavaScript object literal keeps the last of two equal keys without a warning, so a key defined twice in one
// language silently replaces the first text (s.ha.hint once showed the token setup paragraph in the entity picker).
test('i18n.js defines every key once per language', () => {
  const fs = require('fs');
  const path = require('path');
  const lines = fs.readFileSync(path.join(__dirname, '..', '..', 'android', 'app', 'src', 'main', 'assets', 'www', 'i18n.js'), 'utf8').split('\n');
  // EXTRA is merged into STRINGS with Object.assign, so the two share one key space per language; CATALOG is its own.
  const TABLE = /^ {2}const ([A-Z_]+) = \{\s*$/;
  // A property name as source text: 'single', "double" or bare. name() turns it into the name JavaScript uses, so
  // Time, 'Time' and "Time" (or es and 'es') count as the same key.
  const NAME = String.raw`'(?:[^'\\]|\\.)*'|"(?:[^"\\]|\\.)*"|[A-Za-z_$][\w$]*`;
  const BLOCK = new RegExp(String.raw`^ {4}(${NAME}):\s*\{\s*$`);
  const KEY = new RegExp(String.raw`^ {6}(${NAME}):`);
  const name = (token) => (/^['"]/.test(token) ? Function(`"use strict"; return (${token});`)() : token);
  const dupes = [];
  const spaces = new Map();
  let table = null, block = null;
  lines.forEach((line, i) => {
    const tb = TABLE.exec(line);
    if (tb) { table = tb[1] === 'EXTRA' ? 'STRINGS' : tb[1]; block = null; return; }
    const b = BLOCK.exec(line);
    if (b) { block = `${table} ${name(b[1])}`; if (!spaces.has(block)) spaces.set(block, new Map()); return; }
    const k = block && KEY.exec(line);
    if (!k) return;
    const key = name(k[1]);
    const seen = spaces.get(block);
    if (seen.has(key)) dupes.push(`${block} ${k[1]} (lines ${seen.get(key)} and ${i + 1})`);
    else seen.set(key, i + 1);
  });
  expect(spaces.size).toBeGreaterThanOrEqual(8); // the parser still finds the language blocks of every table
  expect(dupes).toEqual([]);
});

test('translations preserve literal dollar tokens in session names', async ({ page }) => {
  await page.goto('/panel/i18n.js');
  await page.addScriptTag({ url: '/panel/i18n.js' });
  const result = await page.evaluate(() => {
    const i = window.PhoneBoxI18n;
    i.setLang('en');
    return ['Build', 'Budget $&', '$$', '$`', "$'", '', 0, null].map(name => i.t('panel.sessionFinished', { name }));
  });
  expect(result).toEqual(['Build finished', 'Budget $& finished', '$$ finished', '$` finished', "$' finished", ' finished', '0 finished', ' finished']);
});

test('both settings language selectors stay in sync and Revert restores the saved language', async ({ page }) => {
  await page.goto('/panel/');
  await page.evaluate(async () => {
    localStorage.clear();
    const config = await (await fetch('/api/settings')).json();
    config.display.language = 'en';
    config.controls.actions = [];
    config.controls.profiles = [];
    localStorage.setItem('pb-demo-config', JSON.stringify(config));
  });
  await page.goto('/settings/#general');
  await expect(page.locator('#langSel')).toHaveValue('en');
  await page.locator('#displayLangSel').selectOption('es');
  await expect(page.locator('#langSel')).toHaveValue('es');
  await expect(page.locator('#save')).toBeEnabled();
  await expect(page.locator('[data-bind="display.theme"]')).toBeVisible();
  await page.locator('#revert').click();
  await expect(page.locator('#langSel')).toHaveValue('en');
  await expect(page.locator('#displayLangSel')).toHaveValue('en');
  await expect(page.locator('#save')).toBeDisabled();
});

// A widget title the user typed keeps its own letters: text-transform uppercases it for the language on screen.
test('a widget title typed by the user keeps its accented letters when uppercased', async ({ page }) => {
  await page.goto('/panel/');
  await page.evaluate(() => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      display: { language: 'es' },
      pages: [{ id: 'one', title: 'One', widgets: [
        { id: 'inbox', type: 'inbox', x: 0, y: 0, w: 6, h: 4 },
        { id: 'sw', type: 'stopwatch', x: 6, y: 0, w: 3, h: 3 },
        { id: 'tm', type: 'timer', x: 9, y: 0, w: 3, h: 3, options: { label: 'café' } },
      ] }],
    }));
  });
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('lang', 'es');
  // innerText is the rendered text, so it shows what text-transform made of it; the widgets are laid out in order
  const title = (i) => page.locator('.w-title').nth(i).evaluate((e) => e.innerText.split('\n')[0].trim());
  await expect.poll(() => title(2)).toBe('CAFÉ');
});

test('the saved Portuguese preference reaches the settings and the phone panel', async ({ page }) => {
  await page.goto('/settings/#general');
  await expect(page.locator('#save')).toBeDisabled();
  await page.locator('#langSel').selectOption('pt-BR');
  await expect(page.locator('#save')).toHaveText('Salvar');
  await page.locator('#save').click();
  await expect(page.locator('#save')).toBeDisabled();
  await page.reload();
  await expect(page.locator('#displayLangSel')).toHaveValue('pt-BR');
  await page.goto('/panel/');
  await expect(page.locator('html')).toHaveAttribute('lang', 'pt-BR');
  await expect(page.locator('#dots .page-all')).toHaveAttribute('title', /Todas as páginas/);
});

// Following the system: an exact tag wins, every other Portuguese falls back to the Brazilian dictionary, and a
// language the panel does not have (or a name Object already carries) reads English.
test('follow system matches the tag, keeps Portuguese together and falls back to English', async ({ page }) => {
  await page.goto('/panel/');
  const resolved = await page.evaluate(() => {
    const i18n = window.PhoneBoxI18n;
    return ['pt-BR', 'pt-PT', 'pt', 'es-MX', 'en-GB', 'de-DE', 'xx', 'constructor'].map((tag) => {
      Object.defineProperty(navigator, 'languages', { value: [tag], configurable: true });
      return i18n.resolveLang('auto');
    });
  });
  expect(resolved).toEqual(['pt-BR', 'pt-BR', 'pt-BR', 'es', 'en', 'en', 'en', 'en']);
});


for (const [language, month, periods] of [['es', /enero/, ['a. m.', 'p. m.']], ['en', /January/, ['am', 'pm']]]) {
  test(`the clock is centered and keeps time in order (${language})`, async ({ page }) => {
    await page.route('https://api.open-meteo.com/v1/forecast?*', route => route.fulfill({ json: {
      current: { weather_code: 0, is_day: 1, temperature_2m: 16, apparent_temperature: 15 },
      daily: { temperature_2m_max: [19], temperature_2m_min: [11] },
    } }));
    await show(page, [
      { id: 'clock', type: 'clock', x: 0, y: 0, w: 6, h: 4, options: { providers: [], align: 'center' } },
      { id: 'media', type: 'media', x: 6, y: 0, w: 6, h: 4 },
    ], { language, hour12: true });
    const clock = page.locator('.w-clock .clock');
    const date = page.locator('.w-clock .date');

    for (const [index, hour] of [10, 22].entries()) {
      const time = await page.evaluate((hour) => new Date(2026, 0, 1, hour, 24, 35).getTime(), hour);
      await page.clock.setFixedTime(time);
      await page.evaluate(async () => (await import('/panel/main.js')).tick());
      await expect(clock.locator('span')).toHaveText(['10', ':', '24', '35', periods[index]]);
      await expect(page.locator('#nClock')).toHaveText(`10:24 ${periods[index]}`);
      const { left, right, midpoint, ordered } = await clock.evaluate((row) => {
        const { left, right } = row.getBoundingClientRect();
        const parts = [...row.children].map((part) => part.getBoundingClientRect());
        return { left, right, midpoint: (parts[0].left + parts.at(-1).right) / 2,
          ordered: parts.every((part, i) => i === 0 || part.left >= parts[i - 1].right - 1) };
      });
      expect(ordered).toBe(true);
      expect(Math.abs(midpoint - (left + right) / 2)).toBeLessThan(2);
    }

    const widget = page.locator('.w-clock');
    await expect(widget.locator('.status .dot.on')).toBeVisible();
    await expect(widget.locator('.wx-temp')).toHaveText('16°');
    await expect(widget.locator('.wx-icon svg')).toBeVisible();
    await expect(widget.locator('.wx-desc')).not.toBeEmpty();
    await expect(widget.locator('.wx-range')).toContainText('15');
    for (const width of [1000, 600]) {
      await page.setViewportSize({ width, height: 420 });
      await page.evaluate(async () => (await import('/panel/main.js')).emit('state'));
      await expect(widget.locator('.status .dot.on')).toBeVisible();
      const centers = await widget.evaluate((box) => {
        const center = (rect) => (rect.left + rect.right) / 2;
        const expected = center(box.getBoundingClientRect());
        return Object.fromEntries(['.status', '.clock', '.wx'].map(selector => {
          const row = box.querySelector(selector);
          const children = [...row.children].filter(child => child.getClientRects().length);
          const rects = children.map(child => child.getBoundingClientRect());
          const content = { left: Math.min(...rects.map(rect => rect.left)), right: Math.max(...rects.map(rect => rect.right)) };
          if (selector === '.status') {
            // The status row clips overflow. Measure the part the browser actually shows.
            const clip = row.getBoundingClientRect();
            content.left = Math.max(content.left, clip.left);
            content.right = Math.min(content.right, clip.right);
          }
          return [selector, Math.abs(center(content) - expected)];
        }));
      });
      for (const [row, offset] of Object.entries(centers)) expect(offset, `${row} at viewport ${width}`).toBeLessThan(2);
      await expect(date).toHaveCSS('text-align', 'center');
      const bounds = await widget.boundingBox();
      const media = await page.locator('.w-media').boundingBox();
      expect(media.x).toBeGreaterThanOrEqual(bounds.x + bounds.width - 1);
    }

    await expect(date).toHaveText(month);
  });
}

for (const [language, period] of [['es', 'p. m.'], ['en', 'pm']]) {
  test(`clock start alignment holds for old and new settings (${language})`, async ({ page }) => {
    await page.setViewportSize({ width: 1200, height: 720 });
    await page.clock.setFixedTime(new Date(2026, 0, 1, 22, 24, 35));
    await page.route('https://api.open-meteo.com/v1/forecast?*', route => route.fulfill({ json: {
      current: { weather_code: 0, is_day: 1, temperature_2m: 16, apparent_temperature: 15 },
      daily: { temperature_2m_max: [19], temperature_2m_min: [11] },
    } }));
    await show(page, [undefined, 'left', 'start'].map((align, i) => ({
      id: `start-${i}`, type: 'clock', x: (i % 2) * 6, y: Math.floor(i / 2) * 4,
      w: 6, h: 4, options: { providers: [], align },
    })), { language, hour12: true });

    const widgets = page.locator('.w-clock');
    await expect(widgets).toHaveCount(3);
    for (const widget of await widgets.all()) {
      await expect(widget).not.toHaveClass(/clock-centered/);
      await expect(widget.locator('.status .dot.on')).toBeVisible();
      await expect(widget.locator('.wx-temp')).toHaveText('16°');
      await expect(widget.locator('.clock span')).toHaveText(['10', ':', '24', '35', period]);
      await expect(widget.locator('.date')).toHaveCSS('text-align', 'start');
      const offsets = await widget.evaluate((box) =>
        ['.status', '.clock', '.date', '.wx'].map(selector => {
          const row = box.querySelector(selector);
          const bounds = row.getBoundingClientRect();
          const content = selector === '.date'
            ? (() => { const range = document.createRange(); range.selectNodeContents(row); return range.getBoundingClientRect(); })()
            : row.firstElementChild.getBoundingClientRect();
          return Math.abs(content.left - bounds.left);
        }));
      for (const offset of offsets) expect(offset).toBeLessThan(2);
    }
  });
}

test('the clock uses the day period of the panel language', async ({ page }) => {
  await page.clock.setFixedTime(new Date(2026, 0, 1, 22, 24, 35));
  await show(page, [{ id: 'es', type: 'clock', x: 0, y: 0, w: 6, h: 4,
    options: { status: false, weather: false } }], { language: 'es', hour12: true });
  await expect(page.locator('.w-clock .ap')).toHaveText('p. m.');
});

test('the clock falls back to AM/PM when Intl has no day period', async ({ page }) => {
  await page.addInitScript(() => {
    Intl.DateTimeFormat = class extends Intl.DateTimeFormat {
      formatToParts() { return []; }
    };
  });
  await page.goto('/panel/');
  const fallback = await page.evaluate(async () => {
    const { clockPeriod } = await import('/panel/main.js');
    return [10, 22].map(hour => clockPeriod(new Date(2026, 0, 1, hour)));
  });
  expect(fallback).toEqual(['AM', 'PM']);
});

test('the clock alignment setting reads legacy left as Start and saves Center', async ({ page }) => {
  await page.goto('/settings/#pages');
  await page.waitForFunction(() => cfg && cfg.pages);
  await page.evaluate(() => {
    const p = curPage();
    p.widgets.push({ id: 'align-test', type: 'clock', x: 0, y: 0, w: 6, h: 4 });
    selWidget = p.widgets.length - 1;
    renderInspector();
  });
  const align = page.locator('#inspector .field', { has: page.getByText('Align', { exact: true }) }).locator('select');
  await expect(align).toHaveValue('start');
  await expect(align.locator('option')).toHaveText(['Start', 'Center']);
  await page.evaluate(() => {
    curPage().widgets[selWidget].options = { align: 'left' };
    renderInspector();
  });
  await expect(align).toHaveValue('start');
  await expect(align.locator('option')).toHaveCount(2);
  await align.selectOption('center');
  await page.locator('#save').click();
  await expect(page.locator('#save')).toBeDisabled();
  const saved = await page.evaluate(async () => {
    const config = await (await fetch('/api/settings')).json();
    return config.pages.flatMap(p => p.widgets).find(w => w.id === 'align-test')?.options?.align;
  });
  expect(saved).toBe('center');
});

test('the idle media widget uses the localized nothing-playing label', async ({ page }) => {
  await show(page, [{ id: 'media', type: 'media', x: 0, y: 0, w: 8, h: 4, options: {} }], { language: 'es' });
  await expect(page.locator('html')).toHaveAttribute('lang', 'es');
  await expect(page.locator('.w-media')).toBeVisible();
  const label = await page.evaluate(async () => {
    const main = await import('/panel/main.js');
    main.D.state = { ...main.D.state, media: null };
    main.emit('state');
    return document.querySelector('.w-media .m-title').textContent;
  });
  expect(label).toBe('No suena nada');
});

// The heading is built by replacing {title} with the saved page title, so characters a replacement treats specially
// ($&, $`) have to come through as typed.
test('the template dialog inserts the saved page title literally', async ({ page }) => {
  const title = 'Budget $& <2026> "Q4"';
  await page.addInitScript((title) => {
    localStorage.setItem('pb-demo-config', JSON.stringify({
      display: { language: 'pt-BR' },
      pages: [{ id: 'home', title, widgets: [] }],
    }));
  }, title);
  await page.goto('/settings/#pages');
  await expect(page.locator('#peHead input[type="text"]').nth(1)).toHaveValue(title);
  await page.locator('#peHead').getByRole('button', { name: 'Substituir por modelo…' }).click();
  await expect(page.locator('#modalBody h3')).toContainText(title);
});

// The button's label is translated while the shortcut's own name, which the user typed, stays as it is.
test('the shortcut delete button is localized without changing the shortcut name', async ({ page }) => {
  await page.goto('/settings/#shortcuts');
  await page.locator('#deck .key:not(.add)').first().click();
  const deleteButton = page.locator('#editor button.danger');
  await expect(deleteButton).toHaveAccessibleName('Delete');
  const shortcut = page.locator('#deck .key:not(.add) .kl').first();
  const label = await shortcut.textContent();
  await page.locator('#langSel').selectOption('pt-BR');
  await expect(deleteButton).toHaveAccessibleName('Excluir');
  await expect(shortcut).toHaveText(label);
  await page.evaluate(() => { location.hash = '#webhooks'; });
  const pushedDelete = page.locator('#whItems button').first();
  await expect(pushedDelete).toHaveAccessibleName('Excluir');
  await page.locator('#langSel').selectOption('en');
  await expect(pushedDelete).toHaveAccessibleName('Delete');
});

for (const language of ['es', 'pt-BR']) {
  test(`the calendar uses localized Monday-first weekday headings (${language})`, async ({ page }) => {
    await show(page, [{ id: 'calendar', type: 'calendar', x: 0, y: 0, w: 4, h: 4 }], { language });
    const headings = await page.evaluate(() => {
      const formatter = new Intl.DateTimeFormat(window.PhoneBoxI18n.getLocale(), { weekday: 'narrow' });
      return Array.from({ length: 7 }, (_, i) => formatter.format(new Date(2024, 0, i + 1)));
    });
    await expect(page.locator('.w-calendar .cal-h')).toHaveText(headings);
  });
}
