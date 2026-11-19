'use strict';
// Built-in cmdlets. Param spec strings: "Name|Alias1|Alias2" + flags
//   *  positional   +  positional, swallows the rest   !  switch   =a,b,c  completion values
const fs = require('fs');
const path = require('path');
const os = require('os');
const crypto = require('crypto');
const { spawnSync, spawn } = require('child_process');
const V = require('./values');
const { formatOutput, renderTable, renderList, cell } = require('./format');
const {
  PoshError, ScriptBlock, obj, hashtable, isHash, isObj, toBool, toNum, toStr, getMember, compare, eq,
  wildcardRe, hasWildcard, fmtDate, stripAnsi, timeSpan, typeOf, TYPE, findKey,
} = V;

const IS_WIN = process.platform === 'win32';
const COLORS = ['Black', 'DarkBlue', 'DarkGreen', 'DarkCyan', 'DarkRed', 'DarkMagenta', 'DarkYellow', 'Gray', 'DarkGray', 'Blue', 'Green', 'Cyan', 'Red', 'Magenta', 'Yellow', 'White'];
const FG = { black: 30, darkblue: 34, darkgreen: 32, darkcyan: 36, darkred: 31, darkmagenta: 35, darkyellow: 33, gray: 37, darkgray: 90, blue: 94, green: 92, cyan: 96, red: 91, magenta: 95, yellow: 93, white: 97 };

function parseSpec(s) {
  const [head, vals] = s.split('=');
  const m = /^(.*?)([*+!]*)$/.exec(head);
  const [name, ...aliases] = m[1].split('|');
  return {
    name, aliases, switch: m[2].includes('!'), positional: m[2].includes('*') || m[2].includes('+'), rest: m[2].includes('+'),
    values: vals ? vals.split(',') : null,
  };
}

const arr = v => v === null || v === undefined ? [] : Array.isArray(v) ? v : [v];
const unwrap = a => a.length === 0 ? null : a.length === 1 ? a[0] : a;
const expandHome = p => p === '~' || p.startsWith('~/') || p.startsWith('~\\') ? os.homedir() + p.slice(1) : p;
const resolve = p => path.resolve(process.cwd(), expandHome(toStr(p)));
const itemPath = x => typeof x === 'string' ? x : x && (x.FullName || x.Path || x.PSPath) ? (x.FullName || x.Path || x.PSPath) : toStr(x);

