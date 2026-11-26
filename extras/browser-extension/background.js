// PhoneBox video crop: passes the tabs' reports to the PhoneBox agent on this computer, and nowhere else.
'use strict';

const MSG = 'phonebox-video-crop';
const DEFAULT_PORT = 8765;
let port = DEFAULT_PORT;

chrome.storage.local.get({ port: DEFAULT_PORT }).then((s) => { port = validPort(s.port); }).catch(() => {});
chrome.storage.onChanged.addListener((changes) => { if (changes.port) port = validPort(changes.port.newValue); });

function validPort(p) {
  const n = Number(p);
  return Number.isInteger(n) && n >= 1024 && n <= 65535 ? n : DEFAULT_PORT;
}

chrome.runtime.onMessage.addListener((msg, sender) => {
  // only from this extension's own content scripts, in a tab
  if (!sender.tab || sender.id !== chrome.runtime.id || !msg || msg.type !== MSG) return;
  const body = msg.gone ? { gone: String(msg.gone) } : msg.report;
  if (!body) return;
  fetch(`http://127.0.0.1:${port}/api/nowplaying/rect`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
    cache: 'no-store',
  }).catch(() => { /* the agent is not running: the phone shows the whole window, or what moves in it */ });
});
