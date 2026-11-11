'use strict';

/* =====================================================================
   Music basics
   ===================================================================== */

// Lengths are counted in ticks: 1 tick = a 32nd note. Every note length
// we offer, dotted or not, is then a whole number of ticks.
const BASE_TICKS = { w: 32, h: 16, q: 8, e: 4, s: 2 };
const DUR_ORDER = ['w', 'h', 'q', 'e', 's'];
const DUR_NAMES = { w: 'Whole', h: 'Half', q: 'Quarter', e: 'Eighth', s: 'Sixteenth' };
const DUR_KEYS = { '1': 'w', '2': 'h', '3': 'q', '4': 'e', '5': 's' };

// Lengths that can be written as one symbol, longest first. Used to split
// notes that cross a bar line and to fill bars with rests.
const VALUES = [
  { t: 48, dur: 'w', dot: true },
  { t: 32, dur: 'w', dot: false },
  { t: 24, dur: 'h', dot: true },
  { t: 16, dur: 'h', dot: false },
  { t: 12, dur: 'q', dot: true },
  { t: 8, dur: 'q', dot: false },
  { t: 6, dur: 'e', dot: true },
  { t: 4, dur: 'e', dot: false },
  { t: 2, dur: 's', dot: false },
];

// Pitches are "steps": octave * 7 + letter index (C = 0 ... B = 6).
const LETTERS = 'CDEFGAB';
const SEMITONES = [0, 2, 4, 5, 7, 9, 11];
const E4 = 4 * 7 + 2;        // bottom line of the treble staff
const MIN_STEP = 3 * 7 + 5;  // A3
const MAX_STEP = 6 * 7 + 1;  // D6
const B4 = 4 * 7 + 6;        // middle line

const ticksOf = (n) => BASE_TICKS[n.dur] * (n.dot ? 1.5 : 1);
const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));
const stepName = (step) => LETTERS[step % 7] + Math.floor(step / 7);
const midiOf = (n) => (Math.floor(n.step / 7) + 1) * 12 + SEMITONES[n.step % 7] + (n.acc || 0);
const freqOf = (n) => 440 * Math.pow(2, (midiOf(n) - 69) / 12);
const fmt = (x) => String(Number(x.toFixed(2)));

function decompose(ticks) {
  const out = [];
  for (const v of VALUES) {
    while (ticks >= v.t) { out.push(v); ticks -= v.t; }
  }
  return out;
}

function timeSignature() {
  const bt = state.beatTicks;
  // A dotted beat means compound time: 2 dotted quarters per bar -> 6/8.
  if (bt % 3 === 0) return [state.beats * 3, 32 / (bt / 3)];
  return [state.beats, 32 / bt];
}

/* =====================================================================
   State, history and saving
   ===================================================================== */

const state = {
  notes: [],          // { id, rest, dur, dot, step, acc, lyric }
  cursor: 0,          // insertion point (index into notes)
  selectedId: null,
  dur: 'q',
  dot: false,
  restMode: false,
  barred: false,
  beats: 4,
  beatTicks: 8,
  fillLast: true,
  tempo: 90,
  title: '',
  playingId: null,
  lyricId: null,      // the note whose lyric is being typed
  lyricCaret: 0,      // text cursor inside that lyric
  fileName: null,     // the .smusic file this sheet was opened from or saved to
  savedJson: null,    // the sheet as last saved, to know about unsaved changes
  notice: null,       // a short message shown in the status line once
};
const MAX_LYRIC = 60;
let lyricPushed = false;   // a stretch of typing under one note is one undo step
let nextId = 1;
const undoStack = [];
const redoStack = [];
const STORAGE_KEY = 'simplemusic.v1';

function snapshot() {
  return JSON.stringify({ notes: state.notes, cursor: state.cursor });
}
function restore(json) {
  const s = JSON.parse(json);
  state.notes = s.notes;
  state.cursor = clamp(s.cursor, 0, s.notes.length);
  state.selectedId = null;
  if (!state.notes.some((n) => n.id === state.lyricId && !n.rest)) state.lyricId = null;
  lyricPushed = false;
}
function pushHistory() {
  undoStack.push(snapshot());
  if (undoStack.length > 300) undoStack.shift();
  redoStack.length = 0;
  lyricPushed = false;
}
function undo() {
  if (!undoStack.length) return;
  redoStack.push(snapshot());
  restore(undoStack.pop());
  render();
}
function redo() {
  if (!redoStack.length) return;
  undoStack.push(snapshot());
  restore(redoStack.pop());
  render();
}

function save() {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify({
      notes: state.notes, barred: state.barred, beats: state.beats,
      beatTicks: state.beatTicks, fillLast: state.fillLast, tempo: state.tempo, dur: state.dur, title: state.title,
      fileName: state.fileName, savedJson: state.savedJson,
    }));
  } catch (e) { /* storage unavailable: the sheet just won't be remembered */ }
}
function load() {
  try {
    const s = JSON.parse(localStorage.getItem(STORAGE_KEY) || 'null');
    if (!s || !Array.isArray(s.notes)) return;
    state.notes = s.notes.filter((n) => BASE_TICKS[n.dur] && Number.isInteger(n.step));
    for (const n of state.notes) n.lyric = n.rest ? null : cleanLyric(n.lyric);
    state.cursor = state.notes.length;
    state.barred = !!s.barred;
    state.beats = clamp(s.beats | 0 || 4, 1, 16);
    if ([32, 24, 16, 12, 8, 6, 4, 2].includes(s.beatTicks)) state.beatTicks = s.beatTicks;
    state.fillLast = s.fillLast !== false;
    state.tempo = clamp(s.tempo | 0 || 90, 40, 200);
    if (BASE_TICKS[s.dur]) state.dur = s.dur;
    if (typeof s.title === 'string') state.title = s.title.slice(0, 120);
    if (typeof s.fileName === 'string') state.fileName = s.fileName;
    if (typeof s.savedJson === 'string') state.savedJson = s.savedJson;
    nextId = state.notes.reduce((m, n) => Math.max(m, n.id || 0), 0) + 1;
  } catch (e) { /* ignore a broken save */ }
}

/* =====================================================================
   From notes to drawable items (this is where bars happen)
   ===================================================================== */

// Returns "units": in free mode one unit per note; once divided into
// bars, one unit per bar. Each unit holds the items to draw.
function buildUnits() {
  const units = [];
  if (!state.notes.length) return units;

  if (!state.barred) {
    for (const n of state.notes) {
      units.push({ measure: false, items: [makeItem(n, { t: ticksOf(n), dur: n.dur, dot: n.dot }, true, false)] });
    }
    return units;
  }

  const barTicks = state.beats * state.beatTicks;
  let bar = null;
  const newBar = () => { bar = { measure: true, number: units.length + 1, used: 0, items: [] }; units.push(bar); };
  newBar();

  for (const n of state.notes) {
    let remaining = ticksOf(n);
    let first = true;
    while (remaining > 0) {
      if (bar.used === barTicks) newBar();
      const take = Math.min(barTicks - bar.used, remaining);
      for (const part of decompose(take)) {
        remaining -= part.t;
        const it = makeItem(n, part, first, !n.rest && remaining > 0);
        it.bar = bar.number;
        it.offset = bar.used;
        bar.items.push(it);
        bar.used += part.t;
        first = false;
      }
    }
  }

  if (bar.used < barTicks && state.fillLast) {
    // Shorter rests first, so the silence grows towards the end of the bar.
    for (const part of decompose(barTicks - bar.used).reverse()) {
      bar.items.push({ note: null, virtual: true, rest: true, dur: part.dur, dot: part.dot, t: part.t, first: true, tieNext: false });
    }
  }
  return units;
}

function makeItem(n, part, first, tieNext) {
  return {
    note: n, virtual: false, rest: n.rest, step: n.step,
    dur: part.dur, dot: part.dot, t: part.t,
    first, tieNext, showAcc: first && !n.rest && !!n.acc, lyricPad: 0,
  };
}

// A lyric is free text of up to MAX_LYRIC characters; empty means none.
function cleanLyric(text) {
  if (typeof text !== 'string') return null;
  text = text.replace(/[\u0000-\u001f\u007f]/g, '').slice(0, MAX_LYRIC);
  return text.trim() ? text : null;
}

/* =====================================================================
   Layout
   ===================================================================== */

const HALF = 5;                 // half the distance between staff lines
const PAD_TOP = 8;
const SYS_H = 140;              // height of one line of music
const STAFF_OFFSET = 50;        // staff top inside a line of music
const STAFF_X = 14;
const CLEF_W = 50;
const TS_W = 34;
const RIGHT = 14;
const BAR_PAD_L = 12;
const BAR_PAD_R = 8;

