'use strict';
const input = document.getElementById('port');
const saved = document.getElementById('saved');
chrome.storage.local.get({ port: 8765 }).then((s) => { input.value = s.port; });
input.addEventListener('change', () => {
  const n = Number(input.value);
  if (!Number.isInteger(n) || n < 1024 || n > 65535) { input.value = 8765; return; }
  chrome.storage.local.set({ port: n }).then(() => { saved.hidden = false; setTimeout(() => { saved.hidden = true; }, 1500); });
});
