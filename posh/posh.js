#!/usr/bin/env node
'use strict';
// posh entry point: command-line switches, profile, REPL and Tab completion.
const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');
const { Interp, Flow, listExecutables } = require('./interp');
const { LineEditor } = require('./editor');
const { wildcardRe, hasWildcard } = require('./values');

const VERSION = '1.0.0';
const IS_WIN = process.platform === 'win32';
const DATA_DIR = path.join(os.homedir(), '.posh');
const HIST_FILE = path.join(DATA_DIR, 'history.jsonl');
const MAX_HIST = 5000;
const expandHome = p => p === '~' || /^~[\\/]/.test(p) ? os.homedir() + p.slice(1) : p;

const HELP = `posh ${VERSION} - a PowerShell-style object shell

Usage: posh [options] [script.posh [args...]]

  -NoProfile          Don't run the profile script (~/.posh/profile.posh)
  -NoLogo             Hide the startup banner
  -NoExit             Stay interactive after -Command / -File
  -Command <text>     Run commands (the rest of the line) and exit
  -EncodedCommand <b> Run a base64 (UTF-16LE) encoded command
  -File <script>      Run a .posh script with arguments and exit
  -WorkingDirectory   Start in this directory
  -NonInteractive     Never prompt; fail instead
  -Version            Print the version
  -Help               Show this help
`;

function parseArgs(argv) {
  const o = { noProfile: false, noLogo: false, noExit: false, command: null, file: null, fileArgs: [], wd: null, version: false, help: false, nonInteractive: false };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const m = /^(?:--?|\/)([\w?]+)$/.exec(a);
    if (!m) { o.file = a; o.fileArgs = argv.slice(i + 1); break; }
    const k = m[1].toLowerCase();
    const is = (full, min) => k.length >= min && full.startsWith(k);
    if (is('noprofile', 3)) o.noProfile = true;
    else if (is('nologo', 3)) o.noLogo = true;
    else if (is('noexit', 3)) o.noExit = true;
    else if (is('noninteractive', 4)) o.nonInteractive = true;
    else if (is('command', 1)) { o.command = argv.slice(i + 1).join(' '); break; }
    else if (is('encodedcommand', 2) || k === 'ec') o.command = Buffer.from(argv[++i] || '', 'base64').toString('utf16le');
    else if (is('file', 1)) { o.file = argv[i + 1]; o.fileArgs = argv.slice(i + 2); break; }
    else if (is('workingdirectory', 2) || k === 'wd') o.wd = argv[++i];
    else if (is('version', 1)) o.version = true;
    else if (is('help', 1) || k === '?') o.help = true;
    else if (is('executionpolicy', 2) || k === 'ep' || is('windowstyle', 1)) i++; // accepted for compatibility
    else if (is('login', 1) || is('interactive', 1)) { /* accepted, no-op */ }
    else { process.stderr.write(`posh: unknown option '${a}'. Try posh -Help.\n`); process.exit(64); }
  }
  return o;
}

function loadHistory() {
  try {
    return fs.readFileSync(HIST_FILE, 'utf8').split('\n').filter(Boolean).map(l => { try { return JSON.parse(l); } catch { return null; } }).filter(x => typeof x === 'string').slice(-MAX_HIST);
  } catch { return []; }
}

function saveHistoryLine(line) {
  try {
    fs.mkdirSync(DATA_DIR, { recursive: true });
    fs.appendFileSync(HIST_FILE, JSON.stringify(line) + '\n');
  } catch { /* history is best-effort */ }
}