// Lyrics sit under each staff; lines of music get extra room for them on screen.
const LYRIC_FONT = "15px Georgia, 'Times New Roman', serif";
const LYRIC_BASELINE = 70;   // below the staff top, unless low notes need more room
const LYRIC_EXTRA = 24;
const LYRIC_ZONE = 63;       // clicks this far below the staff top go to the lyrics
const LYRIC_GAP = 9;         // between neighbouring words
const HEAD_CLEAR = 7;        // an empty neighbour still keeps its note head clear

const noteWidth = (it) => 14 + 10 * Math.sqrt(it.t) + (it.dot ? 6 : 0) + (it.showAcc ? 10 : 0);
const itemWidth = (it) => noteWidth(it) + (it.lyricPad || 0);

const measureCtx = document.createElement('canvas').getContext('2d');
const widthCache = new Map();
function lyricWidth(s) {
  if (!s) return 0;
  let w = widthCache.get(s);
  if (w == null) {
    measureCtx.font = LYRIC_FONT;
    w = measureCtx.measureText(s).width;
    if (widthCache.size > 2000) widthCache.clear();
    widthCache.set(s, w);
  }
  return w;
}
// A trailing "-" means the word carries on: the hyphen is drawn between the notes.
const hasHyphen = (text) => !!text && text.length > 1 && text.endsWith('-');
const lyricBody = (text) => (hasHyphen(text) ? text.slice(0, -1) : text || '');

// Spreads the notes so each lyric keeps its full size: the distance from a
// sung note to the next one is at least half of each one's words, plus a gap
// (and room for a hyphen when the word carries on), as in printed music.
function makeRoomForLyrics(units) {
  const items = units.flatMap((u) => u.items);
  const sung = (it) => it.first && !it.rest && it.note;
  const half = (it) => Math.max(HEAD_CLEAR, lyricWidth(lyricBody(it.note.lyric)) / 2);
  for (let i = 0; i < items.length; i++) {
    const it = items[i];
    if (!sung(it) || !it.note.lyric) continue;
    let j = i + 1;
    let between = noteWidth(it);
    while (j < items.length && !sung(items[j])) between += noteWidth(items[j++]);
    if (j >= items.length) continue;
    const need = half(it) + LYRIC_GAP + half(items[j]) + (hasHyphen(it.note.lyric) ? lyricWidth('-') + LYRIC_GAP : 0);
    it.lyricPad = Math.max(0, need - between);
  }
}
const unitWidth = (u) => u.items.reduce((s, it) => s + itemWidth(it), 0) + (u.measure ? BAR_PAD_L + BAR_PAD_R : 0);
const yAt = (staffTop, step) => staffTop + 40 - (step - E4) * HALF;

// `roomForLyrics`: leave space under every staff even without lyrics (on
// screen, so there is always somewhere to type them; not when printing).
function computeLayout(W, roomForLyrics = true) {
  const units = buildUnits();
  makeRoomForLyrics(units);
  const right = W - RIGHT;
  const sysH = roomForLyrics || state.notes.some((n) => !n.rest && n.lyric) ? SYS_H + LYRIC_EXTRA : SYS_H;
  const lines = [];
  const newLine = () => {
    const left = STAFF_X + CLEF_W + (state.barred && lines.length === 0 ? TS_W : 0);
    const ln = { units: [], natural: 0, left };
    lines.push(ln);
    return ln;
  };
  let line = newLine();
  for (const u of units) {
    const uw = unitWidth(u);
    if (line.units.length && line.natural + uw > right - line.left) line = newLine();
    line.units.push(u);
    line.natural += uw;
  }

  const items = [];
  const barlines = [];
  const barNumbers = [];
  lines.forEach((ln, s) => {
    const avail = right - ln.left;
    const isLast = s === lines.length - 1;
    // Full lines are stretched to the right edge; the last one keeps its natural spacing.
    const scale = ln.natural > 0 && (!isLast || ln.natural > avail) ? avail / ln.natural : 1;
    const staffTop = PAD_TOP + s * sysH + STAFF_OFFSET;
    ln.staffTop = staffTop;
    let x = ln.left;
    ln.units.forEach((u, ui) => {
      if (u.measure) {
        if (ui === 0) barNumbers.push({ x: STAFF_X + 2, staffTop, number: u.number });
        x += BAR_PAD_L * scale;
      }
      for (const it of u.items) {
        it.sys = s;
        it.staffTop = staffTop;
        it.x = x;
        it.w = itemWidth(it) * scale;
        it.cx = x + (it.showAcc ? 10 : 0) + 10;
        it.y = it.rest ? staffTop + 20 : yAt(staffTop, it.step);
        items.push(it);
        x += it.w;
      }
      if (u.measure) {
        x += BAR_PAD_R * scale;
        barlines.push({ x, staffTop, final: false });
      }
    });
    ln.end = x;
  });
  if (barlines.length) barlines[barlines.length - 1].final = true;
  const beams = beamGroups(items).map(layoutBeam);

  // Lyrics: one slot per sung note, under its line, below the lowest ink.
  lines.forEach((ln, s) => { ln.lyricBaseline = Math.max(ln.staffTop + LYRIC_BASELINE, lowestInk(items, ln, s) + 16); });
  const lyrics = [];
  let prev = null;
  for (const it of items) {
    if (it.rest || !it.first || !it.note) continue;
    const slot = { note: it.note, sys: it.sys, cx: it.cx, baseline: lines[it.sys].lyricBaseline, min: STAFF_X, max: right, prev: null, next: null };
    if (prev && prev.sys === slot.sys) { slot.prev = prev; prev.next = slot; }
    lyrics.push(slot);
    prev = slot;
  }

  return { W, sysH, lines, items, barlines, barNumbers, beams, lyrics, height: PAD_TOP + lines.length * sysH };
}

// The lowest point the music reaches on a line (note heads, ledger lines,
// downward stems), so lyrics never collide with it.
function lowestInk(items, ln, sys) {
  let lowest = ln.staffTop + 40;
  for (const it of items) {
    if (it.sys !== sys || it.rest) continue;
    lowest = Math.max(lowest, it.y + 5);
    const down = it.beamed ? !it.stemUp : it.step >= B4;
    if (it.dur === 'w' || !down) continue;
    lowest = Math.max(lowest, it.beamed ? it.stemEnd + 5 : Math.max(it.y + 33, ln.staffTop + 20));
  }
  return lowest;
}

/* ---------------- Lyric room, placement and the text cursor ---------------- */

const slotText = (s) => s.note.lyric || '';

// How far a lyric may reach to each side of its note. Each neighbour keeps
// the room its own words need (up to half the distance between the notes).
function roomHalf(s) {
  const occupied = (n, d) => Math.max(HEAD_CLEAR, Math.min(lyricWidth(lyricBody(slotText(n))) / 2, d / 2));
  const left = s.prev ? s.cx - s.prev.cx - occupied(s.prev, s.cx - s.prev.cx) - LYRIC_GAP : s.cx - s.min;
  const right = s.next ? s.next.cx - s.cx - occupied(s.next, s.next.cx - s.cx) - LYRIC_GAP : s.max - s.cx;
  return Math.max(4, Math.min(left, right));
}

// Where a lyric's words are drawn. The notes make room for the words, so they
// are only squeezed sideways when a single word is wider than the line allows.
function lyricPlacement(s) {
  const w = lyricWidth(lyricBody(slotText(s)));
  const room = roomHalf(s);
  const scale = w / 2 > room ? (room * 2) / w : 1;
  return { left: s.cx - (w * scale) / 2, scale, half: (w * scale) / 2 };
}

function lyricCaretX(s, chars) {
  const text = slotText(s);
  const body = lyricBody(text);
  const { left, scale, half } = lyricPlacement(s);
  if (chars >= body.length) return body.length === 0 ? s.cx : s.cx + half + (chars > body.length ? lyricWidth('-') : 0);
  return left + lyricWidth(text.slice(0, chars)) * scale;
}

function lyricIndexAt(s, x) {
  let best = 0;
  let bestD = Infinity;
  for (let c = 0; c <= slotText(s).length; c++) {
    const d = Math.abs(lyricCaretX(s, c) - x);
    if (d < bestD) { bestD = d; best = c; }
  }
  return best;
}

function lyricSlotAt(sys, x) {
  let best = null;
  for (const s of layout.lyrics) {
    if (s.sys === sys && (!best || Math.abs(s.cx - x) < Math.abs(best.cx - x))) best = s;
  }
  return best;
}

/* =====================================================================
   Beams
   ===================================================================== */

const beamable = (it) => !it.rest && !it.virtual && (it.dur === 'e' || it.dur === 's');

