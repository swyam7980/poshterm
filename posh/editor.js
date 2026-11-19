'use strict';
// posh line editor: raw-mode input with syntax highlighting, history (prefix search),
// Tab completion, inline history predictions, Ctrl+R search, undo and multi-line input.
// ponytail: width math assumes 1 cell per UTF-16 unit; wide CJK/emoji may misplace the cursor.

const visLen = s => s.replace(/\x1b\[[0-9;?]*[A-Za-z]|\x1b\][^\x07]*\x07/g, '').length;
const CONT = '>> ';
const KEYWORDS = new Set(['if', 'elseif', 'else', 'foreach', 'for', 'while', 'do', 'until', 'switch', 'function', 'filter', 'try', 'catch', 'finally', 'throw', 'return', 'break', 'continue', 'exit', 'param', 'begin', 'process', 'end', 'in']);
const C = { cmd: '\x1b[93m', bad: '\x1b[91m', param: '\x1b[90m', str: '\x1b[36m', var: '\x1b[92m', num: '\x1b[97m', kw: '\x1b[92m', op: '\x1b[90m', comment: '\x1b[32m', type: '\x1b[37m', R: '\x1b[0m' };

// Regex-based highlighter; isCommand decides yellow (known) vs red (unknown) command names.
function highlight(src, isCommand) {
  const rules = [
    ['comment', /#.*/y], ['str', /@"[\s\S]*?("@|$)|@'[\s\S]*?('@|$)/y],
    ['str', /"(?:[^"`]|`[\s\S]|"")*"?|'(?:[^']|'')*'?/y],
    ['var', /\$\{[^}]*\}?|\$(?:\w+:)?\w+|\$[?$^_]?/y], ['type', /\[[\w.\[\]]+\](?=\s*[$:\[(@'"\d-]|$|\s)/y],
    ['param', /-[A-Za-z_][\w-]*:?/y], ['num', /\d+(?:\.\d+)?(?:kb|mb|gb|tb)?(?![\w\\\/.:-])/iy],
    ['op', /&&|\|\||[|;&(){}@,=+*\/%<>!\[\]]/y], ['ws', /\s+/y], ['word', /[^\s|;&(){},=]+/y],
  ];
  let out = '', i = 0, cmdPos = true, prevWasWord = false;
  while (i < src.length) {
    let kind = null, text = null;
    for (const [k, re] of rules) {
      re.lastIndex = i;
      const m = re.exec(src);
      if (m && m[0].length) { kind = k; text = m[0]; break; }
    }
    if (!kind) { kind = 'word'; text = src[i]; }
    i += text.length;
    let color = '';
    if (kind === 'word') {
      if (cmdPos) {
        const lw = text.toLowerCase();
        if (KEYWORDS.has(lw)) { color = C.kw; cmdPos = lw !== 'function' && lw !== 'filter' ? cmdPos : false; out += color + text + C.R; continue; }
        color = isCommand(text) ? C.cmd : C.bad;
        cmdPos = false;
      }
    } else if (kind === 'op') {
      color = C.op;
      if (/[|;{(=]|&&|\|\|/.test(text)) cmdPos = true;
      if (text === '&') cmdPos = true;
    } else if (kind !== 'ws') {
      color = C[kind] || '';
      if (cmdPos && kind !== 'comment') cmdPos = false;
    } else if (/\n/.test(text)) cmdPos = true;
    out += color ? color + text + C.R : text;
  }
  return out;
}

function* parseKeys(s) {
  for (let i = 0; i < s.length;) {
    const c = s[i];
    if (c === '\x1b') {
      if (s[i + 1] === '[') {
        const m = /^\x1b\[([0-9;]*)([A-Za-z~])/.exec(s.slice(i, i + 16));
        if (m) {
          i += m[0].length;
          if (m[1] === '200' && m[2] === '~') { // bracketed paste
            const end = s.indexOf('\x1b[201~', i);
            const text = s.slice(i, end < 0 ? s.length : end);
            i = end < 0 ? s.length : end + 6;
            yield { paste: text };
            continue;
          }
          yield csiKey(m[1], m[2]);
          continue;
        }
      }
      if (s[i + 1] === 'O' && s[i + 2]) { i += 3; yield ({ A: 'up', B: 'down', C: 'right', D: 'left', H: 'home', F: 'end' })[s[i - 1]] || 'unknown'; continue; }
      if (s[i + 1] && s[i + 1] !== '\x1b') {
        i += 2;
        const k = s[i - 1];
        // Meta + Enter / Backspace arrive as ESC CR / ESC DEL; name them like the handler's cases
        yield k === '\r' || k === '\n' ? 'alt-enter' : k === '\x7f' || k === '\b' ? 'alt-backspace' : 'alt-' + k.toLowerCase();
        continue;
      }
      i++; yield 'esc'; continue;
    }
    i++;
    const code = c.charCodeAt(0);
    if (c === '\r') { if (s[i] === '\n') i++; yield 'enter'; }
    else if (c === '\n') yield 'enter';
    else if (c === '\x7f' || c === '\b') yield 'backspace';
    else if (c === '\t') yield 'tab';
    else if (code === 0) yield 'ctrl-space';
    else if (code === 0x1f) yield 'ctrl-backspace';
    else if (code < 32) yield 'ctrl-' + String.fromCharCode(code + 96);
    else {
      // batch printable runs (fast paste without bracketed-paste support)
      let j = i;
      while (j < s.length && s.charCodeAt(j) >= 32 && s[j] !== '\x7f' && s[j] !== '\x1b') j++;
      yield { ch: c + s.slice(i, j) };
      i = j;
    }
  }
}

function csiKey(params, final) {
  const mod = params.split(';')[1];
  const pre = mod === '5' ? 'ctrl-' : mod === '2' ? 'shift-' : mod === '3' ? 'alt-' : mod === '6' ? 'ctrl-shift-' : '';
  const base = { A: 'up', B: 'down', C: 'right', D: 'left', H: 'home', F: 'end', Z: 'shift-tab' }[final];
  if (base) return final === 'Z' ? base : pre + base;
  if (final === '~') {
    const n = params.split(';')[0];
    const k = { 1: 'home', 7: 'home', 4: 'end', 8: 'end', 3: 'delete', 2: 'insert', 5: 'pageup', 6: 'pagedown' }[n];
    return k ? pre + k : 'unknown';
  }
  return 'unknown';
}

const isWordChar = c => /[\w$-]/.test(c);

class LineEditor {
  constructor({ input = process.stdin, output = process.stdout, history = [], complete = null, isCommand = () => true, isIncomplete = () => false }) {
    this.input = input; this.output = output; this.history = history;
    this.complete = complete; this.isCommand = isCommand; this.isIncomplete = isIncomplete;
    this.onData = this.onData.bind(this);
    this.s = null;
  }

  readLine(prompt, { mask = false, useHistory = true } = {}) {
    return new Promise(resolve => {
      this.s = { buf: '', cur: 0, prompt, mask, useHistory, hIdx: this.history.length, hPrefix: null, saved: '', comp: null, search: null, undo: [], rows: 0, resolve };
      if (this.input.isTTY) this.input.setRawMode(true);
      this.output.write('\x1b[?2004h'); // bracketed paste
      this.input.on('data', this.onData);
      this.input.resume();
      this.render();
    });
  }

  onData(chunk) {
    if (!this.s) return;
    for (const k of parseKeys(chunk.toString('utf8'))) {
      if (!this.s) return;
      this.handle(k);
    }
    if (this.s) this.render();
  }

  finish(value, suffix = '') {
    const s = this.s;
    s.search = null;
    this.render(true);
    this.output.write(suffix + '\r\n\x1b[?2004l');
    this.input.removeListener('data', this.onData);
    if (this.input.isTTY) this.input.setRawMode(false);
    this.input.pause();
    this.s = null;
    s.resolve(value);
  }

  pushUndo() {
    const s = this.s;
    const last = s.undo[s.undo.length - 1];
    if (!last || last.buf !== s.buf) s.undo.push({ buf: s.buf, cur: s.cur });
    if (s.undo.length > 200) s.undo.shift();
  }

  insert(text) {
    const s = this.s;
    this.pushUndo();
    s.buf = s.buf.slice(0, s.cur) + text + s.buf.slice(s.cur);
    s.cur += text.length;
  }

  wordLeft(pos) {
    const b = this.s.buf;
    while (pos > 0 && !isWordChar(b[pos - 1])) pos--;
    while (pos > 0 && isWordChar(b[pos - 1])) pos--;
    return pos;
  }
  wordRight(pos) {
    const b = this.s.buf;
    while (pos < b.length && !isWordChar(b[pos])) pos++;
    while (pos < b.length && isWordChar(b[pos])) pos++;
    return pos;
  }

  prediction() {
    const s = this.s;
    if (s.mask || !s.useHistory || !s.buf.trim() || s.cur !== s.buf.length || s.search) return '';
    for (let i = this.history.length - 1; i >= 0; i--) {
      const h = this.history[i];
      if (h.length > s.buf.length && h.startsWith(s.buf) && !h.includes('\n')) return h.slice(s.buf.length);
    }
    return '';
  }

  historyMove(dir) {
    const s = this.s;
    if (!s.useHistory || !this.history.length) return;
    if (s.hPrefix === null) { s.hPrefix = s.buf.slice(0, s.cur) === s.buf ? s.buf : ''; s.saved = s.buf; }
    let i = s.hIdx;
    for (;;) {
      i += dir;
      if (i < 0) return;
      if (i >= this.history.length) { s.hIdx = this.history.length; s.buf = s.saved; s.cur = s.buf.length; return; }
      if (this.history[i].startsWith(s.hPrefix) && this.history[i] !== s.buf) break;
    }
    s.hIdx = i;
    s.buf = this.history[i];
    s.cur = s.buf.length;
  }

  lineOf(pos) {
    const before = this.s.buf.slice(0, pos);
    const line = before.split('\n').length - 1;
    return { line, col: pos - (before.lastIndexOf('\n') + 1) };
  }

  moveVertical(dir) {
    const s = this.s;
    const lines = s.buf.split('\n');
    const { line, col } = this.lineOf(s.cur);
    const target = line + dir;
    if (target < 0 || target >= lines.length) return false;
    let pos = 0;
    for (let i = 0; i < target; i++) pos += lines[i].length + 1;
    s.cur = pos + Math.min(col, lines[target].length);
    return true;
  }

  doComplete(dir) {
    const s = this.s;
    if (!this.complete) return;
    if (!s.comp) {
      const r = this.complete(s.buf, s.cur);
      if (!r || !r.items.length) { this.output.write('\x07'); return; }
      s.comp = { start: r.start, items: r.items, idx: -1, orig: s.buf, origCur: s.cur };
      this.pushUndo();
    }
    const c = s.comp;
    c.idx = (c.idx + dir + c.items.length) % c.items.length;
    const item = c.items[c.idx];
    s.buf = c.orig.slice(0, c.start) + item + c.orig.slice(c.origCur);
    s.cur = c.start + item.length;
  }

  listCompletions() {
    const s = this.s;
    if (!this.complete) return;
    const r = this.complete(s.buf, s.cur);
    if (!r || !r.items.length) { this.output.write('\x07'); return; }
    if (r.items.length === 1) { this.pushUndo(); s.buf = s.buf.slice(0, r.start) + r.items[0] + s.buf.slice(s.cur); s.cur = r.start + r.items[0].length; return; }
    // common prefix first
    let pre = r.items[0];
    for (const it of r.items) while (!it.toLowerCase().startsWith(pre.toLowerCase())) pre = pre.slice(0, -1);
    const typed = s.buf.slice(r.start, s.cur);
    if (pre.length > typed.length) { this.pushUndo(); s.buf = s.buf.slice(0, r.start) + pre + s.buf.slice(s.cur); s.cur = r.start + pre.length; return; }
    const cols = this.output.columns || 80;
    const w = Math.min(Math.max(...r.items.map(x => x.length)) + 2, cols);
    const per = Math.max(1, Math.floor(cols / w));
    const shown = r.items.slice(0, 200);
    let text = '';
    for (let i = 0; i < shown.length; i += per) text += shown.slice(i, i + per).map(x => x.padEnd(w)).join('').trimEnd() + '\r\n';
    if (r.items.length > shown.length) text += `\x1b[90m... and ${r.items.length - shown.length} more\x1b[0m\r\n`;
    this.moveToEnd();
    this.output.write('\r\n' + text);
    s.rows = 0;
  }

  moveToEnd() {
    const s = this.s;
    const down = (s.endRow || 0) - s.rows;
    if (down > 0) this.output.write(`\x1b[${down}B`);
    s.rows = s.endRow || 0;
  }

  handle(k) {
    const s = this.s;
    if (s.search) return this.handleSearch(k);
    if (k !== 'tab' && k !== 'shift-tab') s.comp = null;
    if (k !== 'up' && k !== 'down' && s.hPrefix !== null) { s.hPrefix = null; s.hIdx = this.history.length; }
    if (typeof k === 'object') {
      if (k.paste !== undefined) { this.insert(k.paste.replace(/\r\n?/g, '\n')); return; }
      this.insert(k.ch);
      return;
    }
    const b = s.buf;
    switch (k) {
      case 'enter':
        if (!s.mask && this.isIncomplete(b)) { this.insert('\n'); return; }
        if (s.useHistory) s.hIdx = this.history.length;
        return this.finish(b);
      case 'backspace':
        if (s.cur > 0) { this.pushUndo(); s.buf = b.slice(0, s.cur - 1) + b.slice(s.cur); s.cur--; }
        return;
      case 'delete':
        if (s.cur < b.length) { this.pushUndo(); s.buf = b.slice(0, s.cur) + b.slice(s.cur + 1); }
        return;
      case 'ctrl-d':
        if (!b.length) return; // keep the session open, like PowerShell
        if (s.cur < b.length) { this.pushUndo(); s.buf = b.slice(0, s.cur) + b.slice(s.cur + 1); }
        return;
      case 'left': case 'ctrl-b': if (s.cur > 0) s.cur--; return;
      case 'right': case 'ctrl-f': {
        if (s.cur < b.length) { s.cur++; return; }
        const p = this.prediction();
        if (p) this.insert(p);
        return;
      }
      case 'end': case 'ctrl-e': {
        if (s.cur === b.length) { const p = this.prediction(); if (p) this.insert(p); return; }
        const nl = b.indexOf('\n', s.cur);
        s.cur = nl < 0 ? b.length : nl;
        return;
      }
      case 'home': case 'ctrl-a': s.cur = b.lastIndexOf('\n', s.cur - 1) + 1; return;
      case 'ctrl-left': case 'alt-b': s.cur = this.wordLeft(s.cur); return;
      case 'ctrl-right': case 'alt-f': {
        if (s.cur === b.length) { const p = this.prediction(); if (p) { const m = /^\s*\S+/.exec(p); this.insert(m ? m[0] : p); } return; }
        s.cur = this.wordRight(s.cur);
        return;
      }
      case 'ctrl-backspace': case 'ctrl-w': case 'alt-backspace': {
        const p = this.wordLeft(s.cur);
        if (p < s.cur) { this.pushUndo(); s.buf = b.slice(0, p) + b.slice(s.cur); s.cur = p; }
        return;
      }
      case 'ctrl-delete': case 'alt-d': {
        const p = this.wordRight(s.cur);
        if (p > s.cur) { this.pushUndo(); s.buf = b.slice(0, s.cur) + b.slice(p); }
        return;
      }
      case 'ctrl-u': this.pushUndo(); s.buf = b.slice(s.cur); s.cur = 0; return;
      case 'ctrl-k': this.pushUndo(); s.buf = b.slice(0, s.cur); return;
      case 'esc': if (b) { this.pushUndo(); s.buf = ''; s.cur = 0; } return;
      case 'ctrl-z': { const u = s.undo.pop(); if (u) { s.buf = u.buf; s.cur = u.cur; } return; }
      case 'ctrl-c': return this.finish(null, '\x1b[90m^C\x1b[0m');
      case 'ctrl-l':
        this.output.write('\x1b[2J\x1b[3J\x1b[H');
        s.rows = 0;
        return;
      case 'up': case 'ctrl-p':
        if (b.includes('\n') && this.moveVertical(-1)) return;
        return this.historyMove(-1);
      case 'down': case 'ctrl-n':
        if (b.includes('\n') && this.moveVertical(1)) return;
        return this.historyMove(1);
      case 'tab': return this.doComplete(1);
      case 'shift-tab': return this.doComplete(-1);
      case 'ctrl-space': return this.listCompletions();
      case 'ctrl-r': case 'ctrl-s':
        if (!s.useHistory) return;
        s.search = { q: '', idx: this.history.length, match: null, orig: b, origCur: s.cur };
        return;
      case 'shift-enter': case 'alt-enter': this.insert('\n'); return;
    }
  }

  handleSearch(k) {
    const s = this.s, sr = s.search;
    const find = (from) => {
      for (let i = from; i >= 0; i--) if (sr.q && this.history[i].toLowerCase().includes(sr.q.toLowerCase())) { sr.idx = i; sr.match = this.history[i]; return true; }
      return false;
    };
    if (typeof k === 'object') { sr.q += k.ch ?? k.paste ?? ''; if (!find(this.history.length - 1)) sr.match = null; return; }
    switch (k) {
      case 'backspace': sr.q = sr.q.slice(0, -1); if (!find(this.history.length - 1)) sr.match = null; return;
      case 'ctrl-r': find(sr.idx - 1); return;
      case 'enter':
        s.search = null;
        if (sr.match) { s.buf = sr.match; s.cur = s.buf.length; }
        return this.finish(s.buf);
      case 'ctrl-c': case 'ctrl-g':
        s.search = null; s.buf = sr.orig; s.cur = sr.origCur; return;
      default:
        s.search = null;
        if (sr.match) { s.buf = sr.match; s.cur = s.buf.length; }
        if (k !== 'esc') this.handle(k);
    }
  }

  render(final = false) {
    const s = this.s;
    const cols = this.output.columns || 80;
    let prompt = s.prompt, text = s.buf, cursor = s.cur, hint = '';
    if (s.search) {
      prompt = `\x1b[90m(history search)\x1b[0m '${s.search.q}'${s.search.match || !s.search.q ? '' : ' \x1b[91m(no match)\x1b[0m'}: `;
      text = s.search.match || '';
      cursor = text.length;
    } else if (!final) hint = this.prediction();
    const shown = s.mask ? '*'.repeat(text.length) : text;
    const colored = s.mask || s.search ? shown : highlight(shown, this.isCommand);
    // split highlighted text by line, carrying an open color across the newline
    const plainLines = shown.split('\n');
    const hlLines = colored.split('\n');
    for (let i = 1; i < hlLines.length; i++) {
      const codes = hlLines[i - 1].match(/\x1b\[[0-9;]*m/g);
      const last = codes && codes[codes.length - 1];
      if (last && last !== '\x1b[0m') hlLines[i] = last + hlLines[i];
    }
    let out = '\x1b[?25l';
    if (s.rows > 0) out += `\x1b[${s.rows}A`;
    out += '\r\x1b[J';
    let row = 0, cursorRow = 0, cursorCol = 0, endRow = 0;
    const { line: cLine, col: cCol } = (() => { const before = shown.slice(0, cursor); return { line: before.split('\n').length - 1, col: cursor - (before.lastIndexOf('\n') + 1) }; })();
    for (let i = 0; i < plainLines.length; i++) {
      const pre = i === 0 ? prompt : '\x1b[90m' + CONT + '\x1b[0m';
      const preLen = i === 0 ? visLen(prompt) : CONT.length;
      const isLast = i === plainLines.length - 1;
      const extra = isLast && hint ? '\x1b[90m' + hint + '\x1b[0m' : '';
      const len = preLen + plainLines[i].length + (isLast ? hint.length : 0);
      out += pre + hlLines[i] + extra;
      if (i === cLine) { const off = preLen + cCol; cursorRow = row + Math.floor(off / cols); cursorCol = off % cols; }
      const lineRows = Math.max(1, Math.ceil(len / cols));
      if (!isLast) { out += '\r\n'; row += lineRows; }
      else {
        if (len > 0 && len % cols === 0) { out += '\r\n'; endRow = row + len / cols; }
        else endRow = row + Math.floor(len / cols);
      }
    }
    if (endRow > cursorRow) out += `\x1b[${endRow - cursorRow}A`;
    out += '\r' + (cursorCol ? `\x1b[${cursorCol}C` : '') + '\x1b[?25h';
    s.rows = cursorRow;
    s.endRow = endRow;
    this.output.write(out);
    if (final) this.moveToEnd();
  }
}

module.exports = { LineEditor, highlight, parseKeys };