// ---------- Tab completion ----------
function complete(interp, line, cursor) {
  const before = line.slice(0, cursor);
  let inQ = null, qStart = -1;
  for (let i = 0; i < before.length; i++) {
    const c = before[i];
    if (inQ) { if (c === inQ) inQ = null; }
    else if (c === '"' || c === "'") { inQ = c; qStart = i; }
  }
  let start = cursor;
  if (inQ) start = qStart;
  else while (start > 0 && !/[\s|;(){}=,]/.test(before[start - 1])) start--;
  const word = inQ ? before.slice(start + 1) : before.slice(start);
  const seg = before.slice(0, start);
  const segStart = Math.max(seg.lastIndexOf('|'), seg.lastIndexOf(';'), seg.lastIndexOf('{'), seg.lastIndexOf('('), seg.lastIndexOf('&&') + 1, seg.lastIndexOf('='));
  let segText = seg.slice(segStart + 1).replace(/^\s*&\s*/, '');
  const segWords = segText.trim().split(/\s+/).filter(Boolean);
  const isCmdPos = segWords.length === 0;
  const cmd = segWords[0];
  const lw = word.toLowerCase();
  const match = name => hasWildcard(word) ? wildcardRe(word + '*').test(name) : name.toLowerCase().startsWith(lw);
  const uniqSort = a => [...new Set(a)].sort((x, y) => x.localeCompare(y, undefined, { sensitivity: 'base' }));

  // $variables and $env:VARS
  const vm = /^(\$\{?)(env:)?(.*)$/i.exec(word);
  if (vm && word.startsWith('$')) {
    const prefix = vm[1] + (vm[2] || '');
    const names = vm[2] ? Object.keys(process.env) : interp.variableNames();
    const items = uniqSort(names.filter(n => n.toLowerCase().startsWith(vm[3].toLowerCase()))).map(n => prefix + n);
    return { start, items };
  }
  // -Parameters
  if (word.startsWith('-') && !isCmdPos && cmd) {
    const items = uniqSort(interp.paramNames(cmd).filter(p => p.toLowerCase().startsWith(lw.slice(1)))).map(p => '-' + p);
    if (items.length) return { start, items };
  }
  // values for parameters with known sets (-ItemType Directory, -ForegroundColor Red, ...)
  const prev = segWords[segWords.length - 1];
  if (!isCmdPos && prev && /^-\w/.test(prev) && cmd && !inQ) {
    const vals = interp.paramValues(cmd, prev.slice(1));
    const items = vals.filter(v => v.toLowerCase().startsWith(lw));
    if (items.length) return { start, items };
  }
  // command names
  if (isCmdPos && !/[\\/]/.test(word) && !word.startsWith('.') && !word.startsWith('~') && !inQ) {
    const exes = listExecutables().map(n => IS_WIN && /\.(exe|cmd|bat|com)$/i.test(n) ? n.replace(/\.(exe|cmd|bat|com)$/i, '') : n);
    const items = uniqSort([...interp.commandNames(), ...exes].filter(match));
    if (items.length) return { start, items };
  }
  // file system paths
  const sepIdx = Math.max(word.lastIndexOf('/'), word.lastIndexOf('\\'));
  const dirPart = word.slice(0, sepIdx + 1);
  const prefix = word.slice(sepIdx + 1);
  let entries;
  try { entries = fs.readdirSync(path.resolve(expandHome(dirPart || '.')), { withFileTypes: true }); } catch { return { start, items: [] }; }
  const pre = prefix.toLowerCase();
  const filtered = entries.filter(e => hasWildcard(prefix) ? wildcardRe(prefix + '*').test(e.name) : e.name.toLowerCase().startsWith(pre));
  filtered.sort((a, b) => (b.isDirectory() - a.isDirectory()) || a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }));
  const items = filtered.map(e => {
    let t = (isCmdPos && !dirPart ? '.' + path.sep : dirPart) + e.name + (e.isDirectory() ? path.sep : '');
    if (inQ) return inQ + t + (e.isDirectory() ? '' : inQ);
    if (/[\s'"&()$;,{}@`]/.test(t)) t = `'${t.replace(/'/g, "''")}'`;
    return isCmdPos && t.startsWith("'") ? '& ' + t : t;
  });
  return { start, items };
}

async function main() {
  const opts = parseArgs(process.argv.slice(2));
  if (opts.version) { process.stdout.write(`posh ${VERSION}\n`); return 0; }
  if (opts.help) { process.stdout.write(HELP); return 0; }
  if (IS_WIN && process.stdout.isTTY) {
    // make native programs print UTF-8 into this console
    try { spawnSync(process.env.ComSpec || 'cmd.exe', ['/d', '/c', 'chcp 65001 >nul'], { stdio: 'inherit' }); } catch { }
  }
  if (opts.wd) {
    try { process.chdir(path.resolve(expandHome(opts.wd))); }
    catch { process.stderr.write(`posh: cannot change to directory '${opts.wd}'\n`); }
  }

  const tty = !!(process.stdin.isTTY && process.stdout.isTTY);
  const interactive = tty && !opts.nonInteractive && (opts.noExit || (opts.command === null && !opts.file));
  let editor = null;
  const histLines = loadHistory();

  const host = {
    color: !!process.stdout.isTTY && !process.env.NO_COLOR,
    write: s => process.stdout.write(s),
    writeErr: s => process.stderr.write(s),
    columns: () => process.stdout.columns || 120,
    readLine: async (prompt, o = {}) => {
      if (opts.nonInteractive) return null;
      if (editor || tty) {
        editor = editor || new LineEditor({});
        return editor.readLine(prompt, { ...o, useHistory: false });
      }
      process.stdout.write(prompt);
      return new Promise(res => {
        let buf = '';
        const onData = d => {
          buf += d;
          const nl = buf.indexOf('\n');
          if (nl >= 0) { process.stdin.removeListener('data', onData); process.stdin.pause(); res(buf.slice(0, nl).replace(/\r$/, '')); }
        };
        process.stdin.on('data', onData);
        process.stdin.once('end', () => res(buf || null));
        process.stdin.setEncoding('utf8');
        process.stdin.resume();
      });
    },
  };

  const interp = new Interp(host);
  process.on('SIGINT', () => { interp.cancel = true; });

  // Run code at top level; returns an exit code if `exit` was called.
  const run = async (src, extra) => {
    try { await interp.runTop(src, extra); }
    catch (e) {
      if (e instanceof Flow && e.kind === 'exit') return e.value;
      process.stderr.write(`\x1b[31mposh internal error: ${e && e.stack || e}\x1b[0m\n`);
    }
    return null;
  };
  const statusCode = () => interp.global.get('?') ? 0 : 1;

  if (!opts.noProfile) {
    const profile = interp.global.get('PROFILE');
    if (fs.existsSync(profile)) {
      const code = await run(fs.readFileSync(profile, 'utf8'), { file: profile });
      if (code !== null) return code;
    }
  }

  if (opts.file) {
    const file = path.resolve(expandHome(opts.file));
    if (!fs.existsSync(file)) { process.stderr.write(`posh: The file '${opts.file}' does not exist.\n`); return 64; }
    const code = await run(fs.readFileSync(file, 'utf8'), { file, args: opts.fileArgs, scope: new (require('./interp').Scope)(interp.global) });
    if (code !== null) return code;
    if (!opts.noExit) return statusCode();
  }
  if (opts.command !== null) {
    interp.cancel = false;
    const code = await run(opts.command);
    if (code !== null) return code;
    if (!opts.noExit) return statusCode();
  }
  if (!interactive) {
    if (opts.command === null && !opts.file && !process.stdin.isTTY) {
      const src = fs.readFileSync(0, 'utf8');
      const code = await run(src);
      return code !== null ? code : statusCode();
    }
    return statusCode();
  }

  if (!opts.noLogo) {
    process.stdout.write(`\x1b[36mposh ${VERSION}\x1b[0m \x1b[90m— a PowerShell-style object shell, built from scratch for PoshTerm\x1b[0m\n\x1b[90mType 'help' for the language guide, Get-Command to list commands.\x1b[0m\n\n`);
  }
  editor = new LineEditor({
    history: histLines,
    complete: (l, c) => { try { return complete(interp, l, c); } catch { return null; } },
    isCommand: n => interp.isCommand(n),
    isIncomplete: s => interp.isIncomplete(s),
  });
  for (;;) {
    const cwd = process.cwd();
    // tab title + working directory (OSC 9;9 lets PoshTerm open new panes in the same folder)
    process.stdout.write(`\x1b]0;posh: ${path.basename(cwd) || cwd}\x07\x1b]9;9;${cwd}\x07`);
    interp.cancel = false;
    const prompt = await interp.getPrompt();
    const line = await editor.readLine(prompt);
    if (line === null || !line.trim()) continue;
    if (histLines[histLines.length - 1] !== line) { histLines.push(line); saveHistoryLine(line); if (histLines.length > MAX_HIST) histLines.shift(); }
    const entry = { line, start: new Date() };
    interp.history.push(entry);
    const t0 = Date.now();
    const code = await run(line);
    entry.duration = Date.now() - t0;
    if (code !== null) return code;
  }
}

main().then(code => process.exit(code ?? 0), e => { process.stderr.write(String(e && e.stack || e) + '\n'); process.exit(1); });