// Free writing: every run of eighths/sixteenths is joined. Once divided
// into bars, runs are also cut at bar lines and at each beat (for short
// beats like an eighth, the whole bar is one group).
function beamGroups(items) {
  const groupTicks = state.beatTicks >= 8 ? state.beatTicks : state.beats * state.beatTicks;
  const groups = [];
  let cur = [];
  const flush = () => { if (cur.length > 1) groups.push(cur); cur = []; };
  for (const it of items) {
    if (!beamable(it)) { flush(); continue; }
    const prev = cur[cur.length - 1];
    if (prev && (prev.sys !== it.sys || (state.barred &&
        (prev.bar !== it.bar || Math.floor(prev.offset / groupTicks) !== Math.floor(it.offset / groupTicks))))) {
      flush();
    }
    cur.push(it);
  }
  flush();
  return groups;
}

// Decide the stem direction of a group and fit a gently sloped beam
// that keeps every stem at least a normal stem length.
function layoutBeam(g) {
  const steps = g.map((it) => it.step);
  const up = B4 - Math.min(...steps) > Math.max(...steps) - B4;
  const first = g[0];
  const last = g[g.length - 1];
  const stemX = (it) => it.cx + (up ? 5.4 : -5.4);
  const x0 = stemX(first);
  const x1 = stemX(last);
  const slope = x1 > x0 ? clamp((last.y - first.y) / (x1 - x0), -0.25, 0.25) * 0.5 : 0;
  const mid = first.staffTop + 20;
  let b = up ? Infinity : -Infinity;
  for (const it of g) {
    const dx = stemX(it) - x0;
    for (const v of [(up ? it.y - 31 : it.y + 31) - slope * dx, mid - slope * dx]) {
      b = up ? Math.min(b, v) : Math.max(b, v);
    }
  }
  const yAtX = (x) => b + slope * (x - x0);
  for (const it of g) {
    it.beamed = true;
    it.stemUp = up;
    it.stemX = stemX(it);
    it.stemEnd = yAtX(it.stemX);
  }
  return { g, up, x0, x1, yAtX };
}

function beamSvg(bm) {
  const { g, up, yAtX } = bm;
  const th = 4.6;
  const gap = 7.5;
  const d = up ? 1 : -1; // second beams sit on the note side of the first
  const quad = (xa, xb, off) => {
    const ya = yAtX(xa) + d * off;
    const yb = yAtX(xb) + d * off;
    return `<path d="M${xa},${ya} L${xb},${yb} L${xb},${yb + d * th} L${xa},${ya + d * th} Z"/>`;
  };
  let out = quad(bm.x0, bm.x1, 0);
  g.forEach((it, i) => {
    if (it.dur !== 's') return;
    const prev = g[i - 1];
    const next = g[i + 1];
    if (next && next.dur === 's') out += quad(it.stemX, next.stemX, gap);
    else if (!(prev && prev.dur === 's')) {
      // A lone sixteenth gets a short stub pointing at its neighbour.
      out += next ? quad(it.stemX, it.stemX + 9, gap) : quad(it.stemX - 9, it.stemX, gap);
    }
  });
  return `<g class="beam">${out}</g>`;
}

/* =====================================================================
   Drawing (SVG strings)
   ===================================================================== */

function headSvg(dur, cx, y) {
  if (dur === 'w') {
    return `<ellipse class="hollow" cx="${cx}" cy="${y}" rx="7.2" ry="4.6" stroke-width="2.4"/>`;
  }
  const cls = dur === 'h' ? 'class="hollow" stroke-width="1.7"' : 'fill="currentColor"';
  return `<ellipse ${cls} cx="${cx}" cy="${y}" rx="6" ry="4.3" transform="rotate(-22 ${cx} ${y})"/>`;
}

function flagsSvg(count, sx, ey, up) {
  let out = '';
  const d = up ? 1 : -1;
  for (let k = 0; k < count; k++) {
    const y0 = ey + d * k * 8;
    out += `<path fill="currentColor" d="M${sx},${y0} C${sx + 1},${y0 + d * 7} ${sx + 10},${y0 + d * 9} ${sx + 7},${y0 + d * 20} ` +
      `C${sx + 7.5},${y0 + d * 12} ${sx + 3},${y0 + d * 10.5} ${sx},${y0 + d * 9} Z"/>`;
  }
  return out;
}

function noteSvg(it) {
  const { cx, y, staffTop: t } = it;
  let out = '';
  const pos = it.step - E4;
  for (let p = -2; p >= pos; p -= 2) out += `<line class="ledger" x1="${cx - 10}" x2="${cx + 10}" y1="${t + 40 - p * HALF}" y2="${t + 40 - p * HALF}"/>`;
  for (let p = 10; p <= pos; p += 2) out += `<line class="ledger" x1="${cx - 10}" x2="${cx + 10}" y1="${t + 40 - p * HALF}" y2="${t + 40 - p * HALF}"/>`;

  if (it.showAcc) {
    const glyph = it.note.acc > 0 ? '♯' : '♭';
    out += `<text class="accidental" x="${cx - 18}" y="${y + (it.note.acc > 0 ? 6 : 4)}">${glyph}</text>`;
  }
  out += headSvg(it.dur, cx, y);

  if (it.dur !== 'w') {
    const up = it.beamed ? it.stemUp : it.step < B4;
    const mid = t + 20;
    const sx = it.beamed ? it.stemX : up ? cx + 5.4 : cx - 5.4;
    const ey = it.beamed ? it.stemEnd : up ? Math.min(y - 33, mid) : Math.max(y + 33, mid);
    out += `<line x1="${sx}" x2="${sx}" y1="${up ? y - 1 : y + 1}" y2="${ey}" stroke="currentColor" stroke-width="1.4"/>`;
    if (!it.beamed) {
      const flags = it.dur === 'e' ? 1 : it.dur === 's' ? 2 : 0;
      out += flagsSvg(flags, sx, ey, up);
    }
  }
  if (it.dot) {
    const onLine = ((pos % 2) + 2) % 2 === 0;
    out += `<circle cx="${cx + 11}" cy="${onLine ? y - HALF : y}" r="2" fill="currentColor"/>`;
  }
  return out;
}

function restSvg(dur, dot, cx, t) {
  let out = '';
  switch (dur) {
    case 'w': out += `<rect x="${cx - 6}" y="${t + 10}" width="12" height="5.5" fill="currentColor"/>`; break;
    case 'h': out += `<rect x="${cx - 6}" y="${t + 14.5}" width="12" height="5.5" fill="currentColor"/>`; break;
    case 'q':
      out += `<path fill="none" stroke="currentColor" stroke-width="2.6" stroke-linejoin="round" stroke-linecap="round" ` +
        `d="M${cx - 3},${t + 6} L${cx + 3.5},${t + 14} L${cx - 2},${t + 20.5} L${cx + 3.5},${t + 27} Q${cx - 6},${t + 23} ${cx - 1},${t + 33}"/>`;
      break;
    case 'e':
      out += `<circle cx="${cx - 3}" cy="${t + 16}" r="2.7" fill="currentColor"/>` +
        `<path fill="none" stroke="currentColor" stroke-width="1.5" d="M${cx - 3},${t + 17.5} Q${cx + 1},${t + 19} ${cx + 4},${t + 14} L${cx - 1},${t + 31}"/>`;
      break;
    case 's':
      out += `<circle cx="${cx - 2}" cy="${t + 16}" r="2.7" fill="currentColor"/>` +
        `<circle cx="${cx - 4.5}" cy="${t + 24}" r="2.7" fill="currentColor"/>` +
        `<path fill="none" stroke="currentColor" stroke-width="1.5" d="M${cx - 2},${t + 17.5} Q${cx + 2},${t + 19} ${cx + 5},${t + 14} L${cx - 1},${t + 36}"/>` +
        `<path fill="none" stroke="currentColor" stroke-width="1.5" d="M${cx - 4.5},${t + 25.5} Q${cx},${t + 27} ${cx + 2.8},${t + 22}"/>`;
      break;
  }
  if (dot) out += `<circle cx="${cx + 10}" cy="${t + 15}" r="2" fill="currentColor"/>`;
  return out;
}

function tieSvg(x1, x2, y, dir, cls) {
  const xm = (x1 + x2) / 2;
  const h = Math.min(11, 5 + (x2 - x1) * 0.06);
  return `<path class="tie ${cls}" d="M${x1},${y} Q${xm},${y + dir * h} ${x2},${y} Q${xm},${y + dir * (h - 3)} ${x1},${y} Z"/>`;
}

function itemClasses(it, print) {
  if (print) return 'item';
  if (it.virtual) return 'item virtual';
  let c = 'item';
  if (it.note.id === state.selectedId) c += ' selected';
  if (it.note.id === state.playingId) c += ' playing';
  return c;
}

let layout = null;
const scoreEl = document.getElementById('score');
const svgNS = 'http://www.w3.org/2000/svg';
const svg = document.createElementNS(svgNS, 'svg');
const musicG = document.createElementNS(svgNS, 'g');
const ghostG = document.createElementNS(svgNS, 'g');
ghostG.setAttribute('class', 'ghost');
svg.setAttribute('class', 'score-svg');
svg.append(musicG, ghostG);
scoreEl.append(svg);

