import { API, D, PANEL_HEADERS, api, averageColor, curPage, emit, fmtTime, h, haptic, media, pages, relayPath, setArtColor, setMediaState, setVolDragging, t, tp, volDragging } from '../main.js';
import { W } from '../registry.js';

// ---------------------------------------------------------------- Now playing screen
// A whole page (or a large card) for what is playing on the computer: the cover filling the background, blurred and
// darkened, the service's own logo and colour (Netflix, YouTube, HBO Max, Disney+, Prime Video…, recognized by the
// agent in MediaService.cs), the title, and the controls. Made for a page of its own, so a film or an episode can be
// seen across the room; it reflows to a smaller card without the extras.

/** Each service's own mark and colour; the agent sends the id (MediaSnapshot.Service). */
export const SERVICES = {
  netflix: { color: '#e50914', mark: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 1h4.2l5.1 14.4V1H19v22h-4.2L9.7 8.6V23H5z" fill="currentColor"/></svg>' },
  youtube: { color: '#ff0033', mark: '<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="1" y="4" width="22" height="16" rx="5" fill="currentColor"/><path d="m10 8 6 4-6 4z" fill="#fff"/></svg>' },
  'youtube-music': { color: '#ff0033', mark: '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="11" fill="currentColor"/><circle cx="12" cy="12" r="6.5" fill="none" stroke="#fff" stroke-width="1.4"/><path d="m10 8.5 5.5 3.5-5.5 3.5z" fill="#fff"/></svg>' },
  'hbo-max': { color: '#991eeb', mark: '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="7" cy="12" r="5.4" fill="currentColor"/><circle cx="16" cy="12" r="5.4" fill="currentColor" opacity=".55"/><circle cx="21" cy="12" r="2.6" fill="currentColor" opacity=".35"/></svg>' },
  'disney-plus': { color: '#1f80e0', mark: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M2 8c3.5-2 11-2.5 15 .5 3 2.3 2.2 6-1.5 6.8-2.8.6-5-.4-5-.4V19l-3 1V7.5c-2 .2-4 .8-5.5 1.6zm8.5 1.2v3.6s1.6.6 3 .2c1.4-.4 1.6-2-.2-2.9-1-.5-2.1-.8-2.8-.9z" fill="currentColor"/></svg>' },
  'prime-video': { color: '#00a8e1', mark: '<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="1" y="4" width="22" height="16" rx="3" fill="currentColor"/><path d="m10 8.5 6 3.5-6 3.5z" fill="#fff"/></svg>' },
  'apple-tv': { color: '#d1d5db', mark: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M16.2 12.5c0-2 1.6-3 1.7-3.1-1-1.4-2.4-1.6-2.9-1.6-1.3-.1-2.4.7-3 .7s-1.6-.7-2.6-.7c-1.4 0-2.6.8-3.3 2-1.4 2.5-.4 6.1 1 8 .7 1 1.5 2 2.5 2 1 0 1.4-.6 2.6-.6s1.5.6 2.6.6 1.8-1 2.5-2c.5-.7.7-1.1 1-1.9-2.5-1-2.6-3.3-2.6-3.4zM14 5.9c.5-.7.9-1.6.8-2.6-.8 0-1.8.5-2.4 1.2-.5.6-1 1.6-.8 2.5.9.1 1.8-.4 2.4-1.1z" fill="currentColor"/></svg>' },
  'paramount-plus': { color: '#0064ff', mark: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m12 2 3 7.5L22 11l-5.5 4.2L18 23l-6-4.2L6 23l1.5-7.8L2 11l7-1.5z" fill="currentColor"/></svg>' },
  globoplay: { color: '#f5463b', mark: '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="10" fill="none" stroke="currentColor" stroke-width="3"/><ellipse cx="12" cy="12" rx="4.5" ry="10" fill="none" stroke="currentColor" stroke-width="2"/></svg>' },
  crunchyroll: { color: '#f47521', mark: '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="10" fill="none" stroke="currentColor" stroke-width="3"/><circle cx="16" cy="8" r="2.6" fill="currentColor"/></svg>' },
  twitch: { color: '#9146ff', mark: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 3h16v11l-4 4h-3l-3 3H8v-3H4zm3 2v9h3v3l3-3h3l2-2V5z" fill="currentColor"/></svg>' },
  spotify: { color: '#1db954', mark: '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="10" fill="currentColor"/><path d="M7 9.5c3.5-1 7.5-.6 10 1M7.5 13c3-.8 6-.4 8 .9M8 16.2c2.4-.6 4.6-.3 6 .6" fill="none" stroke="#000" stroke-width="1.6" stroke-linecap="round"/></svg>' },
  soundcloud: { color: '#ff5500', mark: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M2 14v3h1v-3zm2-2v5h1v-5zm2-2v7h1v-7zm2 1v6h1v-6zm3-4c-.6 0-1 .1-1.5.4V17H19a3 3 0 0 0 0-6c-.3 0-.6 0-.9.1A4.6 4.6 0 0 0 13 7z" fill="currentColor"/></svg>' },
  deezer: { color: '#a238ff', mark: '<svg viewBox="0 0 24 24" aria-hidden="true"><g fill="currentColor"><rect x="14" y="4" width="8" height="2.6"/><rect x="14" y="8" width="8" height="2.6"/><rect x="2" y="12" width="8" height="2.6"/><rect x="14" y="12" width="8" height="2.6"/><rect x="2" y="16" width="8" height="2.6"/><rect x="14" y="16" width="8" height="2.6"/></g></svg>' },
  tidal: { color: '#00ffff', mark: '<svg viewBox="0 0 24 24" aria-hidden="true"><g fill="currentColor"><path d="m6 6 3 3-3 3-3-3zm12 0 3 3-3 3-3-3zm-6 0 3 3-3 3-3-3zm0 6 3 3-3 3-3-3z"/></g></svg>' },
  vimeo: { color: '#1ab7ea', mark: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M22 7.6c-.1 2.1-1.6 5-4.4 8.6-2.9 3.8-5.4 5.7-7.4 5.7-1.3 0-2.3-1.1-3.2-3.4L5.4 13c-.6-2.3-1.3-3.4-2-3.4-.2 0-.7.3-1.4.8L1 9.3c2.5-2.2 4.9-4.6 6.4-4.7 1.6-.2 2.6.9 3 3.3.4 2.6.7 4.2.9 4.8.5 2.2 1 3.2 1.6 3.2.5 0 1.2-.7 2.1-2.1.9-1.4 1.4-2.5 1.5-3.2.1-1.3-.4-2-1.5-2-.5 0-1.1.1-1.7.4C14.3 5.3 16.6 3.6 19 3.7c1.9.1 2.8 1.4 2.7 4z" fill="currentColor"/></svg>' },
  plex: { color: '#e5a00d', mark: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 2h6l7 10-7 10H5l7-10z" fill="currentColor"/></svg>' },
  'internet-radio': { color: '#38bdf8', mark: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6.2 6.8 18.5 2a1 1 0 0 1 .7 1.9L10.8 7H20a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V9a2 2 0 0 1 2-2h2.2zm1.8 5.2a3 3 0 1 0 0 6 3 3 0 0 0 0-6zm6 1.5v1.8h4v-1.8zm0 3.2v1.8h4v-1.8z" fill="currentColor"/></svg>' },
};

/** The reading the screen shows, with the agent's service and show title filled in where the player had none. */
export function describe(m) {
  if (!m) return null;
  // the agent's own Internet Radio has no "service" of its own, but it has a logo here
  const id = m.service || (m.app === 'Internet Radio' ? 'internet-radio' : null);
  const known = id ? SERVICES[id] : null;
  return {
    service: known ? id : null,
    color: known ? known.color : null,
    mark: known ? known.mark : '',
    // the browser's window knows the episode when the player said no more than "Netflix" (MediaService.Describe)
    title: m.showTitle || m.title || '',
    // what is under the title: the artist and album for music, the service's name for a film or an episode
    sub: [m.artist, m.album].filter(Boolean).join(' · ') || m.serviceName || m.app || '',
    service_name: m.serviceName || m.app || '',
    live: m.app === 'Internet Radio' && !(m.durMs > 0),
  };
}

W.nowplaying = {
  // 'media': the agent reads the players every second while this is on screen. 'tick': keeps the live-video loop
  // going on a page that is up but still (nothing playing means no state changes to ride on).
  needs: ['state', 'fast', 'media', 'tick'],
  create(box, { o, page, w }) {
    const showArt = o.art !== false;
    const showControls = o.controls !== false;
    const showVolume = o.volume !== false && showControls;
    const showProgress = o.progress !== false;
    // Live video: the agent sends pictures of the playing player's own window (Windows). The widget names itself, and
    // the agent checks that widget's own option, as the Camera widget does.
    const liveOn = o.live !== false;
    const liveFps = Math.min(15, Math.max(1, Number(o.fps) || 5));
    const wq = 'widget=' + encodeURIComponent(`${page.id}/${w.id}`);

    const backdrop = h('div', 'np-backdrop');
    const backImg = h('img', 'np-back-img');
    backImg.alt = '';
    backdrop.append(backImg);
    const cover = h('img', 'np-cover');
    cover.alt = '';
    const coverPh = h('div', 'np-cover-ph', '♪');
    const coverWrap = h('div', 'np-cover-wrap', cover, coverPh);
    const badgeMark = h('span', 'np-badge-mark');
    const badgeName = h('span', 'np-badge-name');
    const badge = h('div', 'np-badge', badgeMark, badgeName);
    const title = h('div', 'np-title');
    const sub = h('div', 'np-sub');
    const fill = h('div', 'np-fill');
    const bar = h('div', 'np-bar', fill);
    const posText = h('span', 'np-pos', '0:00');
    const durText = h('span', 'np-dur', '0:00');
    const progress = h('div', 'np-progress', bar, h('div', 'np-times', posText, durText));
    const info = h('div', 'np-info', badge, title, sub, ...(showProgress ? [progress] : []));

    const svg = (d) => { const e = h('span'); e.innerHTML = `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="${d}"/></svg>`; return e.firstChild; };
    const btn = (action, label, path) => {
      const b = h('button', `np-ic np-${action}`);
      b.type = 'button';
      b.setAttribute('aria-label', label);
      b.append(svg(path));
      b.addEventListener('click', () => act(action));
      return b;
    };
    const prev = btn('prev', tp('Previous'), 'M6 5h2v14H6zM20 5v14L9.5 12z');
    const play = h('button', 'np-ic np-play');
    play.type = 'button';
    play.setAttribute('aria-label', tp('Play/Pause'));
    play.append(svg('M8 5v14l11-7z'), svg('M7 5h4v14H7zM13 5h4v14h-4z'));
    play.firstChild.classList.add('np-i-play');
    play.lastChild.classList.add('np-i-pause');
    play.addEventListener('click', () => act('playpause'));
    const next = btn('next', tp('Next'), 'M16 5h2v14h-2zM4 5v14l10.5-7z');
    const vol = h('input', 'np-vol');
    vol.type = 'range'; vol.min = '0'; vol.max = '100'; vol.step = '1'; vol.value = '50';
    vol.setAttribute('aria-label', tp('Volume'));
    const volVal = h('span', 'np-vol-val', '--');
    const controls = h('div', 'np-controls', prev, play, next, ...(showVolume ? [h('div', 'np-vol-wrap', vol, volVal)] : []));

    // The live picture sits over the backdrop and under the words, so the title stays readable on it. The video is
    // drawn on a canvas (H.264 decoded by WebCodecs); the JPEG pictures, for a panel without it, use two layers as in
    // the Camera widget: the next picture is decoded in the hidden one and only then swapped in, so it never blanks.
    const liveLayers = [h('img', 'np-live'), h('img', 'np-live')];
    for (const img of liveLayers) img.alt = '';
    const videoCanvas = h('canvas', 'np-live np-video');
    const liveLayer = h('div', 'np-live-wrap', videoCanvas, ...liveLayers);

    box.classList.add('np');
    box.append(backdrop, ...(liveOn ? [liveLayer] : []),
      h('div', 'np-body', ...(showArt ? [coverWrap] : []), info), ...(showControls ? [controls] : []));

    let artId = -1, volTimer = 0, volumeRelease = 0;
    cover.addEventListener('load', () => { cover.classList.add('on'); backImg.src = cover.src; setArtColor(averageColor(cover)); });
    cover.addEventListener('error', () => { cover.classList.remove('on'); backImg.removeAttribute('src'); });

    async function act(action) {
      haptic();
      if (action === 'playpause' && media) {
        media.playing = !media.playing; // optimistic, like the Now playing widget: the button must not wait for the PC
        media.at = performance.now();
        box.classList.toggle('playing', media.playing);
      }
      try {
        const res = await api('/media/' + action, 'POST');
        if (res && typeof res === 'object' && 'media' in res) setMediaState(res.media);
        emit('tick');
      } catch (_) {}
    }

    // ---------------------------------------------------------------- live video
    // The agent captures the window of the player that is playing and sends JPEGs, only while this widget keeps asking
    // (PlayerCapture.cs). Asking stops as soon as the widget is off screen, so nothing is captured then.
    let liveAlive = true, liveRunning = false, liveInflight = null, liveFront = 0, liveOk = false;
    const liveUrls = [null, null];
    const liveVisible = () => liveOn && liveAlive && box.isConnected && !document.hidden
      && !document.body.classList.contains('night') && pages[curPage] === page && !box.closest('.stack-item:not(.active)');
    const liveClear = () => {
      liveOk = false;
      box.classList.remove('has-live');
      videoStop();
      for (let i = 0; i < 2; i++) {
        liveLayers[i].classList.remove('on');
        liveLayers[i].removeAttribute('src');
        if (liveUrls[i]) { URL.revokeObjectURL(liveUrls[i]); liveUrls[i] = null; }
      }
    };
    const decoded = (img) => (img.decode ? img.decode() : new Promise((done, fail) => { img.onload = done; img.onerror = fail; }));
    // only for the rare retry after a picture failed to decode: the agent paces the frames, the panel does not
    const liveEvery = () => 1000 / liveFps;

    const liveOne = async () => {
      const ctl = new AbortController();
      liveInflight = ctl;
      let late;
      const deadline = new Promise((_, fail) => { late = setTimeout(() => { ctl.abort(); fail(new Error('timeout')); }, 8000); });
      deadline.catch(() => {});
      try {
        const fresh = liveUrls[liveFront] ? '' : '&fresh=true';
        const r = await Promise.race([
          fetch(`${API}${relayPath(`/nowplaying/frame?${wq}${fresh}`)}`, { cache: 'no-store', headers: PANEL_HEADERS, signal: ctl.signal }),
          deadline]);
        // The agent holds the request until it grabs a frame, so a 204 means its wait ran out (nothing is being
        // grabbed) rather than "ask again sooner". Asking straight back keeps the next frame arriving as it is made:
        // guessing the interval here is what made the frames land 33 to 267 ms apart.
        if (r.status === 204) return 0;
        if (r.ok) {
          const url = URL.createObjectURL(await Promise.race([r.blob(), deadline]));
          const back = 1 - liveFront, next = liveLayers[back];
          next.src = url;
          try { await Promise.race([decoded(next), deadline]); }
          catch (_) { URL.revokeObjectURL(url); return liveEvery(); }
          if (!liveVisible()) { next.removeAttribute('src'); URL.revokeObjectURL(url); return 0; }
          if (liveUrls[back]) URL.revokeObjectURL(liveUrls[back]);
          liveUrls[back] = url;
          next.classList.add('on');
          liveLayers[liveFront].classList.remove('on');
          liveFront = back;
          liveOk = true;
          box.classList.add('has-live');
          // straight back for the next one: the agent paces the frames, and waiting here only adds jitter
          return 0;
        }
        // nothing playing, no window, the option off, or a computer that cannot capture: back to the cover art.
        // The body is read even though it is not used: an unread one leaves the request hanging, which the browser
        // then reports as an aborted request on every pass.
        await r.text().catch(() => {});
        liveClear();
        // 501 (not Windows) and 403 (the option is off) will not change by asking again soon
        return r.status === 501 || r.status === 403 ? 30000 : 2000;
      } catch (_) { liveClear(); return 3000; }
      finally { clearTimeout(late); liveInflight = null; }
    };

    // ---------------------------------------------------------------- H.264 video
    // The agent sends one long response of framed messages (VideoWire.cs): a configuration, then H.264 access units as
    // they are encoded. WebCodecs decodes them on the phone's own video hardware, and each frame is shown at the time
    // it was captured plus a small buffer that grows when the network is uneven and shrinks when it is calm, so the
    // motion is even rather than as jerky as the Wi-Fi. A panel without WebCodecs, or a stream that does not get
    // through, uses the JPEG pictures above instead.
    const videoWanted = o.liveCodec !== 'jpeg';
    let videoBroken = !videoWanted || typeof window.VideoDecoder !== 'function' || typeof ReadableStream === 'undefined';
    let videoStalls = 0, decoder = null, frames = [], offset = null, raf = 0, slackMin = Infinity, slackAt = 0;
    const gl = videoCanvas.getContext('2d', { alpha: false, desynchronized: true });
    const BUFFER_MS = 60, MAX_BUFFER_MS = 400;

    function videoStop() {
      if (raf) { cancelAnimationFrame(raf); raf = 0; }
      for (const f of frames) { try { f.close(); } catch (_) {} }
      frames = [];
      offset = null;
      if (decoder) { try { decoder.close(); } catch (_) {} decoder = null; }
      videoCanvas.classList.remove('on');
    }

    // Shows the newest frame that is due, and drops the ones it overtook. Timestamps are the capture's, in µs.
    function present() {
      raf = 0;
      if (!frames.length) return;
      const now = performance.now();
      if (offset === null) offset = now - frames[0].timestamp / 1000 + BUFFER_MS;
      let show = null;
      while (frames.length && frames[0].timestamp / 1000 + offset <= now) {
        if (show) show.close();
        show = frames.shift();
      }
      if (frames.length > 12) {
        // far behind (the phone was busy): jump to the newest instead of playing the backlog slowly
        if (show) show.close();
        while (frames.length > 1) frames.shift().close();
        show = frames.shift();
        offset = now - show.timestamp / 1000 + BUFFER_MS;
      }
      if (show) {
        if (videoCanvas.width !== show.displayWidth || videoCanvas.height !== show.displayHeight) {
          videoCanvas.width = show.displayWidth;
          videoCanvas.height = show.displayHeight;
        }
        gl.drawImage(show, 0, 0, videoCanvas.width, videoCanvas.height);
        show.close();
        if (!liveOk) {
          liveOk = true;
          videoCanvas.classList.add('on');
          for (const img of liveLayers) img.classList.remove('on');
          box.classList.add('has-live');
        }
      }
      if (frames.length) raf = requestAnimationFrame(present);
    }

    function onFrame(frame) {
      if (!liveVisible()) { frame.close(); return; }
      const now = performance.now();
      if (offset !== null) {
        // the buffer adapts: a frame that came late widens it at once; a second of comfortable margin narrows it
        const slack = frame.timestamp / 1000 + offset - now;
        if (slack < 0) offset = Math.min(offset - slack + 10, now - frame.timestamp / 1000 + MAX_BUFFER_MS);
        slackMin = Math.min(slackMin, slack);
        if (now - slackAt > 1000) {
          if (slackMin > BUFFER_MS && slackMin !== Infinity) offset -= Math.min(20, slackMin - BUFFER_MS);
          slackMin = Infinity;
          slackAt = now;
        }
      }
      frames.push(frame);
      if (!raf) raf = requestAnimationFrame(present);
    }

    async function openDecoder(cfg) {
      const base = { codec: cfg.codec, codedWidth: cfg.width, codedHeight: cfg.height, optimizeForLatency: true };
      for (const extra of [{ hardwareAcceleration: 'prefer-hardware' }, {}]) {
        const config = { ...base, ...extra };
        try {
          const ok = await VideoDecoder.isConfigSupported(config);
          if (!ok.supported) continue;
          const d = new VideoDecoder({ output: onFrame, error: () => { if (decoder === d) decoder = null; } });
          d.configure(config);
          return d;
        } catch (_) {}
      }
      return null;
    }

    // One stream, until it ends; the answer is how long to wait before the next try (as liveOne's).
    const videoOne = async () => {
      const ctl = new AbortController();
      liveInflight = ctl;
      let reader = null, gotBytes = false, waitKey = true;
      try {
        const r = await fetch(`${API}${relayPath(`/nowplaying/video?${wq}`)}`, { cache: 'no-store', headers: PANEL_HEADERS, signal: ctl.signal });
        if (!r.ok || !r.body) {
          const state = await r.json().then((j) => j && j.state).catch(() => null);
          liveClear();
          // the widget keeps to JPEG pictures, or the computer cannot encode: the pictures it is
          if (r.status === 409 || state === 'jpeg-only' || r.status === 404) { videoBroken = true; return 0; }
          return r.status === 501 || r.status === 403 ? 30000 : 2000;
        }
        reader = r.body.getReader();
        let buf = new Uint8Array(0);
        while (liveVisible()) {
          // the agent writes something at least every second; a silent stream is a stuck one (or a transport that holds
          // the response back, in which case the pictures are the way)
          let timer;
          const read = await Promise.race([reader.read(), new Promise((done) => { timer = setTimeout(() => done(null), 4000); })]);
          clearTimeout(timer);
          if (read === null) {
            if (!gotBytes && ++videoStalls >= 2) videoBroken = true;
            break;
          }
          if (read.done) break;
          gotBytes = true;
          videoStalls = 0;
          const joined = new Uint8Array(buf.length + read.value.length);
          joined.set(buf);
          joined.set(read.value, buf.length);
          buf = joined;
          let at = 0;
          while (buf.length - at >= 16) {
            const view = new DataView(buf.buffer, buf.byteOffset + at, 16);
            const type = view.getUint8(0), key = (view.getUint8(1) & 1) === 1, len = view.getUint32(4, true);
            if (buf.length - at < 16 + len) break;
            const ts = Number(view.getBigInt64(8, true));
            const payload = buf.subarray(at + 16, at + 16 + len);
            at += 16 + len;
            if (type === 1) {
              const cfg = JSON.parse(new TextDecoder().decode(payload));
              if (decoder) { try { decoder.close(); } catch (_) {} }
              decoder = await openDecoder(cfg);
              if (!decoder) { videoBroken = true; return 0; } // this phone cannot play it: the pictures instead
              waitKey = true;
              offset = null;
            } else if (type === 2) {
              if (!decoder || decoder.state !== 'configured') continue;
              if (waitKey && !key) continue;
              if (decoder.decodeQueueSize > 8 && !key) { waitKey = true; continue; } // the decoder is behind
              waitKey = false;
              decoder.decode(new EncodedVideoChunk({ type: key ? 'key' : 'delta', timestamp: ts, data: payload }));
            } else if (type === 3) {
              const s = JSON.parse(new TextDecoder().decode(payload)).state;
              liveClear();
              if (s === 'no-encoder' || s === 'jpeg-only') { videoBroken = true; return 0; }
              return s === 'off' ? 30000 : 2000;
            }
          }
          buf = buf.subarray(at);
        }
        return liveVisible() ? 1000 : 0;
      } catch (_) {
        liveClear();
        return 3000;
      } finally {
        liveInflight = null;
        try { ctl.abort(); } catch (_) {}
        if (reader) reader.cancel().catch(() => {});
      }
    };

    const liveLoop = async () => {
      if (liveRunning || !liveOn) return;
      liveRunning = true;
      try {
        while (liveVisible()) {
          const started = Date.now();
          const wait = await (videoBroken ? liveOne() : videoOne());
          if (!liveVisible()) break;
          await new Promise((done) => setTimeout(done, Math.max(0, wait - (Date.now() - started))));
        }
      } finally {
        liveRunning = false;
        if (!liveVisible()) liveClear(); // off screen: the agent stops capturing, so no stale picture stays up
      }
    };

    const paintVol = (v) => { vol.style.setProperty('--p', v + '%'); volVal.textContent = v; };
    if (showVolume) {
      vol.addEventListener('input', () => {
        clearTimeout(volumeRelease);
        setVolDragging(vol, true);
        paintVol(vol.value);
        clearTimeout(volTimer);
        volTimer = setTimeout(() => api('/volume?level=' + vol.value, 'POST').catch(() => {}), 120);
      });
      vol.addEventListener('change', () => {
        clearTimeout(volumeRelease);
        volumeRelease = setTimeout(() => setVolDragging(vol, false), 600);
      });
      vol.addEventListener('pointercancel', () => { clearTimeout(volTimer); clearTimeout(volumeRelease); setVolDragging(vol, false); });
    }
    // Seeking: a tap or a drag on the bar, committed on release, like the Now playing widget's.
    let scrub = null;
    const canScrub = () => !!(media && media.durMs > 0 && media.canSeek);
    const msFrom = (clientX) => {
      const r = bar.getBoundingClientRect();
      return Math.round(Math.min(1, Math.max(0, (clientX - r.left) / r.width)) * media.durMs);
    };
    const paintScrub = (ms) => { fill.style.width = (ms / media.durMs) * 100 + '%'; posText.textContent = fmtTime(ms); };
    if (showProgress) {
      bar.addEventListener('pointerdown', (e) => {
        if (!canScrub() || (e.pointerType === 'mouse' && e.button !== 0)) return;
        scrub = msFrom(e.clientX);
        bar.classList.add('scrubbing');
        paintScrub(scrub);
        try { bar.setPointerCapture(e.pointerId); } catch (_) {}
      });
      bar.addEventListener('pointermove', (e) => { if (scrub !== null) { scrub = msFrom(e.clientX); paintScrub(scrub); } });
      bar.addEventListener('pointerup', () => {
        if (scrub === null) return;
        const ms = scrub;
        scrub = null;
        bar.classList.remove('scrubbing');
        if (!canScrub()) return;
        haptic();
        // the bar stays where it was put until the agent catches up, as in the Now playing widget
        media.posMs = ms; media.at = performance.now(); media.hold = performance.now() + 2500;
        api('/media/seek?ms=' + ms, 'POST').catch(() => {});
      });
      bar.addEventListener('pointercancel', () => { scrub = null; bar.classList.remove('scrubbing'); });
    }

    return {
      update(kind) {
        if (liveOn && liveVisible()) liveLoop();
        if (kind === 'fast') {
          if (!media || scrub !== null || !showProgress) return;
          if (media.durMs > 0) {
            const pos = Math.min(media.durMs, media.posMs + (media.playing ? performance.now() - media.at : 0));
            fill.style.width = (pos / media.durMs) * 100 + '%';
            posText.textContent = fmtTime(pos);
          } else if (media.playing) {
            fill.style.width = '100%';
            posText.textContent = fmtTime(Math.max(0, (media.posMs || 0) + (performance.now() - media.at)));
          }
          return;
        }
        const m = D.state?.media, a = D.state?.audio, d = describe(m);
        if (showVolume) {
          vol.disabled = (!a || a.canControl === false);
          if (a) { if (!volDragging) { vol.value = a.volume; paintVol(a.volume); } } else volVal.textContent = 'n/a';
        }
        box.classList.toggle('idle', !m);
        box.dataset.service = d?.service || '';
        // the service's own colour tints the badge, the bar and the glow; without a known service the panel accent stays
        if (d?.color) box.style.setProperty('--np-c', d.color); else box.style.removeProperty('--np-c');
        if (!m) {
          box.classList.remove('playing');
          badgeMark.replaceChildren();
          badgeName.textContent = tp('Nothing playing');
          title.textContent = t('w.nothingPlaying');
          sub.textContent = t('w.startOnPc');
          fill.style.width = '0';
          posText.textContent = durText.textContent = '0:00';
          if (artId !== 0) { artId = 0; cover.classList.remove('on'); backImg.removeAttribute('src'); setArtColor(null); }
          return;
        }
        box.classList.toggle('playing', m.playing);
        badgeMark.innerHTML = d.mark;
        badgeName.textContent = d.service_name;
        title.textContent = d.title || tp('Unknown track');
        sub.textContent = d.sub;
        durText.textContent = d.live ? tp('LIVE') : fmtTime(m.durMs);
        if (!(m.durMs > 0) && !d.live) { fill.style.width = '0'; posText.textContent = '0:00'; }
        if (m.artId !== artId) {
          artId = m.artId;
          if (m.artId) cover.src = `${API}${relayPath(`/art?id=${m.artId}`)}`;
          else { cover.classList.remove('on'); backImg.removeAttribute('src'); setArtColor(null); }
        }
      },
      destroy() {
        clearTimeout(volTimer);
        clearTimeout(volumeRelease);
        setVolDragging(vol, false);
        liveAlive = false;
        if (liveInflight) liveInflight.abort();
        liveClear();
      },
    };
  },
};
