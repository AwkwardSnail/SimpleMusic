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
  notes: [],          // { id, rest, dur, dot, step, acc }
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
};
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
}
function pushHistory() {
  undoStack.push(snapshot());
  if (undoStack.length > 300) undoStack.shift();
  redoStack.length = 0;
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
    }));
  } catch (e) { /* storage unavailable: the sheet just won't be remembered */ }
}
function load() {
  try {
    const s = JSON.parse(localStorage.getItem(STORAGE_KEY) || 'null');
    if (!s || !Array.isArray(s.notes)) return;
    state.notes = s.notes.filter((n) => BASE_TICKS[n.dur] && Number.isInteger(n.step));
    state.cursor = state.notes.length;
    state.barred = !!s.barred;
    state.beats = clamp(s.beats | 0 || 4, 1, 16);
    if ([32, 24, 16, 12, 8, 6, 4, 2].includes(s.beatTicks)) state.beatTicks = s.beatTicks;
    state.fillLast = s.fillLast !== false;
    state.tempo = clamp(s.tempo | 0 || 90, 40, 200);
    if (BASE_TICKS[s.dur]) state.dur = s.dur;
    if (typeof s.title === 'string') state.title = s.title.slice(0, 120);
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
    first, tieNext, showAcc: first && !n.rest && !!n.acc,
  };
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

const itemWidth = (it) => 14 + 10 * Math.sqrt(it.t) + (it.dot ? 6 : 0) + (it.showAcc ? 10 : 0);
const unitWidth = (u) => u.items.reduce((s, it) => s + itemWidth(it), 0) + (u.measure ? BAR_PAD_L + BAR_PAD_R : 0);
const yAt = (staffTop, step) => staffTop + 40 - (step - E4) * HALF;

function computeLayout(W) {
  const units = buildUnits();
  const right = W - RIGHT;
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
    const staffTop = PAD_TOP + s * SYS_H + STAFF_OFFSET;
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

  return { W, lines, items, barlines, barNumbers, beams, height: PAD_TOP + lines.length * SYS_H };
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
  const L = computeLayout(W);
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

  if (print) return { L, out };

  if (!state.notes.length) {
    out += `<text class="hint" x="${L.lines[0].left + 16}" y="${L.lines[0].staffTop - 18}">Click anywhere on the staff to place your first note</text>`;
  }

  // Insertion cursor
  if (state.selectedId == null && state.playingId == null) {
    const c = cursorPosition(L);
    const t = L.lines[c.sys].staffTop;
    out += `<line class="caret" x1="${c.x}" x2="${c.x}" y1="${t - 8}" y2="${t + 48}"/>`;
  }
  return { L, out };
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
    const top = PAD_TOP + s * SYS_H;
    html += `<svg class="score-svg print-system" xmlns="${svgNS}" viewBox="0 ${top} ${PRINT_W} ${SYS_H}">${out}</svg>`;
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
  const n = state.notes.length;
  if (!n) return 'Empty sheet. Choose a note length above, then click on the staff.';
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
  return clamp(Math.floor((y - PAD_TOP) / SYS_H), 0, layout.lines.length - 1);
}

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
$('title').addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === 'Escape') e.target.blur(); });
$('btn-pdf').addEventListener('click', () => window.print());

$('btn-new').addEventListener('click', () => {
  if (state.notes.length && !confirm('Start a new, empty sheet? (You can still undo this.)')) return;
  stopPlayback();
  pushHistory();
  state.notes = [];
  state.cursor = 0;
  state.selectedId = null;
  state.barred = false;
  render();
});

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

  if (ctrl && (k === 'z' || k === 'Z')) { e.preventDefault(); e.shiftKey ? redo() : undo(); return; }
  if (ctrl && (k === 'y' || k === 'Y')) { e.preventDefault(); redo(); return; }
  if (ctrl || e.altKey) return;

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
$('title').value = state.title;
render();
if (document.fonts) document.fonts.ready.then(render);
