// PhoneBox video crop: where is the video that is playing in this tab?
//
// Runs in every frame. Each frame finds its best video (playing first, then the largest on screen) and, when it is
// not the top frame, hands it to its parent, which maps it into its own coordinates through the <iframe> it sits in.
// The top frame sends the winner to the extension's background, which tells the PhoneBox agent on this computer
// (http://127.0.0.1). Nothing leaves the computer: the page's title (to match the browser window) and a rectangle.
(() => {
  'use strict';
  const TOP = window.top === window;
  const TICK_MS = 500;      // how often the page is looked at
  const REPEAT_MS = 1000;   // an unchanged report is sent again this often (the agent forgets one after 3 s)
  const CHILD_TTL_MS = 2000;
  const MSG = 'phonebox-video-crop';

  /** @type {Map<Window, {r: {x:number,y:number,w:number,h:number}, playing: boolean, at: number}>} */
  const children = new Map();
  let lastJson = '', lastSent = 0, lastTitle = '', scheduled = false;

  // The picture inside a <video>: with object-fit contain (the default for video) the element is letterboxed, and
  // the bars are page, not video.
  function picture(v, r) {
    const vw = v.videoWidth, vh = v.videoHeight;
    const fit = getComputedStyle(v).objectFit;
    if (!vw || !vh || fit === 'fill' || fit === 'cover') return { x: r.left, y: r.top, w: r.width, h: r.height };
    const s = Math.min(r.width / vw, r.height / vh, fit === 'scale-down' ? 1 : Infinity);
    const w = vw * s, h = vh * s;
    return { x: r.left + (r.width - w) / 2, y: r.top + (r.height - h) / 2, w, h };
  }

  function onScreen(r) {
    const w = Math.min(r.x + r.w, innerWidth) - Math.max(r.x, 0);
    const h = Math.min(r.y + r.h, innerHeight) - Math.max(r.y, 0);
    return w > 0 && h > 0 ? w * h : 0;
  }

  /** This frame's best candidate: its own videos and what its child frames reported, in this frame's coordinates. */
  function best() {
    let top = null, topScore = 0;
    const consider = (r, playing) => {
      const area = onScreen(r);
      if (area < 120 * 68) return; // a thumbnail or a muted preview is not the film
      const score = area * (playing ? 8 : 1);
      if (score > topScore) { topScore = score; top = { r, playing }; }
    };
    for (const v of document.querySelectorAll('video')) {
      const r = v.getBoundingClientRect();
      if (r.width < 2 || r.height < 2) continue;
      const playing = !v.paused && !v.ended && v.readyState >= 2;
      consider(picture(v, r), playing);
    }
    const now = Date.now();
    for (const [win, c] of children) {
      if (now - c.at > CHILD_TTL_MS) { children.delete(win); continue; }
      const frame = [...document.querySelectorAll('iframe, frame')].find((f) => f.contentWindow === win);
      if (!frame) continue;
      const fr = frame.getBoundingClientRect();
      const cs = getComputedStyle(frame);
      // the child's viewport starts inside the frame's border and padding
      const ox = fr.left + parseFloat(cs.borderLeftWidth) + parseFloat(cs.paddingLeft);
      const oy = fr.top + parseFloat(cs.borderTopWidth) + parseFloat(cs.paddingTop);
      consider({ x: c.r.x + ox, y: c.r.y + oy, w: c.r.w, h: c.r.h }, c.playing);
    }
    return top;
  }

  function send(msg) {
    try { chrome.runtime.sendMessage(msg).catch(() => {}); } catch (_) { /* the extension was reloaded */ }
  }

  function tick() {
    scheduled = false;
    const b = best();
    if (!TOP) {
      if (b) window.parent.postMessage({ [MSG]: { r: b.r, playing: b.playing } }, '*');
      return;
    }
    const title = document.title.trim();
    if (!b || !title) {
      if (lastTitle) { send({ type: MSG, gone: lastTitle }); lastTitle = ''; lastJson = ''; }
      return;
    }
    if (lastTitle && lastTitle !== title) send({ type: MSG, gone: lastTitle });
    const report = {
      title,
      dpr: devicePixelRatio,
      vw: innerWidth,
      vh: innerHeight,
      x: b.r.x, y: b.r.y, w: b.r.w, h: b.r.h,
      visible: document.visibilityState === 'visible',
      playing: b.playing,
      fullscreen: !!document.fullscreenElement,
    };
    // Firefox says where the page is on screen; Chromium has a window of its own for it, which the agent finds
    if (typeof window.mozInnerScreenX === 'number') { report.sx = window.mozInnerScreenX; report.sy = window.mozInnerScreenY; }
    const json = JSON.stringify(report);
    const now = Date.now();
    if (json === lastJson && now - lastSent < REPEAT_MS) return;
    lastJson = json;
    lastSent = now;
    lastTitle = title;
    send({ type: MSG, report });
  }

  // a change is sent at the next frame, not at the next tick: a scroll or a resize must not leave a stale crop
  function soon() {
    if (scheduled) return;
    scheduled = true;
    requestAnimationFrame(tick);
  }

  window.addEventListener('message', (e) => {
    const d = e.data && e.data[MSG];
    if (!d || !e.source || typeof d !== 'object' || !d.r) return;
    const r = d.r;
    if (![r.x, r.y, r.w, r.h].every(Number.isFinite)) return;
    children.set(e.source, { r, playing: !!d.playing, at: Date.now() });
    soon();
  });
  for (const ev of ['resize', 'scroll', 'fullscreenchange', 'visibilitychange']) window.addEventListener(ev, soon, { passive: true, capture: true });
  for (const ev of ['play', 'pause', 'playing', 'loadedmetadata', 'resize']) document.addEventListener(ev, soon, { capture: true });
  window.addEventListener('pagehide', () => { if (TOP && lastTitle) send({ type: MSG, gone: lastTitle }); });
  setInterval(tick, TICK_MS);
  tick();
})();
