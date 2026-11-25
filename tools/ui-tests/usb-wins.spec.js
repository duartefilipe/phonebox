// Settings › Display › "A connected USB cable outranks the computer chosen in the panel" (display.usbWins), and the
// per-phone answer under Phones. The panel hands the setting to the app, which decides which route to try first.
const { test, expect } = require('@playwright/test');

// Records what the panel tells the native bridge, so the handover can be checked without a phone.
async function panel(page, display = {}) {
  await page.goto('/panel/style.css');
  await page.addInitScript(() => {
    window.__phoneboxKey = 'k';
    window.__usbWins = [];
    window.Android = {
      battery: () => '{}', version: () => 'test', haptic() {}, buzz() {}, setBrightness() {}, setVolumeKeys() {},
      is24Hour: () => true, transport: () => 'usb', pcs: () => '[]',
      setUsbWins: (key, value) => { if (key === 'k') window.__usbWins.push(value); },
    };
  });
  await page.evaluate((display) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      display: { idleReturnSec: 0, ...display },
      pages: [{ id: 'a', title: 'A', widgets: [{ id: 'c', type: 'clock', x: 0, y: 0, w: 6, h: 4, options: {} }] }],
    }));
  }, display);
  await page.goto('/panel/');
  await expect(page.locator('#pager .page')).toHaveCount(1);
}

const handed = (page) => page.evaluate(() => window.__usbWins);

test('the panel hands the cable setting to the app, on by default', async ({ page }) => {
  await panel(page);
  expect(await handed(page)).toContain(true);
});

// The panel applies its cached settings before the agent answers, so the app may hear the old value first; what
// counts is where it ends up, and that the default stays on until something says otherwise.
test('turning it off is handed over as off', async ({ page }) => {
  await panel(page, { usbWins: false });
  await expect.poll(async () => (await handed(page)).at(-1)).toBe(false);
});

// Settings › General › Phones: the choice per device, which the agent applies to that phone's own settings.
test('Phones offers the per-device choice, following the general setting until it is answered', async ({ page }) => {
  await page.goto('/settings/#general');
  const row = page.locator('#phoneList .phone-row').first();
  await expect(row).toBeVisible();
  const select = row.locator('select').filter({ hasNot: page.locator('[data-phone-home]') }).last();
  await expect(select).toHaveValue('');
  await expect(select.locator('option')).toHaveText([
    'Same as all devices', 'Yes — show the computer on the cable', 'No — keep the computer chosen in the panel',
  ]);

  await select.selectOption('no');
  await expect(page.locator('#save')).toBeEnabled();
  await page.locator('#save').click();
  await expect(page.locator('#save')).toBeDisabled();
  const saved = await page.evaluate(async () => (await (await fetch('/api/settings')).json()).phones?.[0]?.usbWins);
  expect(saved).toBe(false);
});
