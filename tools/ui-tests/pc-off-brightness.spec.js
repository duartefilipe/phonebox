// The page marked "show while the PC is off" is meant to be read across a desk, so it stays on through the night
// hours instead of letting the night clock cover it, and it can carry its own screen brightness.
const { test, expect } = require('@playwright/test');

const NIGHT = /(^|\s)night($|\s)/;
const AWAY = /(^|\s)away($|\s)/;

// The night window is read from the clock in local time, so the tests below only sit inside it with a fixed zone.
test.use({ timezoneId: 'UTC' });

// Opens the panel inside the night window (00:30–07:30) with a PC-off page, then loses the agent so the page comes up.
async function open(page, display = {}) {
  await page.clock.install({ time: new Date('2026-01-01T02:00:00Z') });
  await page.addInitScript(() => {
    window.__phoneboxKey = 'k';
    window.__brightness = [];
    window.Android = {
      transport: () => 'usb',
      setBrightness: (key, value) => window.__brightness.push(value),
      probePc: () => window.onPcProbe(JSON.stringify({ host: 'down', lan: true, usb: null })),
    };
  });
  await page.goto('/panel/');
  await expect(page.locator('#dots i')).not.toHaveCount(0);
  await page.clock.pauseAt(new Date('2026-01-01T02:01:00Z'));
  await page.evaluate(async (display) => {
    const m = await import('/panel/main.js');
    m.cfg.night = { enabled: true, start: '00:30', end: '07:30', brightness: 0.02 };
    Object.assign(m.cfg.display, display);
    m.cfg.pages = [
      { id: 'home', title: 'Home', enabled: true, widgets: [{ id: 'c', type: 'clock', x: 0, y: 0, w: 6, h: 4, options: {} }] },
      { id: 'off', title: 'PC off', enabled: true, offline: true, widgets: [{ id: 'o', type: 'clock', x: 0, y: 0, w: 12, h: 6, options: {} }] },
    ];
    m.buildLayout();
    window.fetch = () => Promise.reject(new TypeError('the agent is not running'));
    m.setOnline(false);
  }, display);
  await page.clock.runFor(12_000); // the probe says the computer is down: off after 10 s
}

const body = (page) => page.locator('body');
const brightness = (page) => page.evaluate(() => window.__brightness);

test('the PC-off page stays on during the night hours instead of the night clock', async ({ page }) => {
  await open(page);
  await expect(body(page)).toHaveClass(AWAY);
  await expect(body(page)).not.toHaveClass(NIGHT);
});

test('turning off "keep it on at night" lets the night clock cover the PC-off page again', async ({ page }) => {
  await open(page, { awayAtNight: false });
  await expect(body(page)).toHaveClass(AWAY);
  await expect(body(page)).toHaveClass(NIGHT);
});

test('the PC-off page uses its own brightness, and gives the screen back when the agent answers', async ({ page }) => {
  await open(page, { awayBrightness: 0.6 });
  await expect(body(page)).toHaveClass(AWAY);
  expect(await brightness(page)).toContain(0.6);

  await page.evaluate(async () => {
    window.__brightness.length = 0;
    (await import('/panel/main.js')).setOnline(true);
  });
  await expect(body(page)).not.toHaveClass(AWAY);
  // back inside the night window with no PC-off page on screen: the night screen's own level
  expect(await brightness(page)).toContain(0.02);
});

// Without a level of its own the page is drawn at whatever was already set, as it was before the setting existed.
test('a PC-off page with no brightness of its own does not set one', async ({ page }) => {
  await open(page);
  await expect(body(page)).toHaveClass(AWAY);
  expect(await brightness(page)).not.toContain(0.6);
});