// Expands wildcards in the last path segment.
// ponytail: only the final segment may contain wildcards; add recursive globbing if needed.
function expandPaths(p) {
  const full = resolve(p);
  const base = path.basename(full);
  if (!hasWildcard(base)) return [full];
  const dir = path.dirname(full);
  let names = [];
  try { names = fs.readdirSync(dir); } catch { return []; }
  const re = wildcardRe(base);
  return names.filter(n => re.test(n)).sort(cmpName).map(n => path.join(dir, n));
}
const cmpName = (a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' });

function modeString(st, isDir, isLink, full) {
  const ro = !(st.mode & 0o200);
  const hidden = path.basename(full).startsWith('.');
  return (isLink ? 'l' : isDir ? 'd' : '-') + (isDir ? '-' : 'a') + (ro ? 'r' : '-') + (hidden ? 'h' : '-') + '-';
}

function makeItem(full) {
  let lst, st;
  try { lst = fs.lstatSync(full); } catch { return null; }
  const isLink = lst.isSymbolicLink();
  try { st = isLink ? fs.statSync(full) : lst; } catch { st = lst; }
  const isDir = st.isDirectory();
  const parsed = path.parse(full);
  const name = parsed.base || full;
  const o = obj(isDir ? 'DirectoryInfo' : 'FileInfo', {
    Mode: modeString(st, isDir, isLink, full),
    LastWriteTime: st.mtime,
    Length: isDir ? null : st.size,
    Name: name,
    FullName: full,
    Extension: isDir ? '' : parsed.ext,
    BaseName: isDir ? name : parsed.name,
    CreationTime: st.birthtime,
    LastAccessTime: st.atime,
    PSIsContainer: isDir,
    PSParentPath: path.dirname(full),
  });
  if (isDir) o.Parent = path.dirname(full); else o.DirectoryName = path.dirname(full);
  if (isLink) { try { o.LinkTarget = fs.readlinkSync(full); } catch { } }
  return o;
}

function readLines(file) {
  const t = fs.readFileSync(file, 'utf8').replace(/^\ufeff/, '');
  const l = t.split(/\r?\n/);
  if (l.length && l[l.length - 1] === '') l.pop();
  return l;
}

function textOf(values) { return stripAnsi(formatOutput(values, 4096, false)); }

function writeText(file, text, append, encoding = 'utf8') {
  const enc = /^(unicode|utf-?16)/i.test(encoding) ? 'utf16le' : /^ascii$/i.test(encoding) ? 'ascii' : 'utf8';
  const bom = /bom/i.test(encoding) && enc === 'utf8' ? '\ufeff' : '';
  if (append) fs.appendFileSync(file, text, enc); else fs.writeFileSync(file, bom + text, enc);
}

// csv
function csvField(v) { const s = toStr(v); return '"' + s.replace(/"/g, '""') + '"'; }
function toCsv(items, delim = ',', noHeader = false) {
  if (!items.length) return [];
  const first = items.find(isObj) || {};
  const keys = Object.keys(first);
  const lines = noHeader ? [] : [keys.map(csvField).join(delim)];
  for (const it of items) lines.push(isObj(it) ? keys.map(k => csvField(getMember(it, k))).join(delim) : csvField(it));
  return lines;
}
function parseCsv(text, delim = ',', header = null) {
  const rows = [];
  let row = [], f = '', q = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (q) {
      if (c === '"') { if (text[i + 1] === '"') { f += '"'; i++; } else q = false; }
      else f += c;
    } else if (c === '"') q = true;
    else if (c === delim) { row.push(f); f = ''; }
    else if (c === '\n' || c === '\r') {
      if (c === '\r' && text[i + 1] === '\n') i++;
      row.push(f); f = ''; rows.push(row); row = [];
    } else f += c;
  }
  if (f !== '' || row.length) { row.push(f); rows.push(row); }
  const data = rows.filter(r => !(r.length === 1 && r[0] === '') && !(r[0] || '').startsWith('#TYPE'));
  const keys = header ? arr(header).map(toStr) : data.shift() || [];
  return data.map(r => obj(null, Object.fromEntries(keys.map((k, i) => [k, r[i] ?? null]))));
}

function jsonToPs(v, asHash) {
  if (Array.isArray(v)) return v.map(x => jsonToPs(x, asHash));
  if (v && typeof v === 'object') {
    const o = asHash ? hashtable() : obj(null, {});
    for (const [k, x] of Object.entries(v)) o[k] = jsonToPs(x, asHash);
    return o;
  }
  return v;
}
function psToJson(v, depth) {
  if (v === undefined) return null;
  if (v === null || typeof v === 'string' || typeof v === 'boolean') return v;
  if (typeof v === 'number') return Number.isFinite(v) ? v : toStr(v);
  if (v instanceof Date) return v.toISOString();
  if (v instanceof ScriptBlock) return v.text;
  if (depth < 0) return toStr(v);
  if (Array.isArray(v)) return v.map(x => psToJson(x, depth - 1));
  const o = {};
  for (const k of Object.keys(v)) o[k] = psToJson(v[k], depth - 1);
  return o;
}

function listProcesses() {
  if (IS_WIN) {
    const r = spawnSync('tasklist.exe', ['/fo', 'csv', '/nh'], { encoding: 'utf8' });
    return parseCsv(r.stdout || '', ',', ['Image', 'PID', 'Session', 'SessionNum', 'Mem']).map(p => obj('Process', {
      Id: Number(p.PID), ProcessName: String(p.Image).replace(/\.exe$/i, ''), Name: String(p.Image).replace(/\.exe$/i, ''),
      SessionId: Number(p.SessionNum), WorkingSet: Number(String(p.Mem).replace(/\D/g, '')) * 1024, Path: null,
    }));
  }
  const r = spawnSync('ps', ['-eo', 'pid=,rss=,comm='], { encoding: 'utf8' });
  return (r.stdout || '').split('\n').filter(Boolean).map(l => {
    const m = /^\s*(\d+)\s+(\d+)\s+(.*)$/.exec(l);
    return m && obj('Process', { Id: Number(m[1]), ProcessName: m[3], Name: m[3], SessionId: 0, WorkingSet: Number(m[2]) * 1024 });
  }).filter(Boolean);
}

function selectProps(o, props) {
  const out = obj(null, {});
  for (const p of props) {
    if (isHash(p)) {
      const name = toStr(getMember(p, 'Name') ?? getMember(p, 'N') ?? getMember(p, 'Label') ?? getMember(p, 'L'));
      out[name] = p.__value;
    } else if (typeof p === 'string' && hasWildcard(p)) {
      const re = wildcardRe(p);
      for (const k of Object.keys(isObj(o) ? o : {})) if (re.test(k)) out[k] = o[k];
    } else out[toStr(p)] = getMember(o, toStr(p));
  }
  return out;
}

async function propValue(ctx, o, p) {
  if (p instanceof ScriptBlock) return unwrap(await ctx.interp.invokeWithItem(p, o, ctx.scope));
  if (isHash(p)) {
    const e = getMember(p, 'Expression') ?? getMember(p, 'E');
    return e instanceof ScriptBlock ? unwrap(await ctx.interp.invokeWithItem(e, o, ctx.scope)) : getMember(o, toStr(e));
  }
  return getMember(o, toStr(p));
}

function helpText(interp, c) {
  const syntax = c.spec.map(p => {
    const t = p.switch ? `-${p.name}` : p.positional ? `[-${p.name}] <${p.values ? p.values.join('|') : 'Object'}>` : `-${p.name} <${p.values ? p.values.join('|') : 'Object'}>`;
    return p.positional && !p.switch ? t : `[${t}]`;
  }).join(' ');
  const aliases = [...interp.aliases].filter(([, v]) => v.toLowerCase() === c.name.toLowerCase()).map(([k]) => k);
  return [
    '', 'NAME', `    ${c.name}`, '', 'SYNOPSIS', `    ${c.help || ''}`, '', 'SYNTAX', `    ${c.name} ${syntax}`, '',
    'ALIASES', `    ${aliases.join(', ') || 'None'}`, '',
  ];
}

const ABOUT = `
posh — the native shell of PoshTerm

LANGUAGE
    Variables      $x = 5; $env:PATH; $_ (current pipeline item); $args; $LASTEXITCODE; $?
    Strings        "expands $x and $(1+1)"   'literal'   @" here-string "@   escapes: \`n \`t \`e \`u{1F600}
    Collections    @(1,2,3)  1..10  @{ Name = 'x'; Size = 3 }  [pscustomobject]@{ A = 1 }
    Operators      + - * / %  -eq -ne -gt -ge -lt -le  -like -match -replace -split -join
                   -contains -in -and -or -not !  -is -as  -f  (prefix c for case-sensitive: -ceq)
    Flow           if/elseif/else  foreach ($i in $list) {}  for  while  do/until  switch  try/catch/finally  throw
    Functions      function Get-Thing { param([string]$Name, [switch]$Force) process { $_ } }
    Pipelines      Get-ChildItem | Where-Object Length -gt 1kb | Sort-Object Length -Descending | Select-Object -First 5
    Chains         cmd1 && cmd2 || cmd3
    Redirection    > file  >> file  2> err.txt  2>&1  > $null
    Externals      any program on PATH runs directly: git status, notepad file.txt
    Scripts        .\\script.posh arg1 arg2      dot-source: . .\\lib.posh

STARTUP
    Profile script: $PROFILE (~/.posh/profile.posh)   posh -NoProfile -NoLogo -Command <cmd> -File <script> -NoExit

KEYS
    Tab / Shift+Tab complete · Ctrl+Space list completions · → accept prediction · Ctrl+R history search
    Ctrl+Z undo · Ctrl+L clear · Ctrl+←/→ word move · Esc clear line · Ctrl+C cancel

Run Get-Command to list every command, Get-Help <name> for details.
`;

function register(interp) {
  const def = (name, aliases, params, help, run) => {
    const c = { name, spec: params.map(parseSpec), help, run };
    interp.cmdlets.set(name.toLowerCase(), c);
    for (const a of aliases) interp.aliases.set(a.toLowerCase(), name);
  };
  const alias = (a, target) => interp.aliases.set(a.toLowerCase(), target);
  const host = () => interp.host;
  const colorize = (s, fg, bg) => {
    if (!host().color) return s;
    let pre = '';
    if (fg) { const c = FG[toStr(fg).toLowerCase()]; if (c) pre += `\x1b[${c}m`; }
    if (bg) { const c = FG[toStr(bg).toLowerCase()]; if (c) pre += `\x1b[${c + 10}m`; }
    return pre ? pre + s + '\x1b[0m' : s;
  };

  // ---------------- locations ----------------
  def('Get-Location', ['pwd', 'gl'], [], 'Gets the current working directory.', () => [obj('PathInfo', { Path: process.cwd(), toString() { return this.Path; } })]);

  def('Set-Location', ['cd', 'sl', 'chdir'], ['Path|LiteralPath*', 'PassThru!'], 'Sets the current working directory. "cd -" goes back, "cd ~" goes home.', (p, input) => {
    let target = p.Path ?? (input && input[0] ? itemPath(input[0]) : os.homedir());
    target = toStr(target);
    if (target === '-') { if (!interp.prevDir) throw new PoshError('There is no location history left to navigate backwards.'); target = interp.prevDir; }
    if (/^[a-z]:$/i.test(target)) target += path.sep;
    const full = hasWildcard(path.basename(target)) ? expandPaths(target)[0] : resolve(target);
    if (!full) throw new PoshError(`Cannot find path '${target}' because it does not exist.`, { category: 'ObjectNotFound' });
    let st;
    try { st = fs.statSync(full); } catch { throw new PoshError(`Cannot find path '${full}' because it does not exist.`, { category: 'ObjectNotFound' }); }
    if (!st.isDirectory()) throw new PoshError(`Cannot set location to '${full}' because it is not a directory.`);
    interp.prevDir = process.cwd();
    process.chdir(full);
    return p.PassThru ? [obj('PathInfo', { Path: full, toString() { return this.Path; } })] : [];
  });

  def('Push-Location', ['pushd'], ['Path*'], 'Saves the current location on a stack and optionally changes to a new one.', async (p, input, ctx) => {
    interp.locStack.push(process.cwd());
    if (p.Path !== undefined) await interp.cmdlets.get('set-location').run({ Path: p.Path }, null, ctx);
  });
  def('Pop-Location', ['popd'], [], 'Changes to the location most recently pushed with Push-Location.', () => {
    const d = interp.locStack.pop();
    if (d) { interp.prevDir = process.cwd(); process.chdir(d); }
  });

  // ---------------- items ----------------
  def('Get-ChildItem', ['ls', 'dir', 'gci'], ['Path|LiteralPath*', 'Filter*', 'Recurse|s!', 'Depth', 'Force!', 'Directory|ad!', 'File|af!', 'Name!', 'Include', 'Exclude', 'Hidden|ah!'],
    'Lists the items in one or more locations. Use env: to list environment variables.', (p, input, ctx) => {
      const paths = p.Path !== undefined ? arr(p.Path).map(toStr) : input && input.length ? input.map(itemPath) : ['.'];
      const out = [];
      const inc = p.Include ? arr(p.Include).map(x => wildcardRe(toStr(x))) : null;
      const exc = p.Exclude ? arr(p.Exclude).map(x => wildcardRe(toStr(x))) : null;
      const filt = p.Filter ? wildcardRe(toStr(p.Filter)) : null;
      const maxDepth = p.Depth !== undefined ? toNum(p.Depth) : p.Recurse ? Infinity : 0;
      const keep = it => {
        if (p.Directory && !it.PSIsContainer) return false;
        if (p.File && it.PSIsContainer) return false;
        if (p.Hidden && !it.Mode.includes('h')) return false;
        if (filt && !filt.test(it.Name)) return false;
        if (inc && !inc.some(r => r.test(it.Name))) return false;
        if (exc && exc.some(r => r.test(it.Name))) return false;
        return true;
      };
      const walk = (dir, depth, rel) => {
        let names;
        try { names = fs.readdirSync(dir); } catch (e) { ctx.writeError(`Access to the path '${dir}' is denied.`, { category: 'PermissionDenied' }); return; }
        const items = names.map(n => makeItem(path.join(dir, n))).filter(Boolean);
        items.sort((a, b) => (b.PSIsContainer - a.PSIsContainer) || cmpName(a.Name, b.Name));
        const kept = items.filter(keep);
        out.push(...(p.Name ? kept.map(i => rel ? path.join(rel, i.Name) : i.Name) : kept));
        if (depth < maxDepth) for (const it of items) if (it.PSIsContainer && !it.Mode.startsWith('l')) {
          interp.checkCancel();
          walk(it.FullName, depth + 1, rel ? path.join(rel, it.Name) : it.Name);
        }
      };
      for (const raw of paths) {
        if (/^env:[\\/]?$/i.test(raw)) {
          out.push(...Object.keys(process.env).sort(cmpName).map(k => obj('EnvEntry', { Name: k, Value: process.env[k], Key: k })));
          continue;
        }
        const em = /^env:[\\/]?(.+)$/i.exec(raw);
        if (em) { const re = wildcardRe(em[1]); out.push(...Object.keys(process.env).filter(k => re.test(k)).sort(cmpName).map(k => obj('EnvEntry', { Name: k, Value: process.env[k], Key: k }))); continue; }
        const targets = expandPaths(raw);
        if (!targets.length || (targets.length === 1 && !fs.existsSync(targets[0]))) {
          ctx.writeError(`Cannot find path '${resolve(raw)}' because it does not exist.`, { category: 'ObjectNotFound' });
          continue;
        }
        for (const t of targets) {
          const it = makeItem(t);
          if (!it) continue;
          if (it.PSIsContainer && (!hasWildcard(path.basename(raw)) || p.Recurse)) walk(t, 0, '');
          else if (keep(it)) out.push(p.Name ? it.Name : it);
        }
      }
      return out;
    });

  def('Get-Item', ['gi'], ['Path|LiteralPath*', 'Force!'], 'Gets the item at the specified location.', (p, input, ctx) => {
    const out = [];
    for (const raw of (p.Path !== undefined ? arr(p.Path) : (input || [])).map(itemPath)) {
      if (/^env:/i.test(raw)) { const k = raw.replace(/^env:[\\/]?/i, ''); out.push(obj('EnvEntry', { Name: k, Value: process.env[k] ?? null })); continue; }
      const t = expandPaths(raw).map(makeItem).filter(Boolean);
      if (!t.length) ctx.writeError(`Cannot find path '${resolve(raw)}' because it does not exist.`, { category: 'ObjectNotFound' });
      out.push(...t);
    }
    return out;
  });

  def('New-Item', ['ni'], ['Path*', 'ItemType|Type=File,Directory,SymbolicLink,HardLink', 'Name', 'Value', 'Force!'], 'Creates a new file, directory or link.', (p, input) => {
    const out = [];
    for (const base of arr(p.Path ?? '.')) {
      const full = p.Name !== undefined ? resolve(path.join(toStr(base), toStr(p.Name))) : resolve(base);
      const type = toStr(p.ItemType || 'File').toLowerCase();
      if (fs.existsSync(full) && !p.Force && type !== 'directory') throw new PoshError(`The file '${full}' already exists.`, { category: 'ResourceExists' });
      if (type === 'directory') {
        if (fs.existsSync(full) && !p.Force) throw new PoshError(`An item with the specified name ${full} already exists.`, { category: 'ResourceExists' });
        fs.mkdirSync(full, { recursive: true });
      } else if (type === 'symboliclink') fs.symlinkSync(resolve(p.Value), full);
      else if (type === 'hardlink') fs.linkSync(resolve(p.Value), full);
      else {
        if (p.Force) fs.mkdirSync(path.dirname(full), { recursive: true });
        fs.writeFileSync(full, p.Value !== undefined ? toStr(p.Value) : (input && input.length ? toStr(unwrap(input)) : ''));
      }
      out.push(makeItem(full));
    }
    return out;
  });

  def('mkdir', ['md'], ['Path+'], 'Creates one or more directories (including parents).', (p) => {
    return arr(p.Path).map(x => { const f = resolve(x); fs.mkdirSync(f, { recursive: true }); return makeItem(f); });
  });

  def('Remove-Item', ['rm', 'del', 'erase', 'rd', 'rmdir', 'ri'], ['Path|LiteralPath*', 'Recurse|r!', 'Force|f!', 'Include', 'Exclude'], 'Deletes files and directories.', (p, input, ctx) => {
    const raws = p.Path !== undefined ? arr(p.Path).map(toStr) : (input || []).map(itemPath);
    for (const raw of raws) {
      const targets = expandPaths(raw);
      if (!targets.length || !targets.some(t => { try { fs.lstatSync(t); return true; } catch { return false; } })) {
        ctx.writeError(`Cannot find path '${resolve(raw)}' because it does not exist.`, { category: 'ObjectNotFound' });
        continue;
      }
      for (const t of targets) {
        let st;
        try { st = fs.lstatSync(t); } catch { continue; }
        if (st.isDirectory() && !p.Recurse && fs.readdirSync(t).length) {
          ctx.writeError(`The item at ${t} has children and the Recurse parameter was not specified. Use -Recurse to delete it.`, { category: 'InvalidOperation' });
          continue;
        }
        try { fs.rmSync(t, { recursive: true, force: !!p.Force }); }
        catch (e) { ctx.writeError(`Cannot remove item ${t}: ${e.message}`, { category: 'PermissionDenied' }); }
      }
    }
  });

  def('Copy-Item', ['cp', 'copy', 'cpi'], ['Path|LiteralPath*', 'Destination*', 'Recurse|r!', 'Force!', 'PassThru!'], 'Copies files and directories.', (p, input, ctx) => {
    const dest = resolve(p.Destination ?? '.');
    const out = [];
    for (const raw of p.Path !== undefined ? arr(p.Path).map(toStr) : (input || []).map(itemPath)) {
      for (const src of expandPaths(raw)) {
        if (!fs.existsSync(src)) { ctx.writeError(`Cannot find path '${src}' because it does not exist.`, { category: 'ObjectNotFound' }); continue; }
        const isDir = fs.statSync(src).isDirectory();
        let to = dest;
        try { if (fs.statSync(dest).isDirectory()) to = path.join(dest, path.basename(src)); } catch { }
        if (isDir && !p.Recurse) { fs.mkdirSync(to, { recursive: true }); }
        else fs.cpSync(src, to, { recursive: true, force: true });
        if (p.PassThru) out.push(makeItem(to));
      }
    }
    return out;
  });

  def('Move-Item', ['mv', 'move', 'mi'], ['Path|LiteralPath*', 'Destination*', 'Force!', 'PassThru!'], 'Moves or renames files and directories.', (p, input, ctx) => {
    const dest = resolve(p.Destination ?? '.');
    const out = [];
    for (const raw of p.Path !== undefined ? arr(p.Path).map(toStr) : (input || []).map(itemPath)) {
      for (const src of expandPaths(raw)) {
        if (!fs.existsSync(src)) { ctx.writeError(`Cannot find path '${src}' because it does not exist.`, { category: 'ObjectNotFound' }); continue; }
        let to = dest;
        try { if (fs.statSync(dest).isDirectory()) to = path.join(dest, path.basename(src)); } catch { }
        if (fs.existsSync(to) && !p.Force && to !== src) { ctx.writeError(`Cannot create a file when that file already exists: ${to}`, { category: 'ResourceExists' }); continue; }
        try { fs.renameSync(src, to); }
        catch (e) { if (e.code !== 'EXDEV') throw e; fs.cpSync(src, to, { recursive: true }); fs.rmSync(src, { recursive: true }); }
        if (p.PassThru) out.push(makeItem(to));
      }
    }
    return out;
  });

  def('Rename-Item', ['ren', 'rni'], ['Path|LiteralPath*', 'NewName*', 'PassThru!'], 'Renames an item.', (p, input) => {
    const src = resolve(p.Path ?? itemPath(input && input[0]));
    const to = path.join(path.dirname(src), toStr(p.NewName));
    fs.renameSync(src, to);
    return p.PassThru ? [makeItem(to)] : [];
  });

  def('Test-Path', [], ['Path|LiteralPath*', 'PathType|Type=Any,Leaf,Container', 'IsValid!'], 'Returns $true if the path exists.', (p, input) => {
    return (p.Path !== undefined ? arr(p.Path) : (input || [])).map(raw => {
      raw = itemPath(raw);
      if (/^env:/i.test(raw)) return process.env[raw.replace(/^env:[\\/]?/i, '')] !== undefined;
      if (p.IsValid) return !/[\0<>"|?*]/.test(path.basename(raw)) || hasWildcard(raw);
      const targets = expandPaths(raw);
      return targets.some(t => {
        try {
          const st = fs.statSync(t);
          const type = toStr(p.PathType || 'Any').toLowerCase();
          return type === 'leaf' ? !st.isDirectory() : type === 'container' ? st.isDirectory() : true;
        } catch { return false; }
      });
    });
  });

  def('Resolve-Path', ['rvpa'], ['Path|LiteralPath*', 'Relative!'], 'Resolves wildcards and relative paths into full paths.', (p, input, ctx) => {
    const out = [];
    for (const raw of (p.Path !== undefined ? arr(p.Path) : input || []).map(itemPath)) {
      const t = expandPaths(raw).filter(x => fs.existsSync(x));
      if (!t.length) ctx.writeError(`Cannot find path '${resolve(raw)}' because it does not exist.`, { category: 'ObjectNotFound' });
      out.push(...t.map(x => p.Relative ? '.' + path.sep + path.relative(process.cwd(), x) : obj('PathInfo', { Path: x, toString() { return this.Path; } })));
    }
    return out;
  });

  def('Join-Path', [], ['Path*', 'ChildPath*', 'AdditionalChildPath+'], 'Combines a path and child paths.', (p) => {
    return arr(p.Path).map(b => path.join(toStr(b), ...arr(p.ChildPath).map(toStr), ...arr(p.AdditionalChildPath).map(toStr)));
  });

  def('Split-Path', [], ['Path*', 'Parent!', 'Leaf!', 'LeafBase!', 'Extension!', 'Qualifier!', 'NoQualifier!', 'IsAbsolute!'], 'Returns part of a path.', (p, input) => {
    return (p.Path !== undefined ? arr(p.Path) : input || []).map(x => {
      const s = itemPath(x);
      if (p.Leaf) return path.basename(s);
      if (p.LeafBase) return path.parse(s).name;
      if (p.Extension) return path.extname(s);
      if (p.Qualifier) return (/^[a-z]:/i.exec(s) || [''])[0];
      if (p.NoQualifier) return s.replace(/^[a-z]:/i, '');
      if (p.IsAbsolute) return path.isAbsolute(s);
      const d = path.dirname(s);
      return d === '.' && !/[\\/]/.test(s) ? '' : d;
    });
  });

  // ---------------- content ----------------
  def('Get-Content', ['gc', 'cat', 'type'], ['Path|LiteralPath*', 'TotalCount|First|Head', 'Tail|Last', 'Raw!', 'Encoding=utf8,unicode,ascii'], 'Gets the lines of a file.', (p, input, ctx) => {
    const out = [];
    for (const raw of (p.Path !== undefined ? arr(p.Path) : input || []).map(itemPath)) {
      const files = expandPaths(raw);
      if (!files.length || !fs.existsSync(files[0])) { ctx.writeError(`Cannot find path '${resolve(raw)}' because it does not exist.`, { category: 'ObjectNotFound' }); continue; }
      for (const f of files) {
        if (fs.statSync(f).isDirectory()) { ctx.writeError(`Unable to get content because it is a directory: '${f}'.`); continue; }
        if (p.Raw) { out.push(fs.readFileSync(f, /unicode/i.test(toStr(p.Encoding)) ? 'utf16le' : 'utf8').replace(/^\ufeff/, '')); continue; }
        let lines = readLines(f);
        if (p.TotalCount !== undefined) lines = lines.slice(0, toNum(p.TotalCount));
        if (p.Tail !== undefined) lines = lines.slice(-toNum(p.Tail));
        out.push(...lines);
      }
    }
    return out;
  });

  const setContent = append => (p, input) => {
    const values = p.Value !== undefined ? arr(p.Value) : input || [];
    const text = values.map(toStr).join(os.EOL) + (p.NoNewline || !values.length ? '' : os.EOL);
    for (const raw of arr(p.Path)) {
      const files = hasWildcard(path.basename(toStr(raw))) ? expandPaths(raw) : [resolve(raw)];
      for (const f of files) writeText(f, p.NoNewline ? values.map(toStr).join('') : text, append, toStr(p.Encoding || 'utf8'));
    }
  };
  def('Set-Content', ['sc'], ['Path*', 'Value*', 'NoNewline!', 'Encoding=utf8,utf8BOM,unicode,ascii'], 'Writes content to a file, replacing it.', setContent(false));
  def('Add-Content', ['ac'], ['Path*', 'Value*', 'NoNewline!', 'Encoding=utf8,utf8BOM,unicode,ascii'], 'Appends content to a file.', setContent(true));
  def('Clear-Content', ['clc'], ['Path*'], 'Empties a file.', p => { for (const f of arr(p.Path).flatMap(expandPaths)) fs.writeFileSync(f, ''); });

  def('Out-File', [], ['FilePath|Path*', 'Append!', 'Encoding=utf8,utf8BOM,unicode,ascii', 'NoNewline!', 'InputObject'], 'Sends formatted output to a file.', (p, input) => {
    const values = p.InputObject !== undefined ? arr(p.InputObject) : input || [];
    let text = textOf(values);
    if (p.NoNewline) text = text.replace(/\n$/, '');
    if (IS_WIN) text = text.replace(/\n/g, '\r\n');
    writeText(resolve(p.FilePath), text, p.Append, toStr(p.Encoding || 'utf8'));
  });

  def('Select-String', ['sls'], ['Pattern*', 'Path*', 'SimpleMatch!', 'CaseSensitive!', 'NotMatch!', 'AllMatches!', 'Quiet!', 'List!', 'Raw!'], 'Finds text in strings and files (like grep).', (p, input) => {
    const pats = arr(p.Pattern).map(toStr);
    const res = pats.map(x => p.SimpleMatch ? new RegExp(x.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), p.CaseSensitive ? 'g' : 'gi') : V.makeRegex(x, p.CaseSensitive, true));
    const out = [];
    const test = (line, file, n) => {
      let matches = [];
      for (const re of res) { re.lastIndex = 0; const m = [...line.matchAll(re)]; if (m.length) matches = matches.concat(m.map(x => x[0])); }
      const hit = matches.length > 0;
      if (hit === !p.NotMatch) {
        if (p.Raw) out.push(line);
        else out.push(obj('MatchInfo', {
          LineNumber: n, Line: line, Path: file || 'InputStream', Filename: file ? path.basename(file) : 'InputStream',
          RelativePath: file ? path.relative(process.cwd(), file) : null, Pattern: res[0], Matches: matches,
          toString() { return this.Path !== 'InputStream' ? `${this.RelativePath}:${this.LineNumber}:${this.Line}` : this.Line; },
        }));
        return true;
      }
      return false;
    };
    if (p.Path !== undefined) {
      for (const f of arr(p.Path).flatMap(x => expandPaths(itemPath(x)))) {
        let lines;
        try { if (fs.statSync(f).isDirectory()) continue; lines = readLines(f); } catch { continue; }
        for (let i = 0; i < lines.length; i++) if (test(lines[i], f, i + 1) && p.List) break;
      }
    } else {
      let n = 0;
      for (const item of input || []) {
        if (item && (item[TYPE] === 'FileInfo')) {
          const lines = readLines(item.FullName);
          for (let i = 0; i < lines.length; i++) if (test(lines[i], item.FullName, i + 1) && p.List) break;
        } else for (const line of toStr(item).split(/\r?\n/)) test(line, null, ++n);
      }
    }
    return p.Quiet ? [out.length > 0] : out;
  });

  def('Get-FileHash', [], ['Path|LiteralPath*', 'Algorithm=SHA256,SHA1,SHA384,SHA512,MD5'], 'Computes a file hash.', (p, input) => {
    const alg = toStr(p.Algorithm || 'SHA256');
    return (p.Path !== undefined ? arr(p.Path) : input || []).map(itemPath).flatMap(expandPaths).map(f => obj('FileHash', {
      Algorithm: alg.toUpperCase(), Hash: crypto.createHash(alg.toLowerCase().replace('sha', 'sha')).update(fs.readFileSync(f)).digest('hex').toUpperCase(), Path: f,
    }));
  });

  // ---------------- archives (via the OS tar, which reads/writes zip on Windows 10+) ----------------
  const tarBin = () => IS_WIN ? path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'tar.exe') : 'tar';
  def('Compress-Archive', [], ['Path|LiteralPath*', 'DestinationPath*', 'Force!', 'Update!'], 'Creates a zip archive.', (p) => {
    const dest = resolve(p.DestinationPath);
    if (fs.existsSync(dest)) { if (!p.Force && !p.Update) throw new PoshError(`The archive file ${dest} already exists. Use -Force to overwrite.`); fs.rmSync(dest); }
    const files = arr(p.Path).flatMap(x => expandPaths(itemPath(x)));
    const r = IS_WIN
      ? spawnSync(tarBin(), ['-a', '-c', '-f', dest, ...files.flatMap(f => ['-C', path.dirname(f), path.basename(f)])], { encoding: 'utf8' })
      : spawnSync('zip', ['-r', '-q', dest, ...files.map(f => path.basename(f))], { cwd: path.dirname(files[0]), encoding: 'utf8' });
    if (r.status !== 0) throw new PoshError(`Compress-Archive failed: ${(r.stderr || r.error?.message || '').trim()}`);
  });
  def('Expand-Archive', [], ['Path|LiteralPath*', 'DestinationPath*', 'Force!'], 'Extracts a zip archive.', (p) => {
    const src = resolve(p.Path);
    const dest = resolve(p.DestinationPath ?? path.join(process.cwd(), path.parse(src).name));
    fs.mkdirSync(dest, { recursive: true });
    const r = IS_WIN ? spawnSync(tarBin(), ['-x', '-f', src, '-C', dest], { encoding: 'utf8' }) : spawnSync('unzip', [p.Force ? '-o' : '-n', '-q', src, '-d', dest], { encoding: 'utf8' });
    if (r.status !== 0) throw new PoshError(`Expand-Archive failed: ${(r.stderr || r.error?.message || '').trim()}`);
  });

  // ---------------- pipeline / objects ----------------
  const CMP_SWITCHES = ['EQ', 'NE', 'GT', 'GE', 'LT', 'LE', 'Like', 'NotLike', 'Match', 'NotMatch', 'Contains', 'NotContains', 'In', 'NotIn', 'CEQ', 'CNE', 'CLike', 'CMatch', 'Is', 'IsNot'];
  def('Where-Object', ['where', '?'], ['Property|FilterScript*', 'Value*', ...CMP_SWITCHES.map(s => s + '!'), 'Not!'], 'Filters pipeline objects: where { $_.Size -gt 1kb }  or  where Size -gt 1kb', async (p, input, ctx) => {
    const out = [];
    const f = p.Property;
    const op = CMP_SWITCHES.find(s => p[s]);
    for (const item of input || []) {
      interp.checkCancel();
      let ok;
      if (f instanceof ScriptBlock) ok = toBool(unwrap(await interp.invokeWithItem(f, item, ctx.scope)));
      else {
        const v = getMember(item, toStr(f));
        if (!op) ok = p.Not ? !toBool(v) : toBool(v);
        else {
          const lop = op.toLowerCase();
          const cs = lop.startsWith('c') && lop !== 'contains';
          ok = V.binaryOp(cs ? lop.slice(1) : lop, v, p.Value, cs);
          if (Array.isArray(ok)) ok = ok.length > 0;
        }
      }
      if (ok) out.push(item);
    }
    return out;
  });

  def('ForEach-Object', ['foreach', '%'], ['Process|MemberName*', 'ArgumentList+', 'Begin', 'End'], 'Runs a script block for each pipeline item ($_), or gets a member: % Name', async (p, input, ctx) => {
    const out = [];
    const push = r => { for (const x of r) if (x !== undefined) out.push(x); };
    if (p.Begin instanceof ScriptBlock) push(await interp.invokeWithItem(p.Begin, null, ctx.scope));
    const f = p.Process;
    for (const item of input || []) {
      interp.checkCancel();
      if (f instanceof ScriptBlock) push(await interp.invokeWithItem(f, item, ctx.scope));
      else if (f !== undefined) {
        const name = toStr(f);
        if (p.ArgumentList !== undefined) push([await V.callMethod(item, name, arr(p.ArgumentList), interp)]);
        else { const v = getMember(item, name); if (Array.isArray(v)) push(v); else push([v]); }
      }
    }
    if (p.End instanceof ScriptBlock) push(await interp.invokeWithItem(p.End, null, ctx.scope));
    return out;
  });

  def('Select-Object', ['select'], ['Property*', 'First', 'Last', 'Skip', 'SkipLast', 'Unique!', 'ExpandProperty', 'ExcludeProperty', 'Index'], 'Selects properties or a subset of objects.', async (p, input, ctx) => {
    let items = input || [];
    if (p.Unique) { const seen = []; items = items.filter(x => { if (seen.some(s => eq(s, x, true) || (isObj(s) && isObj(x) && toStr(s) === toStr(x)))) return false; seen.push(x); return true; }); }
    if (p.Skip !== undefined) items = items.slice(toNum(p.Skip));
    if (p.SkipLast !== undefined) items = items.slice(0, Math.max(0, items.length - toNum(p.SkipLast)));
    if (p.Index !== undefined) items = arr(p.Index).map(i => items[toNum(i)]).filter(x => x !== undefined);
    if (p.First !== undefined || p.Last !== undefined) {
      const first = p.First !== undefined ? items.slice(0, toNum(p.First)) : [];
      const last = p.Last !== undefined ? items.slice(-toNum(p.Last) || items.length) : [];
      items = p.First !== undefined && p.Last !== undefined ? first.concat(last) : p.First !== undefined ? first : last;
    }
    if (p.ExpandProperty !== undefined) {
      const out = [];
      for (const it of items) { const v = getMember(it, toStr(p.ExpandProperty)); if (Array.isArray(v)) out.push(...v); else if (v !== null) out.push(v); }
      return out;
    }
    if (p.Property === undefined && p.ExcludeProperty === undefined) return items;
    const props = arr(p.Property ?? '*');
    const excl = p.ExcludeProperty ? arr(p.ExcludeProperty).map(x => wildcardRe(toStr(x))) : null;
    const out = [];
    for (const it of items) {
      const o = obj(null, {});
      for (const pr of props) {
        if (isHash(pr)) {
          const name = toStr(getMember(pr, 'Name') ?? getMember(pr, 'N') ?? getMember(pr, 'Label') ?? getMember(pr, 'L'));
          o[name] = await propValue(ctx, it, pr);
        } else if (pr instanceof ScriptBlock) o[pr.text.trim()] = await propValue(ctx, it, pr);
        else if (hasWildcard(toStr(pr))) {
          const re = wildcardRe(toStr(pr));
          if (isObj(it)) for (const k of Object.keys(it)) if (re.test(k)) o[k] = it[k];
        } else o[toStr(pr)] = getMember(it, toStr(pr));
      }
      if (excl) for (const k of Object.keys(o)) if (excl.some(r => r.test(k))) delete o[k];
      out.push(o);
    }
    return out;
  });

  def('Sort-Object', ['sort'], ['Property*', 'Descending!', 'Unique!', 'CaseSensitive!', 'Top', 'Bottom'], 'Sorts objects by property values.', async (p, input, ctx) => {
    const items = input || [];
    const props = p.Property !== undefined ? arr(p.Property) : [null];
    const keyed = [];
    for (const it of items) {
      const keys = [];
      for (const pr of props) keys.push(pr === null ? it : await propValue(ctx, it, pr));
      keyed.push({ it, keys });
    }
    const desc = props.map(pr => isHash(pr) && getMember(pr, 'Descending') !== null ? toBool(getMember(pr, 'Descending')) : !!p.Descending);
    keyed.sort((a, b) => {
      for (let i = 0; i < props.length; i++) {
        const c = compare(a.keys[i], b.keys[i], !!p.CaseSensitive);
        if (c) return desc[i] ? -c : c;
      }
      return 0;
    });
    let out = keyed.map(k => k.it);
    if (p.Unique) out = out.filter((x, i) => i === 0 || compare(keyed[i].keys[0], keyed[i - 1].keys[0], !!p.CaseSensitive) !== 0);
    if (p.Top !== undefined) out = out.slice(0, toNum(p.Top));
    if (p.Bottom !== undefined) out = out.slice(-toNum(p.Bottom));
    return out;
  });

  def('Group-Object', ['group'], ['Property*', 'NoElement!', 'AsHashTable!', 'CaseSensitive!'], 'Groups objects that share a property value.', async (p, input, ctx) => {
    const groups = new Map();
    for (const it of input || []) {
      const keys = [];
      for (const pr of arr(p.Property ?? [null])) keys.push(pr === null ? it : await propValue(ctx, it, pr));
      const name = keys.map(toStr).join(', ');
      const k = p.CaseSensitive ? name : name.toLowerCase();
      if (!groups.has(k)) groups.set(k, { name, items: [] });
      groups.get(k).items.push(it);
    }
    if (p.AsHashTable) {
      const h = hashtable();
      for (const g of groups.values()) h[g.name] = g.items.length === 1 ? g.items[0] : g.items;
      return [h];
    }
    return [...groups.values()].sort((a, b) => compare(a.name, b.name)).map(g => obj('GroupInfo', p.NoElement ? { Count: g.items.length, Name: g.name } : { Count: g.items.length, Name: g.name, Group: g.items }));
  });

  def('Measure-Object', ['measure'], ['Property*', 'Sum!', 'Average!', 'Maximum!', 'Minimum!', 'AllStats!', 'Line!', 'Word!', 'Character!'], 'Counts objects and computes sum/average/min/max.', (p, input) => {
    const items = input || [];
    if (p.Line || p.Word || p.Character) {
      const text = items.map(toStr);
      const r = obj('TextMeasureInfo', {});
      if (p.Line) r.Lines = text.reduce((n, t) => n + t.split('\n').length, 0);
      if (p.Word) r.Words = text.reduce((n, t) => n + t.split(/\s+/).filter(Boolean).length, 0);
      if (p.Character) r.Characters = text.reduce((n, t) => n + t.length, 0);
      return [r];
    }
    const props = p.Property !== undefined ? arr(p.Property).map(toStr) : [null];
    return props.map(pr => {
      const vals = items.map(it => pr === null ? it : getMember(it, pr)).filter(v => v !== null && v !== undefined);
      const nums = () => vals.map(v => toNum(v));
      const r = obj('GenericMeasureInfo', { Count: vals.length });
      const all = p.AllStats;
      if (p.Average || all) r.Average = vals.length ? nums().reduce((a, b) => a + b, 0) / vals.length : null;
      if (p.Sum || all) r.Sum = nums().reduce((a, b) => a + b, 0);
      if (p.Maximum || all) r.Maximum = vals.length ? vals.reduce((a, b) => compare(a, b) >= 0 ? a : b) : null;
      if (p.Minimum || all) r.Minimum = vals.length ? vals.reduce((a, b) => compare(a, b) <= 0 ? a : b) : null;
      if (all) { const n = nums(), avg = r.Average; r.StandardDeviation = n.length > 1 ? Math.sqrt(n.reduce((a, b) => a + (b - avg) ** 2, 0) / (n.length - 1)) : 0; }
      r.Property = pr;
      return r;
    });
  });

  def('Get-Member', ['gm'], ['Name*', 'MemberType'], 'Lists the properties and methods of objects.', (p, input) => {
    const seen = new Map();
    for (const it of input || []) {
      const tn = typeOf(it);
      if (seen.has(tn)) continue;
      const members = [];
      if (isObj(it)) for (const k of Object.keys(it)) members.push(obj('MemberDefinition', { TypeName: tn, Name: k, MemberType: it[k] instanceof ScriptBlock ? 'ScriptMethod' : 'NoteProperty', Definition: `${typeOf(it[k])} ${k}=${cell(it[k])}` }));
      const methods = {
        String: ['Contains', 'EndsWith', 'IndexOf', 'Insert', 'LastIndexOf', 'PadLeft', 'PadRight', 'Remove', 'Replace', 'Split', 'StartsWith', 'Substring', 'ToCharArray', 'ToLower', 'ToUpper', 'Trim', 'TrimEnd', 'TrimStart'],
        'Object[]': ['Contains', 'ForEach', 'IndexOf', 'Where'], DateTime: ['AddDays', 'AddHours', 'AddMinutes', 'AddMonths', 'AddSeconds', 'AddYears', 'Subtract', 'ToString'],
        Hashtable: ['Add', 'Clear', 'Clone', 'ContainsKey', 'ContainsValue', 'GetEnumerator', 'Remove'],
      }[tn] || ['ToString'];
      for (const m of methods) members.push(obj('MemberDefinition', { TypeName: tn, Name: m, MemberType: 'Method', Definition: m + '(...)' }));
      if (tn === 'String') members.push(obj('MemberDefinition', { TypeName: tn, Name: 'Length', MemberType: 'Property', Definition: 'int Length {get;}' }));
      seen.set(tn, members);
    }
    const out = [];
    for (const [tn, ms] of seen) {
      const filtered = ms.filter(m => (!p.Name || wildcardRe(toStr(p.Name)).test(m.Name)) && (!p.MemberType || wildcardRe(toStr(p.MemberType) + '*').test(m.MemberType)));
      out.push(`\n   TypeName: ${tn}`, ...filtered);
    }
    return out;
  });

  def('Compare-Object', ['diff', 'compare'], ['ReferenceObject*', 'DifferenceObject*', 'Property', 'IncludeEqual!', 'ExcludeDifferent!', 'PassThru!'], 'Compares two sets of objects.', (p) => {
    const ref = arr(p.ReferenceObject), dif = arr(p.DifferenceObject);
    const key = x => p.Property ? arr(p.Property).map(pr => toStr(getMember(x, toStr(pr)))).join('\u0000') : toStr(x).toLowerCase();
    const rk = ref.map(key), dk = dif.map(key);
    const out = [];
    const mk = (x, ind) => p.PassThru ? x : obj(null, { InputObject: p.Property ? getMember(x, toStr(arr(p.Property)[0])) : x, SideIndicator: ind });
    if (!p.ExcludeDifferent) {
      dif.forEach((x, i) => { if (!rk.includes(dk[i])) out.push(mk(x, '=>')); });
      ref.forEach((x, i) => { if (!dk.includes(rk[i])) out.push(mk(x, '<=')); });
    }
    if (p.IncludeEqual || p.ExcludeDifferent) ref.forEach((x, i) => { if (dk.includes(rk[i])) out.push(mk(x, '==')); });
    return out;
  });

  def('Tee-Object', ['tee'], ['FilePath|Path*', 'Variable', 'Append!'], 'Saves pipeline input to a file or variable and passes it on.', (p, input, ctx) => {
    if (p.FilePath !== undefined) writeText(resolve(p.FilePath), textOf(input || []), p.Append);
    if (p.Variable !== undefined) ctx.scope.set(toStr(p.Variable), unwrap(input || []));
    return input || [];
  });

  def('New-Object', [], ['TypeName*', 'Property'], 'Creates a PSCustomObject (only PSObject/PSCustomObject/Hashtable types are supported).', (p) => {
    const t = toStr(p.TypeName).toLowerCase().replace(/^system\.(management\.automation\.)?/, '');
    if (!['psobject', 'pscustomobject', 'object', 'collections.hashtable'].includes(t)) throw new PoshError(`Cannot find type [${p.TypeName}]: posh only supports PSObject and Hashtable here.`);
    return [t === 'collections.hashtable' ? hashtable({ ...(p.Property || {}) }) : obj(null, { ...(p.Property || {}) })];
  });

  def('Add-Member', [], ['MemberType*', 'Name*', 'Value*', 'NotePropertyName', 'NotePropertyValue', 'InputObject', 'PassThru!', 'Force!'], 'Adds a property to objects.', (p, input) => {
    const items = p.InputObject !== undefined ? arr(p.InputObject) : input || [];
    let name = p.NotePropertyName ?? p.Name, value = p.NotePropertyName !== undefined ? p.NotePropertyValue : p.Value;
    if (p.NotePropertyName === undefined && p.Value === undefined && p.Name !== undefined && !/property|method/i.test(toStr(p.MemberType))) { value = p.Name; name = p.MemberType; }
    for (const it of items) if (isObj(it)) { if (findKey(it, toStr(name)) !== undefined && !p.Force) throw new PoshError(`Cannot add a member with the name "${toStr(name)}" because a member with that name already exists.`); it[toStr(name)] = value; }
    return p.PassThru ? items : [];
  });

  // ---------------- formatting ----------------
  def('Format-Table', ['ft'], ['Property*', 'AutoSize!', 'HideTableHeaders!', 'Wrap!'], 'Formats objects as a table.', async (p, input, ctx) => {
    const items = (input || []).filter(isObj);
    if (!items.length) return (input || []).map(toStr);
    const props = p.Property !== undefined ? arr(p.Property) : Object.keys(items[0]);
    const cols = [];
    for (const pr of props) {
      const label = isHash(pr) ? toStr(getMember(pr, 'Name') ?? getMember(pr, 'Label') ?? getMember(pr, 'N') ?? getMember(pr, 'L')) : pr instanceof ScriptBlock ? pr.text.trim() : toStr(pr);
      const vals = [];
      for (const it of items) vals.push(await propValue(ctx, it, pr));
      cols.push({ label, get: (_, i) => vals[i], align: typeof vals[0] === 'number' ? 'r' : undefined });
    }
    const rows = items.map((_, i) => i);
    const fixed = cols.map(c => ({ ...c, get: i => c.get(null, i) }));
    return renderTable(fixed, rows, interp.host.columns(), interp.host.color, { hideHeaders: p.HideTableHeaders });
  });

  def('Format-List', ['fl'], ['Property*'], 'Formats objects as a list of properties.', async (p, input, ctx) => {
    const items = (input || []).filter(isObj);
    const props = p.Property !== undefined ? arr(p.Property) : null;
    const rows = [];
    for (const it of items) {
      if (!props) { rows.push(it); continue; }
      const o = {};
      for (const pr of props) {
        if (typeof pr === 'string' && hasWildcard(pr)) { const re = wildcardRe(pr); for (const k of Object.keys(it)) if (re.test(k)) o[k] = it[k]; }
        else o[isHash(pr) ? toStr(getMember(pr, 'Name') ?? getMember(pr, 'Label')) : toStr(pr)] = await propValue(ctx, it, pr);
      }
      rows.push(o);
    }
    return renderList(rows, interp.host.color);
  });

  def('Format-Wide', ['fw'], ['Property*', 'Column'], 'Formats objects as a wide list of a single property.', (p, input) => {
    const names = (input || []).map(it => isObj(it) ? toStr(getMember(it, toStr(p.Property ?? (it.Name !== undefined ? 'Name' : Object.keys(it)[0])))) : toStr(it));
    const w = Math.max(...names.map(n => n.length), 1) + 2;
    const cols = p.Column ? toNum(p.Column) : Math.max(1, Math.floor(interp.host.columns() / w));
    const out = [''];
    for (let i = 0; i < names.length; i += cols) out.push(names.slice(i, i + cols).map(n => n.padEnd(w)).join('').trimEnd());
    out.push('');
    return out;
  });

  def('Out-String', ['oss'], ['Width', 'Stream!', 'InputObject'], 'Converts objects to formatted text.', (p, input) => {
    const values = p.InputObject !== undefined ? arr(p.InputObject) : input || [];
    const text = stripAnsi(formatOutput(values, p.Width ? toNum(p.Width) : interp.host.columns(), false));
    return p.Stream ? text.replace(/\n$/, '').split('\n') : [text];
  });

  def('Out-Null', [], ['InputObject'], 'Discards output.', () => []);
  def('Out-Host', ['oh'], ['Paging!', 'InputObject'], 'Writes output to the console.', (p, input) => {
    interp.flushHost();
    interp.host.write(formatOutput(p.InputObject !== undefined ? arr(p.InputObject) : input || [], interp.host.columns(), interp.host.color));
  });
  def('Out-Default', [], ['InputObject'], 'Writes output to the console.', (p, input) => { interp.flushHost(); interp.host.write(formatOutput(input || [], interp.host.columns(), interp.host.color)); });

  // ---------------- output streams ----------------
  def('Write-Output', ['echo', 'write'], ['InputObject+', 'NoEnumerate!'], 'Sends objects down the pipeline.', (p, input) => {
    if (p.InputObject === undefined) return input || [];
    const v = p.InputObject;
    return p.NoEnumerate ? [v.length === 1 ? v[0] : v] : v.flatMap(x => Array.isArray(x) ? x : [x]);
  });

  def('Write-Host', [], ['Object+', 'ForegroundColor|fg=' + COLORS.join(','), 'BackgroundColor|bg=' + COLORS.join(','), 'NoNewline!', 'Separator'], 'Writes text directly to the console, optionally in color.', (p, input) => {
    interp.flushHost();
    const items = p.Object !== undefined ? p.Object : input || [];
    const sep = p.Separator !== undefined ? toStr(p.Separator) : ' ';
    const text = items.map(x => Array.isArray(x) ? x.map(toStr).join(sep) : toStr(x)).join(sep);
    interp.host.write(colorize(text, p.ForegroundColor, p.BackgroundColor) + (p.NoNewline ? '' : '\n'));
  });

  def('Write-Error', [], ['Message*', 'Category', 'TargetObject'], 'Writes a (non-terminating) error.', (p, input, ctx) => {
    ctx.cmdName = null;
    for (const m of p.Message !== undefined ? [p.Message] : input || []) ctx.writeError(toStr(m), { category: toStr(p.Category || 'NotSpecified') });
  });
  def('Write-Warning', [], ['Message*'], 'Writes a yellow warning message.', (p, input) => {
    interp.flushHost();
    for (const m of p.Message !== undefined ? [p.Message] : input || []) interp.host.writeErr(colorize('WARNING: ' + toStr(m), 'Yellow') + '\n');
  });
  const verboseLike = (label, prefVar, color) => (p, input, ctx) => {
    const on = p[label] || /^continue$/i.test(toStr(ctx.scope.get(prefVar)));
    if (!on) return;
    interp.flushHost();
    for (const m of p.Message !== undefined ? [p.Message] : input || []) interp.host.writeErr(colorize(`${label.toUpperCase()}: ${toStr(m)}`, color) + '\n');
  };
  def('Write-Verbose', [], ['Message*', 'Verbose!'], 'Writes a verbose message (shown when $VerbosePreference = "Continue").', verboseLike('Verbose', 'VerbosePreference', 'Yellow'));
  def('Write-Debug', [], ['Message*', 'Debug!'], 'Writes a debug message (shown when $DebugPreference = "Continue").', verboseLike('Debug', 'DebugPreference', 'Yellow'));
  def('Write-Information', [], ['MessageData*'], 'Writes an informational message.', (p) => { interp.flushHost(); interp.host.write(toStr(p.MessageData) + '\n'); });

  def('Write-Progress', [], ['Activity*', 'Status*', 'PercentComplete', 'Completed!', 'Id'], 'Shows a progress bar.', (p) => {
    if (!interp.host.color) return;
    if (p.Completed) { interp.host.write('\x1b]9;4;0;0\x07\r\x1b[2K'); return; }
    const pct = Math.max(0, Math.min(100, p.PercentComplete !== undefined ? toNum(p.PercentComplete) : 0));
    const w = Math.max(10, Math.min(50, interp.host.columns() - 40));
    const fill = Math.round(w * pct / 100);
    interp.host.write(`\x1b]9;4;1;${Math.round(pct)}\x07\r\x1b[2K\x1b[36m${toStr(p.Activity)}\x1b[0m [${'█'.repeat(fill)}${' '.repeat(w - fill)}] ${Math.round(pct)}% ${toStr(p.Status ?? '')}`);
  });

  def('Read-Host', [], ['Prompt*', 'AsSecureString!', 'MaskInput!'], 'Reads a line of input from the console.', async (p) => {
    interp.flushHost();
    const prompt = p.Prompt !== undefined ? toStr(p.Prompt) + ': ' : '';
    const line = await interp.host.readLine(prompt, { mask: !!(p.AsSecureString || p.MaskInput) });
    if (line === null) { const e = new Error('cancelled'); e.cancelled = true; throw e; }
    return [line];
  });

  def('Clear-Host', ['cls', 'clear'], [], 'Clears the screen.', () => { interp.host.write('\x1b[2J\x1b[3J\x1b[H'); });

  // ---------------- commands, help, aliases, variables ----------------
  def('Get-Command', ['gcm'], ['Name*', 'CommandType=Alias,Function,Cmdlet,Application', 'Noun', 'Verb'], 'Lists available commands.', (p, ctx) => {
    const out = [];
    const nameRes = p.Name !== undefined ? arr(p.Name).map(n => wildcardRe(toStr(n))) : null;
    const want = p.CommandType ? toStr(p.CommandType).toLowerCase() : null;
    const ok = n => (!nameRes || nameRes.some(r => r.test(n))) && (!p.Verb || wildcardRe(toStr(p.Verb)).test(n.split('-')[0])) && (!p.Noun || wildcardRe(toStr(p.Noun)).test(n.split('-').slice(1).join('-')));
    if (!want || want === 'cmdlet') for (const c of interp.cmdlets.values()) if (ok(c.name)) out.push(obj('CommandInfo', { CommandType: 'Cmdlet', Name: c.name, Source: 'posh', Definition: c.help }));
    if (!want || want === 'alias') for (const [a, t] of interp.aliases) if (ok(a)) out.push(obj('CommandInfo', { CommandType: 'Alias', Name: `${a} -> ${t}`, Source: '', Definition: t }));
    if (!want || want === 'function') for (let s = interp.global; s; s = null) for (const f of s.funcs.values()) if (ok(f.name)) out.push(obj('CommandInfo', { CommandType: 'Function', Name: f.name, Source: '', Definition: f.text }));
    if ((!want || want === 'application') && nameRes && p.Name !== undefined) {
      for (const n of arr(p.Name).map(toStr)) {
        if (hasWildcard(n)) continue;
        const exe = require('./interp').findExecutable(n);
        if (exe) out.push(obj('CommandInfo', { CommandType: 'Application', Name: path.basename(exe), Source: exe, Definition: exe }));
      }
    }
    if (p.Name !== undefined && !out.length && !arr(p.Name).some(n => hasWildcard(toStr(n)))) {
      throw new PoshError(`The term '${toStr(p.Name)}' is not recognized as a name of a cmdlet, function, script file, or executable program.`, { category: 'ObjectNotFound' });
    }
    return out.sort((a, b) => cmpName(a.Name, b.Name));
  });

  def('Get-Help', ['help', 'man'], ['Name*', 'Full!', 'Examples!'], 'Shows help for a command, or the language overview.', (p) => {
    if (p.Name === undefined || /^about_?(posh)?$/i.test(toStr(p.Name))) return ABOUT.split('\n');
    const name = toStr(p.Name);
    const res = interp.resolveCommand(name, interp.global);
    if (res && res.kind === 'cmdlet') return helpText(interp, res.cmdlet);
    if (res && res.kind === 'function') return ['', 'NAME', `    ${res.fn.name}`, '', 'SYNTAX', `    ${res.fn.name} ${(res.fn.body.params || []).map(x => `[-${x.name}]`).join(' ')}`, '', 'DEFINITION', ...String(res.fn.text).split('\n').map(l => '    ' + l), ''];
    const matches = [...interp.cmdlets.values()].filter(c => wildcardRe('*' + name + '*').test(c.name) || wildcardRe('*' + name + '*').test(c.help));
    if (matches.length) return matches.map(c => obj('CommandInfo', { CommandType: 'Cmdlet', Name: c.name, Source: c.help }));
    if (res && res.kind === 'external') return [`${name} is an external program (${res.path}). Try: ${name} --help`];
    throw new PoshError(`Get-Help could not find ${name} in a help file in this session.`, { category: 'ResourceUnavailable' });
  });

  def('Get-Alias', ['gal'], ['Name*', 'Definition'], 'Lists aliases.', (p) => {
    const re = p.Name !== undefined ? wildcardRe(toStr(p.Name)) : null;
    const dre = p.Definition !== undefined ? wildcardRe(toStr(p.Definition)) : null;
    return [...interp.aliases].filter(([a, t]) => (!re || re.test(a)) && (!dre || dre.test(t))).sort((a, b) => cmpName(a[0], b[0])).map(([a, t]) => obj('AliasInfo', { Name: a, Definition: t }));
  });
  const setAlias = (p) => { interp.aliases.set(toStr(p.Name).toLowerCase(), toStr(p.Value)); };
  def('Set-Alias', ['sal'], ['Name*', 'Value*', 'Force!', 'Scope', 'Option'], 'Creates or changes an alias.', setAlias);
  def('New-Alias', ['nal'], ['Name*', 'Value*', 'Force!', 'Scope', 'Option'], 'Creates an alias.', setAlias);
  def('Remove-Alias', [], ['Name*'], 'Removes an alias.', (p) => { interp.aliases.delete(toStr(p.Name).toLowerCase()); });

  def('Get-Variable', ['gv'], ['Name*', 'ValueOnly!', 'Scope'], 'Lists variables.', (p, input, ctx) => {
    const re = p.Name !== undefined ? arr(p.Name).map(n => wildcardRe(toStr(n))) : null;
    const seen = new Map();
    for (let s = ctx.scope; s; s = s.parent) for (const e of s.vars.values()) if (!seen.has(e.name.toLowerCase())) seen.set(e.name.toLowerCase(), e);
    const vars = [...seen.values()].filter(e => !re || re.some(r => r.test(e.name))).sort((a, b) => cmpName(a.name, b.name));
    if (p.Name !== undefined && !vars.length && !hasWildcard(toStr(p.Name))) throw new PoshError(`Cannot find a variable with the name '${toStr(p.Name)}'.`, { category: 'ObjectNotFound' });
    return p.ValueOnly ? vars.map(e => e.value) : vars.map(e => obj('Variable', { Name: e.name, Value: e.value }));
  });
  def('Set-Variable', ['set', 'sv'], ['Name*', 'Value*', 'Scope', 'Option', 'PassThru!'], 'Sets a variable.', (p, input, ctx) => {
    const v = p.Value !== undefined ? p.Value : input && input.length ? unwrap(input) : null;
    const s = /^global$/i.test(toStr(p.Scope || '')) ? interp.global : ctx.scope;
    s.set(toStr(p.Name), v);
    if (/readonly|constant/i.test(toStr(p.Option || ''))) s.vars.get(toStr(p.Name).toLowerCase()).readOnly = true;
  });
  def('New-Variable', ['nv'], ['Name*', 'Value*', 'Scope', 'Option'], 'Creates a variable.', (p, input, ctx) => interp.cmdlets.get('set-variable').run(p, input, ctx));
  def('Remove-Variable', ['rv'], ['Name*', 'Scope'], 'Deletes a variable.', (p, input, ctx) => {
    for (const n of arr(p.Name)) for (let s = ctx.scope; s; s = s.parent) if (s.vars.delete(toStr(n).toLowerCase())) break;
  });
  def('Clear-Variable', ['clv'], ['Name*'], 'Sets a variable to $null.', (p, input, ctx) => { for (const n of arr(p.Name)) ctx.scope.set(toStr(n), null); });

  def('Get-History', ['history', 'h', 'ghy'], ['Count*', 'Id'], 'Lists commands entered in this session.', (p) => {
    let h = interp.history.map((e, i) => obj('HistoryInfo', { Id: i + 1, CommandLine: e.line, Duration: e.duration !== undefined ? (e.duration / 1000).toFixed(3) + 's' : '', StartExecutionTime: e.start }));
    if (p.Id !== undefined) return h.filter(x => x.Id === toNum(p.Id));
    if (p.Count !== undefined) h = h.slice(-toNum(p.Count));
    return h;
  });
  def('Clear-History', ['clhy'], [], 'Clears the session history.', () => { interp.history.length = 0; });
  def('Invoke-History', ['r', 'ihy'], ['Id*'], 'Re-runs a command from history.', async (p, input, ctx) => {
    const h = interp.history;
    const e = p.Id !== undefined ? h[toNum(p.Id) - 1] : h[h.length - 2];
    if (!e) throw new PoshError('Cannot locate the history entry.');
    interp.host.write(e.line + '\n');
    return interp.cmdlets.get('invoke-expression').run({ Command: e.line }, null, ctx);
  });

  def('Invoke-Expression', ['iex'], ['Command*'], 'Runs a string as posh code.', async (p, input, ctx) => {
    const src = p.Command !== undefined ? toStr(p.Command) : (input || []).map(toStr).join('\n');
    let body;
    try { body = interp.parse(src); } catch (e) { throw new PoshError('ParserError: ' + e.message, { category: 'ParserError' }); }
    const out = [];
    await interp.execBlock(body.end, ctx.scope, out);
    return out;
  });

  def('Invoke-Command', ['icm'], ['ScriptBlock*', 'ArgumentList+', 'NoNewScope!', 'InputObject'], 'Runs a script block locally.', (p, input, ctx) => {
    if (!(p.ScriptBlock instanceof ScriptBlock)) throw new PoshError('Invoke-Command requires a script block.');
    return interp.invokeScriptBlock(p.ScriptBlock, { args: arr(p.ArgumentList).map(value => ({ value })), input: p.InputObject !== undefined ? arr(p.InputObject) : input, scope: ctx.scope, newScope: !p.NoNewScope });
  });

  def('Measure-Command', [], ['Expression*'], 'Times how long a script block takes.', async (p, input, ctx) => {
    const t0 = performance.now();
    await interp.invokeScriptBlock(p.Expression, { scope: ctx.scope, newScope: false, input });
    return [timeSpan(performance.now() - t0)];
  });

  // ---------------- date, time, random ----------------
  def('Get-Date', ['date'], ['Date*', 'Format|f', 'UFormat', 'Year', 'Month', 'Day', 'Hour', 'Minute', 'Second', 'AsUTC!', 'UnixTimeSeconds'], 'Gets the current date/time, optionally formatted: Get-Date -Format "yyyy-MM-dd"', (p) => {
    let d = p.UnixTimeSeconds !== undefined ? new Date(toNum(p.UnixTimeSeconds) * 1000) : p.Date !== undefined ? V.toDate(p.Date) : new Date();
    if (!d) throw new PoshError(`Cannot convert '${toStr(p.Date)}' to a date.`);
    d = new Date(d);
    if (p.Year !== undefined) d.setFullYear(toNum(p.Year));
    if (p.Month !== undefined) d.setMonth(toNum(p.Month) - 1);
    if (p.Day !== undefined) d.setDate(toNum(p.Day));
    if (p.Hour !== undefined) d.setHours(toNum(p.Hour));
    if (p.Minute !== undefined) d.setMinutes(toNum(p.Minute));
    if (p.Second !== undefined) d.setSeconds(toNum(p.Second));
    if (p.AsUTC) d = new Date(d.getTime() + d.getTimezoneOffset() * 6e4);
    if (p.Format !== undefined) return [fmtDate(d, toStr(p.Format))];
    if (p.UFormat !== undefined) {
      const f = toStr(p.UFormat);
      if (f === '%s') return [String(Math.floor(d.getTime() / 1000))];
      const map = { Y: 'yyyy', y: 'yy', m: 'MM', d: 'dd', H: 'HH', I: 'hh', M: 'mm', S: 'ss', p: 'tt', A: 'dddd', a: 'ddd', B: 'MMMM', b: 'MMM', e: 'd', Z: 'zzz', F: 'yyyy-MM-dd', T: 'HH:mm:ss', D: 'MM/dd/yy', R: 'HH:mm' };
      return [f.replace(/%([A-Za-z%])/g, (_, c) => c === '%' ? '%' : map[c] ? fmtDate(d, map[c]) : _)];
    }
    return [d];
  });

  def('Start-Sleep', ['sleep'], ['Seconds|s*', 'Milliseconds|ms', 'Duration'], 'Pauses for a number of seconds.', async (p) => {
    let ms = p.Milliseconds !== undefined ? toNum(p.Milliseconds) : p.Seconds !== undefined ? toNum(p.Seconds) * 1000 : 0;
    const end = Date.now() + ms;
    while (Date.now() < end) {
      interp.checkCancel();
      await new Promise(r => setTimeout(r, Math.min(100, end - Date.now())));
    }
  });

  def('Get-Random', ['random'], ['Maximum|Max*', 'Minimum|Min', 'Count', 'InputObject', 'SetSeed', 'Shuffle!'], 'Gets a random number, or random items from the input.', (p, input) => {
    const items = p.InputObject !== undefined ? arr(p.InputObject) : input && input.length ? input : null;
    if (items) {
      const a = items.slice();
      for (let i = a.length - 1; i > 0; i--) { const j = Math.floor(Math.random() * (i + 1)); [a[i], a[j]] = [a[j], a[i]]; }
      return p.Shuffle ? a : a.slice(0, p.Count !== undefined ? toNum(p.Count) : 1);
    }
    const min = p.Minimum !== undefined ? toNum(p.Minimum) : 0;
    const max = p.Maximum !== undefined ? toNum(p.Maximum) : 2147483647;
    const isInt = Number.isInteger(min) && Number.isInteger(max);
    const one = () => isInt ? min + Math.floor(Math.random() * (max - min)) : min + Math.random() * (max - min);
    return Array.from({ length: p.Count !== undefined ? toNum(p.Count) : 1 }, one);
  });

  def('New-Guid', [], [], 'Generates a new GUID.', () => [obj('Guid', { Guid: crypto.randomUUID(), toString() { return this.Guid; } })]);
  def('New-TimeSpan', [], ['Start*', 'End*', 'Days', 'Hours', 'Minutes', 'Seconds', 'Milliseconds'], 'Creates a time interval.', (p) => {
    if (p.Start !== undefined || p.End !== undefined) return [timeSpan((p.End ? V.toDate(p.End) : new Date()) - (p.Start ? V.toDate(p.Start) : new Date()))];
    const n = k => p[k] !== undefined ? toNum(p[k]) : 0;
    return [timeSpan(n('Days') * 864e5 + n('Hours') * 36e5 + n('Minutes') * 6e4 + n('Seconds') * 1e3 + n('Milliseconds'))];
  });

  // ---------------- processes ----------------
  def('Get-Process', ['gps', 'ps'], ['Name|ProcessName*', 'Id'], 'Lists running processes.', (p, input, ctx) => {
    let procs = listProcesses();
    if (p.Id !== undefined) { const ids = arr(p.Id).map(toNum); procs = procs.filter(x => ids.includes(x.Id)); }
    if (p.Name !== undefined) {
      const res = arr(p.Name).map(n => wildcardRe(toStr(n)));
      procs = procs.filter(x => res.some(r => r.test(x.ProcessName)));
      if (!procs.length && !arr(p.Name).some(n => hasWildcard(toStr(n)))) ctx.writeError(`Cannot find a process with the name "${toStr(p.Name)}". Verify the process name and call the cmdlet again.`, { category: 'ObjectNotFound' });
    }
    return procs.sort((a, b) => cmpName(a.ProcessName, b.ProcessName) || a.Id - b.Id);
  });

  def('Stop-Process', ['kill', 'spps'], ['Id*', 'Name|ProcessName', 'Force!', 'PassThru!'], 'Stops processes by id, name or pipeline input.', (p, input, ctx) => {
    let ids = [];
    if (p.Id !== undefined) ids = arr(p.Id).map(toNum);
    else if (p.Name !== undefined) { const res = arr(p.Name).map(n => wildcardRe(toStr(n))); ids = listProcesses().filter(x => res.some(r => r.test(x.ProcessName))).map(x => x.Id); }
    else ids = (input || []).map(x => typeof x === 'number' ? x : toNum(getMember(x, 'Id')));
    for (const id of ids) {
      try { process.kill(id, p.Force ? 'SIGKILL' : 'SIGTERM'); }
      catch (e) { ctx.writeError(`Cannot stop process with Id ${id}: ${e.code === 'ESRCH' ? 'no such process' : e.message}`, { category: 'InvalidOperation' }); }
    }
  });

  def('Start-Process', ['start', 'saps'], ['FilePath*', 'ArgumentList|Args+', 'WorkingDirectory', 'Wait!', 'NoNewWindow!', 'PassThru!', 'Verb', 'WindowStyle'], 'Starts a program, or opens a file or URL with its default app.', (p) => {
    const file = toStr(p.FilePath);
    const args = arr(p.ArgumentList).flatMap(a => Array.isArray(a) ? a : [a]).map(toStr);
    const cwd = p.WorkingDirectory ? resolve(p.WorkingDirectory) : process.cwd();
    if (p.Verb && /runas/i.test(toStr(p.Verb))) {
      const sudo = require('./interp').findExecutable('sudo');
      if (!sudo) throw new PoshError('Running as administrator needs Windows "sudo" (Settings > System > For developers > Enable sudo).');
      const r = spawnSync(sudo, ['--new-window', file, ...args], { stdio: 'inherit', cwd });
      return [];
    }
    let child;
    if (p.NoNewWindow || !IS_WIN) {
      const exe = require('./interp').findExecutable(file) || file;
      if (!IS_WIN && !require('./interp').findExecutable(file)) child = spawn('xdg-open', [file], { cwd, detached: true, stdio: 'ignore' });
      else child = spawn(exe, args, { cwd, stdio: p.Wait || p.NoNewWindow ? 'inherit' : 'ignore', detached: !p.Wait });
    } else {
      // "start" opens files/URLs with their default app and console programs in a new window
      const line = ['start', '""', '/D', `"${cwd}"`, `"${file}"`, ...args.map(a => /[\s"]/.test(a) ? `"${a.replace(/"/g, '""')}"` : a)];
      if (p.Wait) line.splice(1, 0, '/WAIT');
      child = spawn(process.env.ComSpec || 'cmd.exe', ['/d', '/s', '/c', '"' + line.join(' ') + '"'], { cwd, stdio: 'ignore', windowsVerbatimArguments: true, detached: !p.Wait });
    }
    const info = obj('Process', { Id: child.pid, ProcessName: path.parse(file).name, Name: path.parse(file).name, SessionId: 0, WorkingSet: 0 });
    if (p.Wait) return new Promise(res => child.on('exit', () => res(p.PassThru ? [info] : [])));
    child.unref();
    return p.PassThru ? [info] : [];
  });

  // ---------------- data conversion ----------------
  def('ConvertTo-Json', [], ['InputObject*', 'Depth', 'Compress!', 'AsArray!'], 'Converts objects to JSON.', (p, input) => {
    let v = p.InputObject !== undefined ? p.InputObject : input && input.length === 1 && !p.AsArray ? input[0] : input || [];
    if (p.AsArray && !Array.isArray(v)) v = [v];
    return [JSON.stringify(psToJson(v, p.Depth !== undefined ? toNum(p.Depth) : 2), null, p.Compress ? 0 : 2)];
  });
  def('ConvertFrom-Json', [], ['InputObject*', 'AsHashtable!', 'NoEnumerate!'], 'Parses JSON into objects.', (p, input) => {
    const text = p.InputObject !== undefined ? toStr(p.InputObject) : (input || []).map(toStr).join('\n');
    let v;
    try { v = JSON.parse(text); } catch (e) { throw new PoshError(`Conversion from JSON failed with error: ${e.message}`, { category: 'InvalidArgument' }); }
    v = jsonToPs(v, p.AsHashtable);
    return Array.isArray(v) && !p.NoEnumerate ? v : [v];
  });
  def('ConvertTo-Csv', [], ['InputObject*', 'Delimiter', 'NoTypeInformation!', 'NoHeader!'], 'Converts objects to CSV lines.', (p, input) =>
    toCsv(p.InputObject !== undefined ? arr(p.InputObject) : input || [], p.Delimiter ? toStr(p.Delimiter) : ',', p.NoHeader));
  def('ConvertFrom-Csv', [], ['InputObject*', 'Delimiter', 'Header'], 'Parses CSV text into objects.', (p, input) =>
    parseCsv((p.InputObject !== undefined ? arr(p.InputObject) : input || []).map(toStr).join('\n'), p.Delimiter ? toStr(p.Delimiter) : ',', p.Header));
  def('Import-Csv', ['ipcsv'], ['Path*', 'Delimiter', 'Header'], 'Reads a CSV file into objects.', (p) =>
    parseCsv(fs.readFileSync(resolve(p.Path), 'utf8').replace(/^\ufeff/, ''), p.Delimiter ? toStr(p.Delimiter) : ',', p.Header));
  def('Export-Csv', ['epcsv'], ['Path*', 'Delimiter', 'NoTypeInformation!', 'Append!', 'InputObject'], 'Writes objects to a CSV file.', (p, input) => {
    const items = p.InputObject !== undefined ? arr(p.InputObject) : input || [];
    const f = resolve(p.Path);
    const lines = toCsv(items, p.Delimiter ? toStr(p.Delimiter) : ',', p.Append && fs.existsSync(f));
    writeText(f, lines.join(os.EOL) + os.EOL, p.Append);
  });
  def('ConvertTo-Base64', [], ['InputObject*'], 'Encodes text as Base64 (posh extra).', (p, input) => (p.InputObject !== undefined ? arr(p.InputObject) : input || []).map(x => Buffer.from(toStr(x)).toString('base64')));
  def('ConvertFrom-Base64', [], ['InputObject*'], 'Decodes Base64 text (posh extra).', (p, input) => (p.InputObject !== undefined ? arr(p.InputObject) : input || []).map(x => Buffer.from(toStr(x), 'base64').toString('utf8')));

  // ---------------- web ----------------
  const web = rest => async (p) => {
    let uri = toStr(p.Uri);
    if (!/^[a-z]+:\/\//i.test(uri)) uri = 'http://' + uri;
    const method = toStr(p.Method || (p.Body !== undefined ? 'POST' : 'GET')).toUpperCase();
    const headers = {};
    if (p.Headers) for (const [k, v] of Object.entries(p.Headers)) headers[k] = toStr(v);
    if (p.UserAgent) headers['User-Agent'] = toStr(p.UserAgent);
    else headers['User-Agent'] = headers['User-Agent'] || `posh/1.0 (${os.platform()})`;
    let body;
    if (p.Body !== undefined) {
      if (isObj(p.Body) && method === 'GET') { const u = new URL(uri); for (const [k, v] of Object.entries(p.Body)) u.searchParams.set(k, toStr(v)); uri = u.toString(); }
      else if (isObj(p.Body)) { body = new URLSearchParams(Object.entries(p.Body).map(([k, v]) => [k, toStr(v)])).toString(); headers['Content-Type'] = headers['Content-Type'] || 'application/x-www-form-urlencoded'; }
      else body = toStr(p.Body);
    }
    if (p.ContentType) headers['Content-Type'] = toStr(p.ContentType);
    let res;
    try {
      res = await fetch(uri, { method, headers, body, signal: AbortSignal.timeout(p.TimeoutSec ? toNum(p.TimeoutSec) * 1000 : 100000), redirect: 'follow' });
    } catch (e) { throw new PoshError(`${e.cause?.message || e.message}`, { category: 'ConnectionError' }); }
    const buf = Buffer.from(await res.arrayBuffer());
    if (p.OutFile !== undefined) { fs.writeFileSync(resolve(p.OutFile), buf); if (!p.PassThru) return []; }
    const text = buf.toString('utf8');
    if (!res.ok && !p.SkipHttpErrorCheck) throw new PoshError(`Response status code does not indicate success: ${res.status} (${res.statusText}).${text ? '\n' + text.slice(0, 500) : ''}`, { category: 'InvalidOperation' });
    if (rest) {
      const ct = res.headers.get('content-type') || '';
      if (/json/i.test(ct) || /^\s*[\[{]/.test(text)) { try { const v = jsonToPs(JSON.parse(text)); return Array.isArray(v) ? v : [v]; } catch { } }
      return [text];
    }
    const hdrs = hashtable();
    res.headers.forEach((v, k) => { hdrs[k] = v; });
    return [obj('WebResponse', { StatusCode: res.status, StatusDescription: res.statusText, Content: text, Headers: hdrs, RawContentLength: buf.length })];
  };
  const webParams = ['Uri*', 'Method=GET,POST,PUT,PATCH,DELETE,HEAD,OPTIONS', 'Body', 'Headers', 'ContentType', 'OutFile', 'UserAgent', 'TimeoutSec', 'PassThru!', 'SkipHttpErrorCheck!', 'UseBasicParsing!'];
  def('Invoke-WebRequest', ['iwr', 'curl', 'wget'], webParams, 'Sends an HTTP request and returns the response.', web(false));
  def('Invoke-RestMethod', ['irm'], webParams, 'Sends an HTTP request and parses JSON responses into objects.', web(true));

  // ---------------- clipboard ----------------
  def('Set-Clipboard', ['scb'], ['Value*', 'Append!'], 'Copies text to the clipboard.', (p, input) => {
    const text = (p.Value !== undefined ? arr(p.Value) : input || []).map(toStr).join(os.EOL);
    if (IS_WIN) spawnSync(path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'clip.exe'), { input: Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from(text, 'utf16le')]) });
    else interp.host.write(`\x1b]52;c;${Buffer.from(text).toString('base64')}\x07`);
  });

  // ---------------- system info ----------------
  def('Get-PSDrive', ['gdr'], ['Name*'], 'Lists drives with free/used space.', (p) => {
    const roots = IS_WIN ? 'CDEFGHIJKLMNOPQRSTUVWXYZ'.split('').map(l => l + ':\\').filter(r => fs.existsSync(r)) : ['/'];
    return roots.filter(r => !p.Name || wildcardRe(toStr(p.Name)).test(r[0])).map(r => {
      let used = '', free = '';
      try { const s = fs.statfsSync(r); free = (s.bavail * s.bsize / 1073741824).toFixed(2); used = ((s.blocks - s.bfree) * s.bsize / 1073741824).toFixed(2); } catch { }
      return obj('DriveInfo', { Name: IS_WIN ? r[0] : '/', Used: used, Free: free, Root: r });
    });
  });

  def('Get-Uptime', [], ['Since!'], 'Gets the time since the last boot.', (p) => [p.Since ? new Date(Date.now() - os.uptime() * 1000) : timeSpan(os.uptime() * 1000)]);

  def('Get-ComputerInfo', [], [], 'Gets basic system information.', () => [obj('ComputerInfo', {
    ComputerName: os.hostname(), OS: `${os.type()} ${os.release()}`, Arch: os.arch(), CPU: os.cpus()[0]?.model, Cores: os.cpus().length,
    MemoryGB: +(os.totalmem() / 1073741824).toFixed(1), FreeMemoryGB: +(os.freemem() / 1073741824).toFixed(1), User: os.userInfo().username, Uptime: timeSpan(os.uptime() * 1000),
  })]);

  def('Test-Connection', [], ['TargetName|ComputerName*', 'Count', 'Quiet!'], 'Pings a host using the system ping.', (p) => {
    const host = toStr(p.TargetName);
    const n = String(p.Count !== undefined ? toNum(p.Count) : 4);
    const r = spawnSync('ping', IS_WIN ? ['-n', n, host] : ['-c', n, host], { encoding: 'utf8' });
    if (p.Quiet) return [r.status === 0];
    return (r.stdout || '').split(/\r?\n/).filter(Boolean);
  });

  // ---------------- control ----------------
  def('Exit-Posh', ['quit'], ['Code*'], 'Exits the shell.', (p) => { const { Flow } = require('./interp'); throw new Flow('exit', p.Code !== undefined ? toNum(p.Code) : 0); });

  // common Unix-style shortcuts, mapped to cmdlets
  alias('ii', 'Start-Process');
  alias('rvpa', 'Resolve-Path');
  alias('nv', 'New-Variable');
}

module.exports = { register, makeItem, parseCsv, toCsv };