// Draws the whole score at width W. `print` leaves out everything that
// only makes sense on screen (cursor, selection, hints, grey rests).
function drawScore(W, print) {
  const L = computeLayout(W, !print);
  let out = '';

  // Staves, clefs, time signature, bar numbers
  L.lines.forEach((ln, s) => {
    const t = ln.staffTop;
    for (let i = 0; i < 5; i++) out += `<line class="staff-line" x1="${STAFF_X}" x2="${W - RIGHT}" y1="${t + i * 10}" y2="${t + i * 10}"/>`;
    out += `<text class="clef" x="${STAFF_X + 4}" y="${t + 31}">𝄞</text>`;
    if (state.barred && s === 0) {
      const [num, den] = timeSignature();
      const x = STAFF_X + CLEF_W + TS_W / 2 - 4;
      out += `<text class="ts" text-anchor="middle" x="${x}" y="${t + 19}">${num}</text>`;
      out += `<text class="ts" text-anchor="middle" x="${x}" y="${t + 39}">${den}</text>`;
    }
  });
  for (const b of L.barNumbers) {
    if (b.number > 1) out += `<text class="mnum" x="${b.x}" y="${b.staffTop - 20}">${b.number}</text>`;
  }
  for (const b of L.barlines) {
    if (b.final) {
      out += `<line class="barline" x1="${b.x - 7}" x2="${b.x - 7}" y1="${b.staffTop}" y2="${b.staffTop + 40}"/>`;
      out += `<line class="barline thick" x1="${b.x - 2}" x2="${b.x - 2}" y1="${b.staffTop}" y2="${b.staffTop + 40}"/>`;
    } else {
      out += `<line class="barline" x1="${b.x}" x2="${b.x}" y1="${b.staffTop}" y2="${b.staffTop + 40}"/>`;
    }
  }

  // Notes, rests, beams and ties
  for (const bm of L.beams) out += beamSvg(bm);
  L.items.forEach((it, i) => {
    const body = it.rest ? restSvg(it.dur, it.dot, it.cx, it.staffTop) : noteSvg(it);
    out += `<g class="${itemClasses(it, print)}"${it.note ? ` data-id="${it.note.id}"` : ''}>${body}</g>`;
    if (it.tieNext) {
      const nx = L.items[i + 1];
      const dir = (it.beamed ? it.stemUp : it.step < B4) ? 1 : -1;
      const cls = itemClasses(it, print).replace('item', '');
      const y = it.y + dir * 6;
      if (nx.sys === it.sys) {
        out += tieSvg(it.cx + 6, nx.cx - 6, y, dir, cls);
      } else {
        out += tieSvg(it.cx + 6, L.lines[it.sys].end + 4, y, dir, cls);
        out += tieSvg(nx.cx - 24, nx.cx - 6, nx.y + dir * 6, dir, cls);
      }
    }
  });

  out += lyricsSvg(L, print);

  if (print) return { L, out };

  if (!state.notes.length) {
    out += `<text class="hint" x="${L.lines[0].left + 16}" y="${L.lines[0].staffTop - 18}">Click anywhere on the staff to place your first note</text>`;
  }

  // Text cursor while typing a lyric, otherwise the insertion cursor
  const editing = editSlot(L);
  if (editing) {
    const x = lyricCaretX(editing, state.lyricCaret);
    out += `<line class="caret lyric-caret" x1="${x}" x2="${x}" y1="${editing.baseline - 14}" y2="${editing.baseline + 4}"/>`;
  } else if (state.selectedId == null && state.playingId == null) {
    const c = cursorPosition(L);
    const t = L.lines[c.sys].staffTop;
    out += `<line class="caret" x1="${c.x}" x2="${c.x}" y1="${t - 8}" y2="${t + 48}"/>`;
  }
  return { L, out };
}

// Each lyric centred under its note; a word that carries on gets a hyphen
// halfway to the next note's words. On screen, empty lyrics show a faint
// line, and the lyric being typed shows its room as a faint box.
function lyricsSvg(L, print) {
  let out = '';
  for (const s of L.lyrics) {
    const text = slotText(s);
    const editing = !print && s.note.id === state.lyricId;
    if (editing) {
      const room = roomHalf(s);
      out += `<rect class="lyric-box" x="${s.cx - room}" y="${s.baseline - 15}" width="${room * 2}" height="21" rx="3"/>`;
    }
    if (!text) {
      if (!print && !editing) out += `<line class="lyric-blank" x1="${s.cx - 6}" x2="${s.cx + 6}" y1="${s.baseline + 2}" y2="${s.baseline + 2}"/>`;
      continue;
    }
    const body = lyricBody(text);
    const { left, scale, half } = lyricPlacement(s);
    if (body) {
      const squeeze = scale < 0.999 ? ` textLength="${lyricWidth(body) * scale}" lengthAdjust="spacingAndGlyphs"` : '';
      out += `<text class="lyric" x="${left}" y="${s.baseline}"${squeeze}>${escapeHtml(body)}</text>`;
    }
    if (!hasHyphen(text)) continue;
    const hw = lyricWidth('-');
    const from = s.cx + half;
    let x = from;
    if (s.next && !editing) {
      const to = s.next.cx - (slotText(s.next) ? lyricPlacement(s.next).half : 0);
      if (to - from > hw + 4) x = (from + to - hw) / 2;
    }
    out += `<text class="lyric" x="${x}" y="${s.baseline}">-</text>`;
  }
  return out;
}

function render() {
  const W = Math.max(320, Math.floor(scoreEl.clientWidth));
  const { L, out } = drawScore(W, false);
  layout = L;
  svg.setAttribute('width', W);
  svg.setAttribute('height', L.height);
  svg.setAttribute('viewBox', `0 0 ${W} ${L.height}`);
  musicG.innerHTML = out;
  ghostG.innerHTML = '';

  updateControls();
  save();
  document.title = `${displayFileName()}${isDirty() ? '*' : ''} — Simple Music Melody`;
}

/* =====================================================================
   PDF export (through the browser's "Save as PDF" print option)
   ===================================================================== */

const PRINT_W = 660; // roughly the printable width of an A4 / Letter page
const printArea = document.getElementById('print-area');
const escapeHtml = (s) => s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const sheetTitle = () => state.title.trim() || 'Untitled melody';

// One SVG per line of music, so a page break never cuts through a staff.
function buildPrintArea() {
  const { L, out } = drawScore(PRINT_W, true);
  let html = `<h1 class="print-title">${escapeHtml(sheetTitle())}</h1>`;
  if (state.barred) {
    const [num, den] = timeSignature();
    html += `<p class="print-sub">${num}/${den} · ${state.tempo} beats per minute</p>`;
  }
  L.lines.forEach((ln, s) => {
    const top = PAD_TOP + s * L.sysH;
    html += `<svg class="score-svg print-system" xmlns="${svgNS}" viewBox="0 ${top} ${PRINT_W} ${L.sysH}">${out}</svg>`;
  });
  printArea.innerHTML = html;
}

let titleBeforePrint = null;
window.addEventListener('beforeprint', () => {
  stopPlayback();
  buildPrintArea();
  titleBeforePrint = document.title;
  document.title = sheetTitle(); // becomes the suggested PDF file name
});
window.addEventListener('afterprint', () => {
  printArea.innerHTML = '';
  if (titleBeforePrint != null) document.title = titleBeforePrint;
});

function cursorPosition(L) {
  const c = state.cursor;
  if (c < state.notes.length) {
    const it = L.items.find((i) => i.note === state.notes[c] && i.first);
    return { sys: it.sys, x: it.x + 1 };
  }
  const real = L.items.filter((i) => !i.virtual);
  if (!real.length) return { sys: 0, x: L.lines[0].left + 6 };
  const last = real[real.length - 1];
  return { sys: last.sys, x: last.x + last.w - 2 };
}

/* =====================================================================
   Toolbar and status
   ===================================================================== */

function durIcon(dur, dot) {
  const filled = dur !== 'w' && dur !== 'h';
  let s = `<svg width="22" height="30" viewBox="0 0 22 30">`;
  if (dur === 'w') s += `<ellipse cx="9" cy="22" rx="6.2" ry="4" fill="none" stroke="currentColor" stroke-width="2.2"/>`;
  else {
    s += `<ellipse cx="8" cy="22" rx="5.2" ry="3.7" transform="rotate(-22 8 22)" ${filled ? 'fill="currentColor"' : 'fill="none" stroke="currentColor" stroke-width="1.6"'}/>`;
    s += `<line x1="12.6" x2="12.6" y1="21" y2="3" stroke="currentColor" stroke-width="1.4"/>`;
    const flags = dur === 'e' ? 1 : dur === 's' ? 2 : 0;
    for (let k = 0; k < flags; k++) {
      const y = 3 + k * 6;
      s += `<path fill="currentColor" d="M12.6,${y} C13.4,${y + 5} 20,${y + 6} 18,${y + 14} C18.4,${y + 8} 15,${y + 7.5} 12.6,${y + 7} Z"/>`;
    }
  }
  if (dot) s += `<circle cx="18.5" cy="22" r="1.8" fill="currentColor"/>`;
  return s + `</svg>`;
}

