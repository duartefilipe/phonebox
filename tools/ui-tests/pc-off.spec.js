// When a silent agent means the computer is off: the agent's last word ("shutdown", "sleep", "stop") and what the app
// finds out itself (window.onPcProbe) decide before the unreachable delay does. The night screen stands in for the
// "PC off" page here: both follow the same decision.
const { test, expect } = require('@playwright/test');

const NIGHT = /(^|\s)night($|\s)/;

async function open(page, { route = 'wifi', probe = null } = {}) {
  await page.clock.install({ time: new Date('2026-01-01T12:00:00Z') });
  await page.addInitScript(({ route, probe }) => {
    window.__phoneboxKey = 'k';
    window.__probes = 0;
    window.__probeAnswer = probe;
    window.Android = {
      transport: () => window.__route ?? route,
      probePc: () => {
        window.__probes++;
        const answer = window.__probeAnswer;
        if (answer) setTimeout(() => window.onPcProbe(JSON.stringify(answer)), 50);
      },
    };
  }, { route, probe });
  await page.goto('/panel/');
  await expect(page.locator('#dots i')).not.toHaveCount(0);
  await page.clock.pauseAt(new Date('2026-01-01T12:01:00Z'));
  await page.evaluate(async () => {
    const m = await import('/panel/main.js');
    m.cfg.display.nightWhenUnreachable = true;
    m.cfg.display.unreachableNightDelayMin = 5;
  });
}

// the agent goes away: from now on every request fails
async function lose(page, before = null) {
  await page.evaluate(async (before) => {
    const m = await import('/panel/main.js');
    if (before) window.onAgentStream(before.event, JSON.stringify(before.data));
    window.fetch = () => Promise.reject(new TypeError('the agent is not running'));
    m.setOnline(false);
  }, before);
}

const body = (page) => page.locator('body');

test('a shutdown the agent announced shows the PC-off screen at once', async ({ page }) => {
  await open(page);
  await lose(page, { event: 'bye', data: { reason: 'shutdown' } });
  await page.clock.runFor(1_100);
  await expect(body(page)).toHaveClass(NIGHT);
});

test('a sleep announced in the state shows it at once too', async ({ page }) => {
  await open(page);
  await page.evaluate(async () => {
    const m = await import('/panel/main.js');
    m.applyState({ ...m.D.state, ts: Date.now(), power: 'sleep' });
  });
  await lose(page);
  await page.clock.runFor(1_100);
  await expect(body(page)).toHaveClass(NIGHT);
});

test('a later state without a reason takes back a sleep the computer woke from', async ({ page }) => {
  await open(page);
  await page.evaluate(async () => {
    const m = await import('/panel/main.js');
    m.applyState({ ...m.D.state, ts: Date.now(), power: 'sleep' });
    m.applyState({ ...m.D.state, ts: Date.now() + 1, power: null });
  });
  await lose(page);
  await page.clock.runFor(60_000);
  await expect(body(page)).not.toHaveClass(NIGHT);
});

test('a quit agent leaves the computer on: no PC-off screen after the delay', async ({ page }) => {
  await open(page);
  await lose(page, { event: 'bye', data: { reason: 'stop' } });
  await page.clock.runFor(6 * 60_000);
  await expect(body(page)).not.toHaveClass(NIGHT);
});

test('a computer that no longer answers on its network is off after 10 seconds', async ({ page }) => {
  await open(page, { probe: { host: 'down', lan: true, usb: null } });
  await lose(page);
  await page.clock.runFor(8_000);
  await expect(body(page)).not.toHaveClass(NIGHT);
  await page.clock.runFor(3_000);
  await expect(body(page)).toHaveClass(NIGHT);
});

test('a computer that still answers is on, whatever the delay says', async ({ page }) => {
  await open(page, { probe: { host: 'up', lan: true, usb: null } });
  await lose(page);
  await page.clock.runFor(6 * 60_000);
  await expect(body(page)).not.toHaveClass(NIGHT);
});

test('the USB computer letting go of the phone means off, after 10 seconds', async ({ page }) => {
  await open(page, { route: 'usb', probe: { host: 'unknown', lan: null, usb: false } });
  await lose(page);
  await page.clock.runFor(11_000);
  await expect(body(page)).toHaveClass(NIGHT);
});

test('a phone that left the computer\'s network does not call the computer off', async ({ page }) => {
  await open(page, { probe: { host: 'unknown', lan: false, usb: null } });
  await lose(page);
  await page.clock.runFor(6 * 60_000);
  await expect(body(page)).not.toHaveClass(NIGHT);
});

test('with nothing to go on, the unreachable delay still decides', async ({ page }) => {
  await open(page, { probe: { host: 'unknown', lan: true, usb: null } });
  await lose(page);
  await page.clock.runFor(4 * 60_000);
  await expect(body(page)).not.toHaveClass(NIGHT);
  await page.clock.runFor(61_000);
  await expect(body(page)).toHaveClass(NIGHT);
});

test('the app checks on the computer at once and then every 15 seconds', async ({ page }) => {
  await open(page);
  await lose(page);
  expect(await page.evaluate(() => window.__probes)).toBe(1);
  await page.clock.runFor(14_000);
  expect(await page.evaluate(() => window.__probes)).toBe(1);
  await page.clock.runFor(2_000);
  expect(await page.evaluate(() => window.__probes)).toBe(2);
});

test('an announced shutdown is forgotten once the agent answers again', async ({ page }) => {
  await open(page);
  await lose(page, { event: 'bye', data: { reason: 'shutdown' } });
  await page.clock.runFor(1_100);
  await expect(body(page)).toHaveClass(NIGHT);
  await page.evaluate(async () => (await import('/panel/main.js')).setOnline(true));
  await expect(body(page)).not.toHaveClass(NIGHT);
  await lose(page);
  await page.clock.runFor(60_000);
  await expect(body(page)).not.toHaveClass(NIGHT);
});
