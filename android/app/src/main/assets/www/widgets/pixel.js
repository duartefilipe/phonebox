import { D, LOCALE, cfg, clockPeriod, hour12, pad, t, wxDeg } from '../main.js';
import { W } from '../registry.js';

// ---------------------------------------------------------------- pixel clock
// A clock drawn on an LED / pixel matrix, in the style of the pixel-art desk clocks: the time in big blocky digits,
// the date, the temperature with a weather sprite, the next calendar event scrolling by and a little garden. Every part
// can be turned off and recoloured in the widget's options; the matrix adapts its size to the widget's box.

// 5x7 font: rows top to bottom, bit (w-1-x) lights column x. [width, rows]
const G = {};
const def = (chars, w, ...rows) => { for (const c of chars) G[c] = [w, rows]; };
def('0', 5, 0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E);
def('1', 5, 0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E);
def('2', 5, 0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F);
def('3', 5, 0x1F, 0x02, 0x04, 0x02, 0x01, 0x11, 0x0E);
def('4', 5, 0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02);
def('5', 5, 0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E);
def('6', 5, 0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E);
def('7', 5, 0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08);
def('8', 5, 0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E);
def('9', 5, 0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C);
def('A', 5, 0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11);
def('B', 5, 0x1E, 0x11, 0x11, 0x1E, 0x11, 0x11, 0x1E);
def('C', 5, 0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E);
def('D', 5, 0x1C, 0x12, 0x11, 0x11, 0x11, 0x12, 0x1C);
def('E', 5, 0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F);
def('F', 5, 0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10);
def('G', 5, 0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0F);
def('H', 5, 0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11);
def('I', 3, 0x07, 0x02, 0x02, 0x02, 0x02, 0x02, 0x07);
def('J', 5, 0x07, 0x02, 0x02, 0x02, 0x02, 0x12, 0x0C);
def('K', 5, 0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11);
def('L', 5, 0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F);
def('M', 5, 0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11);
def('N', 5, 0x11, 0x11, 0x19, 0x15, 0x13, 0x11, 0x11);
def('O', 5, 0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E);
def('P', 5, 0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10);
def('Q', 5, 0x0E, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0D);
def('R', 5, 0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11);
def('S', 5, 0x0F, 0x10, 0x10, 0x0E, 0x01, 0x01, 0x1E);
def('T', 5, 0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04);
def('U', 5, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E);
def('V', 5, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04);
def('W', 5, 0x11, 0x11, 0x11, 0x15, 0x15, 0x15, 0x0A);
def('X', 5, 0x11, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x11);
def('Y', 5, 0x11, 0x11, 0x11, 0x0A, 0x04, 0x04, 0x04);
def('Z', 5, 0x1F, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1F);
def(':', 2, 0, 3, 3, 0, 3, 3, 0);
def('.', 1, 0, 0, 0, 0, 0, 0, 1);
def(',', 2, 0, 0, 0, 0, 0, 1, 2);
def('-', 3, 0, 0, 0, 7, 0, 0, 0);
def('+', 5, 0, 0x04, 0x04, 0x1F, 0x04, 0x04, 0);
def('/', 5, 0x01, 0x01, 0x02, 0x04, 0x08, 0x10, 0x10);
def('%', 5, 0x18, 0x19, 0x02, 0x04, 0x08, 0x13, 0x03);
def('!', 1, 1, 1, 1, 1, 1, 0, 1);
def('?', 5, 0x0E, 0x11, 0x01, 0x02, 0x04, 0x00, 0x04);
def('&', 5, 0x0C, 0x12, 0x14, 0x08, 0x15, 0x12, 0x0D);
def('(', 3, 1, 2, 4, 4, 4, 2, 1);
def(')', 3, 4, 2, 1, 1, 1, 2, 4);
def('[', 3, 7, 4, 4, 4, 4, 4, 7);
def(']', 3, 7, 1, 1, 1, 1, 1, 7);
def('°', 3, 2, 5, 2, 0, 0, 0, 0);
def(' ', 3, 0, 0, 0, 0, 0, 0, 0);

/** Upper case without accents ("Terça" → "TERCA"); unknown characters become a space. */
const norm = (s) => String(s || '').normalize('NFD').replace(/[̀-ͯ]/g, '').toUpperCase()
  .split('').map((c) => (G[c] ? c : ' ')).join('').replace(/\s+/g, ' ');
const textW = (s) => { let w = 0; for (const c of s) w += G[c][0] + 1; return Math.max(0, w - 1); };