const durButtons = {};
const durWrap = document.getElementById('dur-buttons');
DUR_ORDER.forEach((d, i) => {
  const b = document.createElement('button');
  b.className = 'tool dur';
  b.title = `${DUR_NAMES[d]} note (key ${i + 1})`;
  b.setAttribute('aria-label', `${DUR_NAMES[d]} note`);
  b.addEventListener('click', () => chooseDuration(d));
  durWrap.append(b);
  durButtons[d] = b;
});
document.getElementById('rest-icon').innerHTML =
  `<svg width="16" height="28" viewBox="0 0 16 28"><path fill="none" stroke="currentColor" stroke-width="2.4" stroke-linejoin="round" stroke-linecap="round" d="M5,2 L11.5,10 L6,16.5 L11.5,23 Q2,19 7,27"/></svg>`;

const $ = (id) => document.getElementById(id);

function updateControls() {
  for (const d of DUR_ORDER) {
    durButtons[d].innerHTML = durIcon(d, state.dot && d !== 's');
    durButtons[d].classList.toggle('active', state.dur === d);
  }
  $('btn-dot').classList.toggle('active', state.dot);
  $('btn-dot').disabled = state.dur === 's';
  $('btn-rest').classList.toggle('active', state.restMode);
  const target = editTarget();
  $('btn-sharp').classList.toggle('active', !!target && target.acc === 1);
  $('btn-flat').classList.toggle('active', !!target && target.acc === -1);
  $('btn-sharp').disabled = $('btn-flat').disabled = !target || target.rest;
  $('btn-undo').disabled = !undoStack.length;
  $('btn-redo').disabled = !redoStack.length;
  $('btn-delete').disabled = !state.notes.length;

  const [num, den] = timeSignature();
  $('ts-preview').innerHTML = `<b>${num}</b><b>${den}</b>`;
  $('beats').value = state.beats;
  $('beat-unit').value = String(state.beatTicks);
  $('fill-last').checked = state.fillLast;
  const barBtn = $('btn-bar');
  barBtn.textContent = state.barred ? 'Back to free writing' : 'Divide into bars';
  barBtn.classList.toggle('secondary', state.barred);
  $('tempo').value = state.tempo;
  $('tempo-val').textContent = state.tempo;

  $('status').innerHTML = statusText();
}

function statusText() {
  if (state.notice) return `<b>✓ ${escapeHtml(state.notice)}</b>`;
  if (state.lyricId != null) return '<b>Typing lyrics</b> · Tab moves to the next note · Esc goes back to writing notes';
  const n = state.notes.length;
  if (!n) return 'Empty sheet. Choose a note length above, then click on the staff. Click under a note to add its lyric.';
  const total = state.notes.reduce((s, x) => s + ticksOf(x), 0);
  const beats = total / state.beatTicks;
  const unit = $('beat-unit').selectedOptions[0].textContent.toLowerCase();
  const sel = state.notes.find((x) => x.id === state.selectedId);
  const selText = sel ? ` · Selected: <b>${sel.rest ? 'rest' : stepName(sel.step) + (sel.acc > 0 ? '♯' : sel.acc < 0 ? '♭' : '')}</b> (${DUR_NAMES[sel.dur].toLowerCase()}${sel.dot ? ', dotted' : ''})` : '';
  if (!state.barred) {
    return `<b>${n}</b> ${n === 1 ? 'note' : 'notes'} · <b>${fmt(beats)}</b> beats long (counting a ${unit} as one beat)${selText}`;
  }
  const [num, den] = timeSignature();
  const full = Math.floor(beats / state.beats);
  const rest = beats - full * state.beats;
  let s = `<b>${num}/${den}</b> · `;
  if (rest === 0) s += `<b>${full}</b> ${full === 1 ? 'bar' : 'bars'}, the music fits exactly`;
  else s += `<b>${full}</b> full ${full === 1 ? 'bar' : 'bars'} + a last bar with <b>${fmt(rest)}</b> of ${state.beats} beats` +
    (state.fillLast ? ' (filled with rests)' : '');
  return s + selText;
}

/* =====================================================================
   Editing actions
   ===================================================================== */

// The note that sharp/flat/arrow keys act on: the selected one,
// or the note just before the cursor.
function editTarget() {
  if (state.selectedId != null) return state.notes.find((n) => n.id === state.selectedId) || null;
  return state.notes[state.cursor - 1] || null;
}

function selectedIndex() {
  return state.notes.findIndex((n) => n.id === state.selectedId);
}

function insertNote(index, step, rest) {
  pushHistory();
  const n = { id: nextId++, rest: !!rest, dur: state.dur, dot: state.dot && state.dur !== 's', step, acc: 0 };
  state.notes.splice(index, 0, n);
  state.cursor = index + 1;
  state.selectedId = null;
  if (!rest) previewTone(n);
  render();
}

function chooseDuration(d) {
  state.dur = d;
  if (d === 's') state.dot = false;
  const sel = state.notes.find((n) => n.id === state.selectedId);
  if (sel) {
    pushHistory();
    sel.dur = d;
    sel.dot = state.dot;
  }
  render();
}

function toggleDot() {
  if (state.dur === 's') return;
  state.dot = !state.dot;
  const sel = state.notes.find((n) => n.id === state.selectedId);
  if (sel && sel.dur !== 's') {
    pushHistory();
    sel.dot = state.dot;
  }
  render();
}

function toggleRestMode() {
  state.restMode = !state.restMode;
  const sel = state.notes.find((n) => n.id === state.selectedId);
  if (sel) {
    pushHistory();
    sel.rest = state.restMode;
    sel.acc = 0;
    if (sel.rest) sel.lyric = null;
  }
  render();
}

function setAccidental(acc) {
  const n = editTarget();
  if (!n || n.rest) return;
  pushHistory();
  n.acc = n.acc === acc ? 0 : acc;
  previewTone(n);
  render();
}

function movePitch(delta) {
  const n = editTarget();
  if (!n || n.rest) return;
  const step = clamp(n.step + delta, MIN_STEP, MAX_STEP);
  if (step === n.step) return;
  pushHistory();
  n.step = step;
  previewTone(n);
  render();
}

function deleteNote(direction) {
  if (!state.notes.length) return;
  let idx = selectedIndex();
  if (idx < 0) idx = direction < 0 ? state.cursor - 1 : state.cursor;
  if (idx < 0 || idx >= state.notes.length) return;
  pushHistory();
  if (state.notes[idx].id === state.lyricId) state.lyricId = null;
  state.notes.splice(idx, 1);
  state.cursor = idx;
  state.selectedId = null;
  render();
}

function select(note) {
  state.selectedId = note ? note.id : null;
  if (note) {
    state.cursor = state.notes.indexOf(note) + 1;
    state.dur = note.dur;
    state.dot = note.dot;
    state.restMode = note.rest;
  }
  render();
}

function moveSelection(delta) {
  const idx = selectedIndex();
  if (idx >= 0) {
    const next = idx + delta;
    if (next < 0) { state.selectedId = null; state.cursor = 0; render(); return; }
    if (next >= state.notes.length) { state.selectedId = null; state.cursor = state.notes.length; render(); return; }
    select(state.notes[next]);
  } else {
    state.cursor = clamp(state.cursor + delta, 0, state.notes.length);
    render();
  }
}

// Pick the octave of the letter that lands closest to the previous note.
function stepForLetter(letter) {
  const li = LETTERS.indexOf(letter);
  let ref = B4;
  for (let i = Math.min(state.cursor, state.notes.length) - 1; i >= 0; i--) {
    if (!state.notes[i].rest) { ref = state.notes[i].step; break; }
  }
  let best = null;
  for (let oct = 3; oct <= 6; oct++) {
    const s = oct * 7 + li;
    if (s < MIN_STEP || s > MAX_STEP) continue;
    if (best == null || Math.abs(s - ref) < Math.abs(best - ref)) best = s;
  }
  return best;
}

function insertIndexForTyping() {
  const idx = selectedIndex();
  return idx >= 0 ? idx + 1 : state.cursor;
}

/* =====================================================================
   Mouse / touch on the score
   ===================================================================== */

let drag = null;

function svgPoint(e) {
  const r = svg.getBoundingClientRect();
  return { x: (e.clientX - r.left) * (layout.W / r.width), y: (e.clientY - r.top) * (layout.height / r.height) };
}

