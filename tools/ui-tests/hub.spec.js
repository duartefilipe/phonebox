// The panel connected to a hub (an agent in hub mode): each widget shows one computer's data, its requests go to that
// computer through the hub, and a computer going off dims its widgets or swaps in its own PC-off page.
const { test, expect } = require('@playwright/test');

const A = 'aaaa', B = 'bbbb';
const pcState = (cpu, host) => ({ pc: { cpu, ramUsedGb: 4, ramTotalGb: 16 }, host, media: null, audio: null });

async function open(page) {
  await page.goto('/panel/');
  await expect(page.locator('#dots i')).not.toHaveCount(0);
  await page.evaluate(async ({ A, B }) => {
    const m = await import('/panel/main.js');
    // the demo's own states, polled every second, carry the computers the test set last
    const demoFetch = window.fetch;
    window.fetch = async (url, opts) => {
      const r = await demoFetch(url, opts);
      if (!String(url).includes('/state') || !window.__sources) return r;
      const j = await r.json();
      return new Response(JSON.stringify({ ...j, sources: window.__sources }), { headers: { 'content-type': 'application/json' } });
    };
    m.cfg.sources = [{ id: A, name: 'Desk' }, { id: B, name: 'Office' }];
    m.cfg.pages = [
      { id: 'a', title: 'A', enabled: true, widgets: [{ id: 'w1', type: 'stat', x: 0, y: 0, w: 3, h: 2, options: { metric: 'cpu' } }] },
      { id: 'b', title: 'B', enabled: true, source: B, widgets: [
        { id: 'w2', type: 'stat', x: 0, y: 0, w: 3, h: 2, options: { metric: 'cpu' } },
        { id: 'w3', type: 'stat', x: 3, y: 0, w: 3, h: 2, options: { metric: 'cpu', source: A } },
      ] },
      { id: 'boff', title: 'B off', enabled: true, offline: true, offlineSource: B, widgets: [] },
      { id: 'alloff', title: 'All off', enabled: true, offline: true, widgets: [] },
    ];
    m.buildLayout();
  }, { A, B });
}

async function state(page, sources) {
  await page.evaluate(async (sources) => {
    const m = await import('/panel/main.js');
    window.__sources = sources;
    m.applyState({ ...m.D.state, ts: Date.now() + Math.random(), sources });
  }, sources);
}

const box = (page, pageId, i) => page.locator(`#pager > .page[data-id="${pageId}"] .w`).nth(i);

test('each widget shows its own computer: the widget\'s, else the page\'s, else the first', async ({ page }) => {
  await open(page);
  await state(page, [{ id: A, name: 'Desk', online: true, state: pcState(11, 'desk') }, { id: B, name: 'Office', online: true, state: pcState(77, 'office') }]);
  await expect(box(page, 'a', 0)).toContainText('11');
  await expect(box(page, 'b', 0)).toContainText('77');
  await expect(box(page, 'b', 1)).toContainText('11');
});

test('a request for computer data goes to the computer in play through the hub', async ({ page }) => {
  await open(page);
  const paths = await page.evaluate(async (A) => {
    const m = await import('/panel/main.js');
    return [m.relayPath('/media'), m.relayPath('/volume?op=up'), m.relayPath('/config'), m.relayPath('/ha/states')];
  }, A);
  expect(paths).toEqual([`/src/${A}/media`, `/src/${A}/volume?op=up`, '/config', '/ha/states']);
});

test('the widgets of a computer that is off are dimmed and say so', async ({ page }) => {
  await open(page);
  await state(page, [{ id: A, name: 'Desk', online: false, state: null }, { id: B, name: 'Office', online: true, state: pcState(5, 'office') }]);
  await expect(box(page, 'a', 0)).toHaveClass(/src-off/);
  await expect(box(page, 'a', 0)).toHaveAttribute('data-off', 'PC off');
});

test('a computer with a PC-off page of its own swaps it in for its pages while it is off', async ({ page }) => {
  await open(page);
  const ids = () => page.locator('#pager > .page').evaluateAll((els) => els.map((e) => e.dataset.id));
  await state(page, [{ id: A, name: 'Desk', online: true, state: pcState(1, 'desk') }, { id: B, name: 'Office', online: true, state: pcState(2, 'office') }]);
  await expect.poll(ids).toEqual(['a', 'b']);
  await state(page, [{ id: A, name: 'Desk', online: true, state: pcState(1, 'desk') }, { id: B, name: 'Office', online: false, state: null }]);
  await expect.poll(ids).toEqual(['a', 'boff']);
  await state(page, [{ id: A, name: 'Desk', online: true, state: pcState(1, 'desk') }, { id: B, name: 'Office', online: true, state: pcState(2, 'office') }]);
  await expect.poll(ids).toEqual(['a', 'b']);
});

test('with every computer off, the PC-off page covers the panel', async ({ page }) => {
  await open(page);
  await state(page, [{ id: A, name: 'Desk', online: false, state: null }, { id: B, name: 'Office', online: false, state: null }]);
  await expect(page.locator('body')).toHaveClass(/(^|\s)away($|\s)/);
  await state(page, [{ id: A, name: 'Desk', online: true, state: pcState(1, 'desk') }, { id: B, name: 'Office', online: false, state: null }]);
  await expect(page.locator('body')).not.toHaveClass(/(^|\s)away($|\s)/);
});

test('without a hub nothing changes: no relay, no computer state', async ({ page }) => {
  await page.goto('/panel/');
  await expect(page.locator('#dots i')).not.toHaveCount(0);
  const r = await page.evaluate(async () => {
    const m = await import('/panel/main.js');
    return [m.inHub(), m.relayPath('/media')];
  });
  expect(r).toEqual([false, '/media']);
});