// 11x11 weather sprites; letters are palette entries
const PAL = { Y: '#fbbf24', O: '#f59e0b', M: '#fde68a', W: '#e5e7eb', G: '#6b7280', B: '#60a5fa', S: '#f8fafc', '*': '#cbd5e1' };
const CLOUD = ['....WWW....', '...WWWWW...', '.WWWWWWWWW.', 'WWWWWWWWWWW', 'WWWWWWWWWWW', '.GGGGGGGGG.'];
const SPR = {
  sun: ['.....Y.....', '.Y.......Y.', '....YYY....', '...YYYYY...', '..YYYYYYY..', 'Y.YYYYYYY.Y', '..YYYYYYY..', '...YYYYY...', '....YYY....', '.Y.......Y.', '.....Y.....'],
  moon: ['....MMMM...', '..MMMMM..*.', '.MMMMM.....', '.MMMM......', 'MMMM.......', 'MMMM.....*.', 'MMMM.......', '.MMMM......', '.MMMMM.....', '..MMMMM....', '....MMMM...'],
  partly: ['..Y........', 'Y.Y.Y......', '.YYY.......', 'YYYYYWWW...', '.YYYWWWWW..', 'Y.WWWWWWWWW', '..WWWWWWWWW', '..WWWWWWWWW', '...GGGGGGG.', '...........', '...........'],
  cloud: ['...........', '...........', '...........', ...CLOUD, '...........', '...........'],
  rain: [...CLOUD, '...........', '..B...B..B.', '.B...B..B..', '...........', '..B...B..B.'],
  snow: [...CLOUD, '...........', '.S...S...S.', '...S...S...', '.S...S...S.', '...........'],
  storm: [...CLOUD, '......Y....', '.....Y.....', '....YYY....', '.....Y.....', '....Y......'],
  fog: ['...........', '...........', 'GGGGGGGG...', '...........', '..GGGGGGGGG', '...........', 'GGGGGGGGG..', '...........', '...GGGGGGGG', '...........', '...........'],
};
function sky(code, day) {
  if (code == null) return null;
  if (code <= 1) return day === 0 ? 'moon' : 'sun';
  if (code === 2) return day === 0 ? 'cloud' : 'partly';
  if (code === 3) return 'cloud';
  if (code === 45 || code === 48) return 'fog';
  if ((code >= 71 && code <= 77) || code === 85 || code === 86) return 'snow';
  if (code >= 95) return 'storm';
  return 'rain';
}

// columns of the matrix: its density, whatever the size of the widget (rows follow the widget's shape)
const COLS = { small: 120, medium: 96, large: 72 };
const FLOWERS = ['#f472b6', '#facc15', '#a78bfa', '#fb7185', '#38bdf8'];