function systemAt(y) {
  return clamp(Math.floor((y - PAD_TOP) / layout.sysH), 0, layout.lines.length - 1);
}

const inLyricZone = (p) => p.y >= layout.lines[systemAt(p.y)].staffTop + LYRIC_ZONE;

function stepAt(y, staffTop) {
  return clamp(Math.round((staffTop + 40 - y) / HALF) + E4, MIN_STEP, MAX_STEP);
}

function hitItem(x, y) {
  for (const it of layout.items) {
    if (it.virtual) continue;
    const dy = y - it.y;
    const tolY = it.rest ? 20 : 7;
    if (Math.abs(x - it.cx) <= 9 && Math.abs(dy) <= tolY) return it;
  }
  return null;
}

// Where a click at (sys, x) should insert: before the first note that
// starts to the right of the click.
function insertIndexAt(sys, x) {
  for (const it of layout.items) {
    if (!it.note || !it.first) continue;
    if (it.sys > sys || (it.sys === sys && it.cx > x)) return state.notes.indexOf(it.note);
  }
  return state.notes.length;
}

function drawGhost(x, y) {
  const sys = systemAt(y);
  const t = layout.lines[sys].staffTop;
  x = Math.max(x, layout.lines[sys].left + 8);
  let out = '';
  if (state.restMode) {
    out = restSvg(state.dur, state.dot, x, t);
  } else {
    const step = stepAt(y, t);
    const it = { cx: x, y: yAt(t, step), staffTop: t, step, dur: state.dur, dot: state.dot && state.dur !== 's', showAcc: false };
    out = noteSvg(it);
    const label = stepName(step);
    const ly = Math.min(it.y, t) - 22;
    out += `<rect class="ghost-label-bg" x="${x - 15}" y="${ly - 12}" width="30" height="17" rx="5"/>`;
    out += `<text class="ghost-label" text-anchor="middle" x="${x}" y="${ly}">${label}</text>`;
  }
  ghostG.innerHTML = out;
}

svg.addEventListener('pointerdown', (e) => {
  if (e.button !== 0) return;
  const p = svgPoint(e);
  e.preventDefault();   // keep the keyboard focus where it is (the lyric input)
  if (inLyricZone(p)) {
    const slot = lyricSlotAt(systemAt(p.y), p.x);
    if (slot) editLyric(slot, lyricIndexAt(slot, p.x));
    else stopLyrics();
    return;
  }
  stopLyrics();
  const hit = hitItem(p.x, p.y);
  if (hit) {
    select(hit.note);
    if (!hit.note.rest) {
      drag = { note: hit.note, staffTop: hit.staffTop, pushed: false };
      svg.setPointerCapture(e.pointerId);
    }
    return;
  }
  const sys = systemAt(p.y);
  const t = layout.lines[sys].staffTop;
  insertNote(insertIndexAt(sys, p.x), stepAt(p.y, t), state.restMode);
  drawGhost(p.x, p.y);
});

svg.addEventListener('pointermove', (e) => {
  const p = svgPoint(e);
  if (drag) {
    const step = stepAt(p.y, drag.staffTop);
    if (step !== drag.note.step) {
      if (!drag.pushed) { pushHistory(); drag.pushed = true; }
      drag.note.step = step;
      previewTone(drag.note);
      render();
    }
    return;
  }
  if (e.pointerType === 'touch') return;
  if (inLyricZone(p)) {
    svg.style.cursor = 'text';
    ghostG.innerHTML = '';
    return;
  }
  const hit = hitItem(p.x, p.y);
  svg.style.cursor = hit ? (hit.note.rest ? 'pointer' : 'ns-resize') : 'crosshair';
  if (hit) ghostG.innerHTML = '';
  else drawGhost(p.x, p.y);
});

const endDrag = () => { drag = null; };
svg.addEventListener('pointerup', endDrag);
svg.addEventListener('pointercancel', endDrag);
svg.addEventListener('pointerleave', () => { if (!drag) ghostG.innerHTML = ''; });

/* =====================================================================
   Typing lyrics (a hidden input receives the typing, so phone and tablet
   keyboards and accented characters work too)
   ===================================================================== */

const lyricInput = document.getElementById('lyric-input');

function editSlot(L = layout) {
  return state.lyricId == null || !L ? null : L.lyrics.find((s) => s.note.id === state.lyricId) || null;
}

function editLyric(slot, caret) {
  state.lyricId = slot.note.id;
  state.lyricCaret = clamp(caret, 0, slotText(slot).length);
  state.selectedId = null;
  lyricPushed = false;
  if (document.activeElement !== lyricInput) lyricInput.focus({ preventScroll: true });
  render();
}

function stopLyrics() {
  if (state.lyricId == null) return;
  state.lyricId = null;
  if (document.activeElement === lyricInput) lyricInput.blur();
  render();
}

// Moves to the lyric of the next (+1) or previous (-1) sung note.
function moveLyric(dir, toStart) {
  const slot = editSlot();
  if (!slot) return;
  const to = layout.lyrics[layout.lyrics.indexOf(slot) + dir];
  if (to) editLyric(to, toStart ? 0 : slotText(to).length);
}

function setLyric(note, text, caret) {
  const clean = cleanLyric(text);
  if (clean === note.lyric) { state.lyricCaret = caret; render(); return; }
  if (!lyricPushed) { pushHistory(); lyricPushed = true; }
  note.lyric = clean;
  state.lyricCaret = clamp(caret, 0, (clean || '').length);
  render();
}

// Types text at the cursor. The notes move apart to make room; only text
// wider than the staff (or longer than MAX_LYRIC) is left out.
function insertLyricText(s) {
  const slot = editSlot();
  if (!slot) return;
  s = s.replace(/[\r\n\t]/g, ' ');
  let text = slotText(slot);
  let i = clamp(state.lyricCaret, 0, text.length);
  const maxW = layout.W - RIGHT - STAFF_X - 2 * LYRIC_GAP;
  for (const c of s) {
    const next = text.slice(0, i) + c + text.slice(i);
    if (next.length > MAX_LYRIC || lyricWidth(lyricBody(next)) > maxW) break;
    text = next;
    i += c.length;
  }
  setLyric(slot.note, text, i);
}

lyricInput.addEventListener('input', () => {
  const v = lyricInput.value;
  lyricInput.value = '';
  if (v) insertLyricText(v);
});

lyricInput.addEventListener('keydown', (e) => {
  const slot = editSlot();
  if (!slot) return;
  const ctrl = e.ctrlKey || e.metaKey;
  if (ctrl) { if (handleCtrlKey(e)) e.preventDefault(); return; }
  const text = slotText(slot);
  const i = clamp(state.lyricCaret, 0, text.length);
  switch (e.key) {
    case 'Escape':
    case 'Enter':
      stopLyrics();
      break;
    case 'Tab':
      moveLyric(e.shiftKey ? -1 : 1, false);
      break;
    case 'Backspace':
      if (i > 0) setLyric(slot.note, text.slice(0, i - 1) + text.slice(i), i - 1);
      else moveLyric(-1, false);
      break;
    case 'Delete':
      if (i < text.length) setLyric(slot.note, text.slice(0, i) + text.slice(i + 1), i);
      break;
    case 'ArrowLeft':
      if (i > 0) { state.lyricCaret = i - 1; render(); } else moveLyric(-1, false);
      break;
    case 'ArrowRight':
      if (i < text.length) { state.lyricCaret = i + 1; render(); } else moveLyric(1, true);
      break;
    case 'Home': state.lyricCaret = 0; render(); break;
    case 'End': state.lyricCaret = text.length; render(); break;
    case 'ArrowUp':
    case 'ArrowDown':
      break; // nothing above or below a lyric
    default:
      return; // a character: it arrives through the input event
  }
  e.preventDefault();
});

// Clicking elsewhere (a toolbar button, the title) ends lyric typing.
lyricInput.addEventListener('blur', () => {
  setTimeout(() => { if (document.activeElement !== lyricInput) stopLyrics(); }, 0);
});

/* =====================================================================
   Sound
   ===================================================================== */

let audioCtx = null;
let playback = null;

function audio() {
  if (!audioCtx) audioCtx = new (window.AudioContext || window.webkitAudioContext)();
  if (audioCtx.state === 'suspended') audioCtx.resume();
  return audioCtx;
}

function tone(ctx, freq, t, d, vol = 0.25) {
  const o = ctx.createOscillator();
  const g = ctx.createGain();
  o.type = 'triangle';
  o.frequency.value = freq;
  g.gain.setValueAtTime(0, t);
  g.gain.linearRampToValueAtTime(vol, t + 0.012);
  g.gain.linearRampToValueAtTime(vol * 0.5, t + Math.max(0.02, d * 0.6));
  g.gain.linearRampToValueAtTime(0, t + d);
  o.connect(g).connect(ctx.destination);
  o.start(t);
  o.stop(t + d + 0.02);
  return o;
}

function previewTone(n) {
  if (playback) return;
  try { const ctx = audio(); tone(ctx, freqOf(n), ctx.currentTime, 0.3, 0.18); } catch (e) { /* no audio */ }
}

function setPlaying(id) {
  state.playingId = id;
  musicG.querySelectorAll('.playing').forEach((el) => el.classList.remove('playing'));
  if (id != null) musicG.querySelectorAll(`[data-id="${id}"]`).forEach((el) => el.classList.add('playing'));
}

function play() {
  if (playback) { stopPlayback(); return; }
  if (!state.notes.length) return;
  const ctx = audio();
  const secPerTick = 60 / state.tempo / state.beatTicks;
  const startIdx = Math.max(0, selectedIndex());
  state.selectedId = null;
  state.playingId = state.notes[startIdx].id;
  render();
  let t = ctx.currentTime + 0.08;
  const timers = [];
  const oscs = [];
  for (const n of state.notes.slice(startIdx)) {
    const d = ticksOf(n) * secPerTick;
    if (!n.rest) oscs.push(tone(ctx, freqOf(n), t, d * 0.95));
    timers.push(setTimeout(() => setPlaying(n.id), (t - ctx.currentTime) * 1000));
    t += d;
  }
  timers.push(setTimeout(stopPlayback, (t - ctx.currentTime) * 1000 + 60));
  playback = { timers, oscs };
  $('btn-play').classList.add('active');
  $('btn-play').querySelector('.tool-icon').textContent = '■';
  $('btn-play').querySelector('.tool-text').textContent = 'Stop';
}

function stopPlayback() {
  if (!playback) return;
  playback.timers.forEach(clearTimeout);
  playback.oscs.forEach((o) => { try { o.stop(); } catch (e) { /* already stopped */ } });
  playback = null;
  state.playingId = null;
  $('btn-play').classList.remove('active');
  $('btn-play').querySelector('.tool-icon').textContent = '▶';
  $('btn-play').querySelector('.tool-text').textContent = 'Play';
  render();
}

/* =====================================================================
   Sheet files (.smusic), the same format as the Windows app
   ===================================================================== */

const FILE_EXT = '.smusic';
const FORMAT_VERSION = 1;
const DUR_FILE = { w: 'W', h: 'H', q: 'Q', e: 'E', s: 'S' };
let fileHandle = null;   // where "Save" writes, when the browser can save to a chosen file

function sheetJson() {
  const notes = state.notes.map((n) => {
    const o = { Id: n.id, Rest: !!n.rest, Dur: DUR_FILE[n.dur], Dot: !!n.dot, Step: n.step, Acc: n.acc || 0 };
    if (!n.rest && n.lyric) o.Lyric = n.lyric;
    return o;
  });
  return JSON.stringify({
    App: 'SimpleMusic', FormatVersion: FORMAT_VERSION,
    Sheet: {
      Title: state.title, Notes: notes, Barred: state.barred, Beats: state.beats,
      BeatTicks: state.beatTicks, FillLast: state.fillLast, Tempo: state.tempo,
    },
  }, null, 2);
}

const isDirty = () => state.savedJson == null ? state.notes.length > 0 : sheetJson() !== state.savedJson;
const displayFileName = () => (state.fileName || 'Untitled').replace(/\.smusic$/i, '');

// Reads a .smusic file; throws an Error with a friendly message if it isn't one.
function readSheet(text) {
  let file;
  try { file = JSON.parse(text); } catch (e) { throw new Error("This file isn't a Simple Music Melody sheet, or it is damaged."); }
  if (!file || file.App !== 'SimpleMusic' || !file.Sheet) throw new Error("This file isn't a Simple Music Melody sheet.");
  if (file.FormatVersion > FORMAT_VERSION) throw new Error('This sheet was made with a newer version of Simple Music Melody.');
  const s = file.Sheet;
  const durOf = (d) => (typeof d === 'string' ? d.toLowerCase() : DUR_ORDER[d]);
  let id = 1;
  const notes = (Array.isArray(s.Notes) ? s.Notes : [])
    .map((n) => ({
      id: id++, rest: !!n.Rest, dur: durOf(n.Dur), dot: !!n.Dot,
      step: Number.isInteger(n.Step) ? n.Step : B4, acc: clamp(n.Acc | 0, -1, 1), lyric: n.Rest ? null : cleanLyric(n.Lyric),
    }))
    .filter((n) => BASE_TICKS[n.dur] && n.step >= MIN_STEP && n.step <= MAX_STEP)
    .map((n) => ({ ...n, dot: n.dot && n.dur !== 's' }));
  // Older sheets kept the lyrics as one free text: give each sung note one syllable.
  if (typeof s.Lyrics === 'string' && s.Lyrics.trim() && notes.every((n) => !n.lyric)) {
    const syllables = [];
    for (const word of s.Lyrics.split(/\s+/).filter(Boolean)) {
      const parts = word.split('-').filter(Boolean);
      parts.forEach((p, i) => syllables.push(i < parts.length - 1 ? p + '-' : p));
    }
    const sung = notes.filter((n) => !n.rest);
    sung.forEach((n, i) => { if (i < syllables.length) n.lyric = cleanLyric(syllables[i]); });
    if (sung.length && syllables.length > sung.length) {
      sung[sung.length - 1].lyric = cleanLyric(syllables.slice(sung.length - 1).join(' ').replace(/- /g, '-'));
    }
  }
  return {
    notes,
    title: typeof s.Title === 'string' ? s.Title.slice(0, 120) : '',
    barred: !!s.Barred,
    beats: clamp(s.Beats | 0 || 4, 1, 16),
    beatTicks: [32, 24, 16, 12, 8, 6, 4, 2].includes(s.BeatTicks) ? s.BeatTicks : 8,
    fillLast: s.FillLast !== false,
    tempo: clamp(s.Tempo | 0 || 90, 40, 200),
  };
}

// Puts a sheet on the page; `baseline` is what it looked like when last saved.
function attachSheet(sheet, fileName, handle) {
  stopPlayback();
  state.lyricId = null;
  state.notes = sheet.notes;
  state.title = sheet.title;
  state.barred = sheet.barred;
  state.beats = sheet.beats;
  state.beatTicks = sheet.beatTicks;
  state.fillLast = sheet.fillLast;
  state.tempo = sheet.tempo;
  state.cursor = state.notes.length;
  state.selectedId = null;
  state.fileName = fileName;
  fileHandle = handle || null;
  nextId = state.notes.reduce((m, n) => Math.max(m, n.id), 0) + 1;
  undoStack.length = 0;
  redoStack.length = 0;
  $('title').value = state.title;
  state.savedJson = sheetJson();
  render();
}

// Unsaved changes? Ask before they are replaced.
function okToDiscard() {
  if (!isDirty()) return true;
  return confirm(`${displayFileName()} has changes that aren't saved. Continue without saving them?`);
}

function newSheet() {
  if (!okToDiscard()) return;
  attachSheet({ notes: [], title: '', barred: false, beats: state.beats, beatTicks: state.beatTicks, fillLast: state.fillLast, tempo: state.tempo }, null, null);
}

async function openSheet() {
  if (!okToDiscard()) return;
  try {
    if (window.showOpenFilePicker) {
      const [handle] = await window.showOpenFilePicker({ types: [{ description: 'Simple Music Melody sheet', accept: { 'application/json': [FILE_EXT] } }] });
      const file = await handle.getFile();
      attachSheet(readSheet(await file.text()), file.name, handle);
    } else {
      $('file-input').click();   // continues in the change handler below
    }
  } catch (e) {
    if (e.name !== 'AbortError') alert(e.message);
  }
}

$('file-input').addEventListener('change', async (e) => {
  const file = e.target.files[0];
  e.target.value = '';
  if (!file) return;
  try { attachSheet(readSheet(await file.text()), file.name, null); } catch (err) { alert(`${file.name} could not be opened:\n${err.message}`); }
});