W.pixelclock = {
  bare: true, needs: ['tick', 'weather', 'calendar'],
  create(box, { o }) {
    const showDate = o.showDate !== false, showWx = o.showWeather !== false, showEvent = o.showEvent !== false;
    const garden = (o.scene || 'garden') === 'garden';
    const led = (o.style || 'led') === 'led', dimLeds = o.dim !== false;
    const col = {
      time: o.timeColor || '#ff5fa2', date: o.dateColor || '#7dd3fc', temp: o.tempColor || '#fbbf24', event: o.eventColor || '#a7f3d0',
    };
    const canvas = document.createElement('canvas');
    canvas.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;display:block';
    box.style.background = '#000';
    box.append(canvas);
    const ctx = canvas.getContext('2d');
    let C = 0, R = 0, pitch = 10, bg = null, marquee = 0, scroll = 0, eventText = '', lastKey = '';

    const resize = () => {
      const w = box.clientWidth, hh = box.clientHeight;
      if (!w || !hh) return false;
      const dpr = window.devicePixelRatio || 1;
      C = COLS[o.ledSize] || COLS.medium;
      pitch = w / C;
      R = Math.max(12, Math.floor(hh / pitch));
      canvas.width = Math.round(w * dpr);
      canvas.height = Math.round(hh * dpr);
      // the unlit matrix is drawn once per size, then copied under every frame
      bg = document.createElement('canvas');
      bg.width = canvas.width; bg.height = canvas.height;
      const b = bg.getContext('2d');
      b.fillStyle = '#000'; b.fillRect(0, 0, bg.width, bg.height);
      if (dimLeds) { b.fillStyle = 'rgba(255,255,255,0.055)'; dots(b, allCells()); }
      lastKey = `${w}x${hh}`;
      return true;
    };
    const allCells = () => { const a = []; for (let y = 0; y < R; y++) for (let x = 0; x < C; x++) a.push(x, y); return a; };
    function dots(c, cells) {
      const sx = canvas.width / C, sy = canvas.height / R, s = Math.min(sx, sy);
      c.beginPath();
      for (let i = 0; i < cells.length; i += 2) {
        const cx = (cells[i] + 0.5) * sx, cy = (cells[i + 1] + 0.5) * sy;
        if (led) { c.moveTo(cx + s * 0.4, cy); c.arc(cx, cy, s * 0.4, 0, Math.PI * 2); } else c.rect(cx - s * 0.45, cy - s * 0.45, s * 0.9, s * 0.9);
      }
      c.fill();
    }

    // a frame: color -> [x, y, x, y, …]
    let frame = new Map();
    const px = (x, y, c) => { if (x < 0 || y < 0 || x >= C || y >= R) return; let a = frame.get(c); if (!a) frame.set(c, (a = [])); a.push(x, y); };
    const text = (s, x, y, c, sc = 1, clip = null) => {
      for (const ch of s) {
        const [w, rows] = G[ch];
        for (let ry = 0; ry < 7; ry++) for (let rx = 0; rx < w; rx++) {
          if (!(rows[ry] >> (w - 1 - rx) & 1)) continue;
          for (let dy = 0; dy < sc; dy++) for (let dx = 0; dx < sc; dx++) {
            const X = x + rx * sc + dx;
            if (clip && (X < clip[0] || X > clip[1])) continue;
            px(X, y + ry * sc + dy, c);
          }
        }
        x += (w + 1) * sc;
      }
    };
    const sprite = (name, x, y, sc = 1) => {
      const rows = SPR[name];
      if (!rows) return;
      rows.forEach((r, ry) => [...r].forEach((k, rx) => {
        if (!PAL[k]) return;
        for (let dy = 0; dy < sc; dy++) for (let dx = 0; dx < sc; dx++) px(x + rx * sc + dx, y + ry * sc + dy, PAL[k]);
      }));
    };

    function nextEvent() {
      const now = Date.now();
      const list = (D.calendar?.events || []).filter((e) => e.end > now && e.start < now + 7 * 864e5);
      const e = list.find((x) => !x.allDay && x.start <= now) || list.find((x) => x.start > now) || list[0];
      if (!e) return '';
      const d = new Date(Math.max(e.start, now)), today = new Date();
      const diff = Math.round((new Date(d.toDateString()) - new Date(today.toDateString())) / 864e5);
      const day = diff <= 0 ? t('time.today') : diff === 1 ? t('time.tomorrow') : d.toLocaleDateString(LOCALE, { weekday: 'short' });
      const when = e.allDay ? '' : e.start <= now ? '' : ` ${pad(new Date(e.start).getHours())}:${pad(new Date(e.start).getMinutes())}`;
      return norm(`${day}${when} ${e.title}`);
    }

    function draw() {
      if (!box.getClientRects().length) return; // hidden (the PC-off layer while the PC is on)
      if (`${box.clientWidth}x${box.clientHeight}` !== lastKey && !resize()) return;
      if (!bg) return;
      frame = new Map();
      const now = new Date();
      const h12 = hour12();
      const H = now.getHours();
      const blink = now.getSeconds() % 2 === 0;
      const hhS = h12 ? String(H % 12 || 12) : pad(H), mmS = pad(now.getMinutes());
      const period = h12 ? norm(clockPeriod(now)) : '';
      const p = 1;
      const w = D.weather?.current;
      const kind = showWx && w ? sky(w.weather_code, w.is_day) : null;
      const temp = showWx && w ? norm(`${wxDeg(w.temperature_2m)}°${cfg.weather?.unit === 'fahrenheit' ? 'F' : 'C'}`) : '';
      const dateS = showDate ? norm(now.toLocaleDateString(LOCALE, { weekday: 'short', day: 'numeric', month: 'short' }).replace(/[.,]/g, '').replace(/ de /gi, ' ')) : '';
      // small text doubles on a big matrix, when the date still fits next to the sprite
      const ss = R >= 60 && textW(dateS) * 2 + (kind ? 22 : 0) + 6 <= C ? 2 : 1, lineH = 7 * ss + 2;
      eventText = showEvent ? nextEvent() : '';

      // the space below the time, from the bottom up: garden, event line, date / temperature with the sprite
      let gardenH = garden && R >= 30 ? 6 : 0;
      let evH = eventText ? lineH : 0;
      const infoLines = (dateS ? 1 : 0) + (temp ? 1 : 0);
      // the weather sprite doubles when the matrix is wide enough for it next to the date
      const spSc = kind && C >= 80 && textW(dateS) * ss + 22 * ss + 6 <= C ? 2 * ss : ss;
      let infoH = Math.max(infoLines * lineH, kind ? 11 * spSc + 1 : 0);
      const tw = textW(`${hhS}:${mmS}`), pw = period ? textW(period) + 2 : 0;
      // the time is as big as the width allows; parts below give way only when the height is what holds it back
      const scW = Math.floor((C - 2 * p - pw) / tw);
      const scH = () => Math.floor((R - 2 * p - gardenH - evH - infoH - 2) / 7);
      const short = () => scH() < Math.min(2, scW);
      if (short() && gardenH) gardenH = 0;
      if (short() && evH) evH = 0;
      if (short() && infoH) infoH = 0;
      const sc = Math.max(1, Math.min(scW, scH()));

      // time
      const blockW = tw * sc + pw;
      const tx = o.align === 'left' ? p + 1 : Math.max(p, Math.floor((C - blockW) / 2));
      const ty = p + 1;
      // the colon blinks once a second; the minutes keep their place either way
      text(hhS, tx, ty, col.time, sc);
      const colonX = tx + (textW(hhS) + 1) * sc;
      if (blink) text(':', colonX, ty, col.time, sc);
      text(mmS, colonX + 3 * sc, ty, col.time, sc);
      if (period) text(period, tx + tw * sc + 2, ty + 7 * sc - 7, col.time, 1);

      // date, temperature and sprite
      let y = ty + 7 * sc + Math.max(2, Math.floor((R - ty - 7 * sc - gardenH - evH - infoH) / 2));
      if (infoH) {
        const spriteW = kind ? 11 * spSc : 0;
        const left = p + 1, right = C - p - 1 - spriteW;
        let ly = y + Math.max(0, Math.floor((infoH - infoLines * lineH) / 2));
        if (dateS) { text(dateS, left, ly, col.date, ss, [0, right - 2]); ly += lineH; }
        if (temp) { text(temp, left, ly, col.temp, ss); }
        if (kind) sprite(kind, C - p - 1 - spriteW, y, spSc);
        y += infoH + 1;
      }

      // next event, scrolling when it is wider than the matrix
      if (evH) {
        const ew = textW(eventText) * ss;
        const room = C - 2 * p - 2;
        if (ew <= room) { text(eventText, p + 1, y, col.event, ss); scroll = 0; }
        else {
          const span = ew + 16;
          const off = scroll % span;
          text(eventText, p + 1 - off, y, col.event, ss, [p + 1, C - p - 2]);
          text(eventText, p + 1 - off + span, y, col.event, ss, [p + 1, C - p - 2]);
        }
        y += evH;
      }

      // garden: grass along the bottom with a few flowers
      if (gardenH) {
        const gy = R - 2;
        for (let x = 0; x < C; x++) {
          px(x, gy + 1, x % 3 === 0 ? '#15803d' : '#16a34a');
          if ((x * 7) % 5 !== 0) px(x, gy, (x * 13) % 4 === 0 ? '#15803d' : '#22c55e');
        }
        const n = Math.max(2, Math.floor(C / 14));
        for (let i = 0; i < n; i++) {
          const fx = Math.round(((i + 0.5) * C) / n + ((i * 37) % 5) - 2);
          const fc = FLOWERS[i % FLOWERS.length];
          px(fx, gy - 1, '#22c55e'); px(fx, gy - 2, '#22c55e'); px(fx - 1, gy - 2, '#16a34a');
          px(fx, gy - 4, fc); px(fx - 1, gy - 3, fc); px(fx + 1, gy - 3, fc); px(fx, gy - 3, '#fde047');
        }
      }

      ctx.drawImage(bg, 0, 0);
      for (const [c, cells] of frame) { ctx.fillStyle = c; dots(ctx, cells); }

      // keep a gentle scroll going only while the event does not fit
      const scrolling = evH && textW(eventText) * ss > C - 2 * p - 2;
      if (scrolling && !marquee) marquee = setInterval(() => { scroll++; draw(); }, 90);
      if (!scrolling && marquee) { clearInterval(marquee); marquee = 0; }
    }

    const ro = typeof ResizeObserver === 'function' ? new ResizeObserver(() => { lastKey = ''; draw(); }) : null;
    ro?.observe(box);
    return {
      needs: ['tick', ...(showWx ? ['weather'] : []), ...(showEvent ? ['calendar'] : [])],
      update: draw,
      destroy() { ro?.disconnect(); if (marquee) clearInterval(marquee); marquee = 0; },
    };
  },
};