const safeName = (s) => s.replace(/[\\/:*?"<>|]+/g, ' ').trim() || 'Untitled';

// Downloads a file the browser way (when it can't save to a chosen file).
function download(name, blob) {
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = name;
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(a.href), 1000);
}

async function saveSheet(saveAs) {
  stopLyrics();
  const json = sheetJson();
  const suggested = state.fileName || `${safeName(sheetTitle())}${FILE_EXT}`;
  try {
    if (window.showSaveFilePicker) {
      if (saveAs || !fileHandle) {
        fileHandle = await window.showSaveFilePicker({ suggestedName: suggested, types: [{ description: 'Simple Music Melody sheet', accept: { 'application/json': [FILE_EXT] } }] });
      }
      const w = await fileHandle.createWritable();
      await w.write(json);
      await w.close();
      state.fileName = fileHandle.name;
    } else {
      download(suggested, new Blob([json], { type: 'application/json' }));
      state.fileName = suggested;
    }
    state.savedJson = json;
    showNotice(`Saved ${state.fileName}`);
  } catch (e) {
    if (e.name !== 'AbortError') alert(`The sheet could not be saved:\n${e.message}`);
  }
}

function showNotice(text) {
  state.notice = text;
  render();
  state.notice = null;   // gone with the next change
}

/* ---------------- MIDI export (Standard MIDI File, format 0, piano) ---------------- */

function midiBytes() {
  const PER_QUARTER = 480, PER_TICK = PER_QUARTER / 8, VELOCITY = 80;
  const track = [];
  const varLen = (v) => {
    const out = [v & 0x7f];
    while ((v >>= 7) > 0) out.unshift((v & 0x7f) | 0x80);
    return out;
  };
  const utf8 = (s) => Array.from(new TextEncoder().encode(s));
  const meta = (type, data) => track.push(0, 0xff, type, ...varLen(data.length), ...data);

  meta(0x03, utf8(sheetTitle()));                                   // track name
  // The tempo slider counts beats of the chosen beat note; MIDI wants microseconds per quarter.
  const us = Math.round((60000000 * 8) / (state.tempo * state.beatTicks));
  meta(0x51, [(us >> 16) & 0xff, (us >> 8) & 0xff, us & 0xff]);
  if (state.barred) {
    const [num, den] = timeSignature();
    meta(0x58, [num, Math.log2(den), state.beatTicks * 3, 8]);
  }
  track.push(0, 0xc0, 0);                                           // acoustic grand piano

  let wait = 0;
  for (const n of state.notes) {
    const length = ticksOf(n) * PER_TICK;
    if (n.rest) { wait += length; continue; }
    const key = clamp(midiOf(n), 0, 127);
    if (n.lyric) {
      // The note's lyric as a lyric event, just before the note sounds.
      const words = utf8(n.lyric.endsWith('-') ? n.lyric : n.lyric + ' ');
      track.push(...varLen(wait), 0xff, 0x05, ...varLen(words.length), ...words);
      wait = 0;
    }
    track.push(...varLen(wait), 0x90, key, VELOCITY, ...varLen(length), 0x80, key, 0);
    wait = 0;
  }
  track.push(...varLen(wait), 0xff, 0x2f, 0);                       // end of track (keeps trailing rests)

  const be = (v, n) => Array.from({ length: n }, (_, i) => (v >> (8 * (n - 1 - i))) & 0xff);
  return new Uint8Array([
    ...utf8('MThd'), ...be(6, 4), ...be(0, 2), ...be(1, 2), ...be(PER_QUARTER, 2),
    ...utf8('MTrk'), ...be(track.length, 4), ...track,
  ]);
}

function exportMidi() {
  if (!state.notes.length) { showNotice('Add some notes first'); return; }
  const name = `${safeName(sheetTitle())}.mid`;
  download(name, new Blob([midiBytes()], { type: 'audio/midi' }));
  showNotice(`MIDI saved as ${name}`);
}

function exportPdf() {
  stopLyrics();
  window.print();
}

// The Export menu: a small list under the button.
const exportMenu = $('export-menu');
function toggleExportMenu(open) {
  const show = open ?? exportMenu.hidden;
  exportMenu.hidden = !show;
  $('btn-export').setAttribute('aria-expanded', String(show));
}
$('btn-export').addEventListener('click', (e) => { e.stopPropagation(); toggleExportMenu(); });
$('menu-pdf').addEventListener('click', () => { toggleExportMenu(false); exportPdf(); });
$('menu-midi').addEventListener('click', () => { toggleExportMenu(false); exportMidi(); });
document.addEventListener('pointerdown', (e) => { if (!exportMenu.hidden && !e.target.closest('.export-wrap')) toggleExportMenu(false); });
document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && !exportMenu.hidden) toggleExportMenu(false); }, true);

// Ctrl/Cmd shortcuts, shared by the page and the lyric input. Returns true when handled.
function handleCtrlKey(e) {
  const k = e.key.toLowerCase();
  if (k === 'z') { e.shiftKey ? redo() : undo(); return true; }
  if (k === 'y') { redo(); return true; }
  if (k === 's') { saveSheet(e.shiftKey); return true; }
  if (k === 'o') { openSheet(); return true; }
  if (k === 'm') { exportMidi(); return true; }
  if (k === 'p') { exportPdf(); return true; }
  return false;
}

/* =====================================================================
   Wiring
   ===================================================================== */

$('btn-dot').addEventListener('click', toggleDot);
$('btn-rest').addEventListener('click', toggleRestMode);
$('btn-sharp').addEventListener('click', () => setAccidental(1));
$('btn-flat').addEventListener('click', () => setAccidental(-1));
$('btn-undo').addEventListener('click', undo);
$('btn-redo').addEventListener('click', redo);
$('btn-delete').addEventListener('click', () => deleteNote(-1));
$('btn-play').addEventListener('click', play);
$('tempo').addEventListener('input', (e) => { state.tempo = +e.target.value; $('tempo-val').textContent = state.tempo; save(); });

$('title').addEventListener('input', (e) => { state.title = e.target.value; save(); });
$('title').addEventListener('keydown', (e) => {
  if (e.key === 'Enter' || e.key === 'Escape') e.target.blur();
  else if ((e.ctrlKey || e.metaKey) && 'som'.includes(e.key.toLowerCase()) && handleCtrlKey(e)) e.preventDefault();
});
$('btn-new').addEventListener('click', newSheet);
$('btn-open').addEventListener('click', openSheet);
$('btn-save').addEventListener('click', () => saveSheet(false));
$('btn-save-as').addEventListener('click', () => saveSheet(true));
window.addEventListener('beforeunload', (e) => { if (isDirty() && state.fileName) e.preventDefault(); });

function setBeats(v) {
  state.beats = clamp(Math.round(v) || 1, 1, 16);
  render();
}
$('beats').addEventListener('change', (e) => setBeats(+e.target.value));
$('beats-minus').addEventListener('click', () => setBeats(state.beats - 1));
$('beats-plus').addEventListener('click', () => setBeats(state.beats + 1));
$('beat-unit').addEventListener('change', (e) => { state.beatTicks = +e.target.value; render(); });
$('fill-last').addEventListener('change', (e) => { state.fillLast = e.target.checked; render(); });
$('btn-bar').addEventListener('click', () => {
  state.barred = !state.barred;
  render();
  if (state.barred) scoreEl.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
});

document.addEventListener('keydown', (e) => {
  const tag = e.target.tagName;
  if (tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA') return;
  const k = e.key;
  const ctrl = e.ctrlKey || e.metaKey;

  if (ctrl) { if (handleCtrlKey(e)) e.preventDefault(); return; }
  if (e.altKey) return;

  if (/^[a-gA-G]$/.test(k)) {
    insertNote(insertIndexForTyping(), stepForLetter(k.toUpperCase()), false);
  } else if (k === 'r' || k === 'R') {
    insertNote(insertIndexForTyping(), B4, true);
  } else if (DUR_KEYS[k]) {
    chooseDuration(DUR_KEYS[k]);
  } else if (k === '.') {
    toggleDot();
  } else if (k === '+' || k === '=' || k === '#') {
    setAccidental(1);
  } else if (k === '-' || k === '_') {
    setAccidental(-1);
  } else if (k === 'ArrowUp' || k === 'ArrowDown') {
    movePitch((k === 'ArrowUp' ? 1 : -1) * (e.shiftKey ? 7 : 1));
  } else if (k === 'ArrowLeft' || k === 'ArrowRight') {
    moveSelection(k === 'ArrowLeft' ? -1 : 1);
  } else if (k === 'Home') {
    state.selectedId = null; state.cursor = 0; render();
  } else if (k === 'End') {
    state.selectedId = null; state.cursor = state.notes.length; render();
  } else if (k === 'Backspace') {
    deleteNote(-1);
  } else if (k === 'Delete') {
    deleteNote(1);
  } else if (k === 'Escape') {
    state.selectedId = null; render();
  } else if (k === ' ') {
    play();
  } else {
    return;
  }
  e.preventDefault();
});

// Keep focus off toolbar buttons so Space/Enter go to the editor, not the last button clicked.
document.addEventListener('click', (e) => {
  const b = e.target.closest('button');
  if (b) b.blur();
});

let resizeTimer = null;
window.addEventListener('resize', () => {
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(render, 80);
});

load();
if (state.savedJson == null) state.savedJson = sheetJson();   // what was remembered counts as saved
$('title').value = state.title;
render();
if (document.fonts) document.fonts.ready.then(render);
