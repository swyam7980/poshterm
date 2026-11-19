'use strict';
// posh interpreter: tree-walking evaluator over the parser's AST.
// Pipelines are materialized: each stage receives the previous stage's full output.
// ponytail: materialized pipelines, so infinite producers (e.g. Get-Content -Wait) can't stream; add generators if needed.
const fs = require('fs');
const path = require('path');
const os = require('os');
const { spawnSync } = require('child_process');
const { parse, ParseError } = require('./parser');
const V = require('./values');
const { formatOutput } = require('./format');
const {
  PoshError, ErrorRecord, ScriptBlock, TypeRef, toBool, toNum, toStr, binaryOp, getMember, setMember,
  index, setIndex, callMethod, cast, range, hashtable, isHash, stripAnsi,
} = V;

const IS_WIN = process.platform === 'win32';

class Flow { constructor(kind, value) { this.kind = kind; this.value = value; } }

class Scope {
  constructor(parent) { this.parent = parent; this.vars = new Map(); this.funcs = new Map(); }
  find(name) {
    const k = name.toLowerCase();
    for (let s = this; s; s = s.parent) if (s.vars.has(k)) return s.vars.get(k);
    return undefined;
  }
  get(name) { const e = this.find(name); return e ? e.value : undefined; }
  set(name, value) {
    const k = name.toLowerCase();
    const e = this.vars.get(k);
    if (e) { if (e.readOnly) throw new PoshError(`Cannot overwrite variable ${name} because it is read-only or constant.`); e.value = value; }
    else this.vars.set(k, { name, value });
  }
  getFunc(name) {
    const k = name.toLowerCase();
    for (let s = this; s; s = s.parent) if (s.funcs.has(k)) return s.funcs.get(k);
    return null;
  }
  root() { let s = this; while (s.parent) s = s.parent; return s; }
}

const unwrap = out => out.length === 0 ? null : out.length === 1 ? out[0] : out;
function pushOut(out, v) {
  if (v === undefined) return;
  if (Array.isArray(v)) { for (const x of v) if (x !== undefined) out.push(x); }
  else out.push(v);
}

// --- external command lookup ---
const pathExts = () => IS_WIN ? (process.env.PATHEXT || '.COM;.EXE;.BAT;.CMD').toLowerCase().split(';').filter(Boolean) : [''];
const exeCache = new Map();
function isFile(p) { try { return fs.statSync(p).isFile(); } catch { return false; } }
function findExecutable(name) {
  const exts = IS_WIN ? [...pathExts(), '.posh'] : ['.posh'];
  const hasExt = !IS_WIN || [...pathExts(), '.posh', '.ps1'].includes(path.extname(name).toLowerCase());
  const probe = base => {
    if (hasExt && isFile(base)) return base;
    for (const e of exts) if (isFile(base + e)) return base + e;
    return null;
  };
  if (/[\\/]/.test(name) || name.startsWith('~')) {
    const p = name.startsWith('~') ? os.homedir() + name.slice(1) : name;
    return probe(path.resolve(p));
  }
  const key = (process.env.PATH || '') + '|' + name;
  if (exeCache.has(key)) return exeCache.get(key);
  let found = null;
  for (const dir of (process.env.PATH || '').split(path.delimiter)) {
    if (dir && (found = probe(path.join(dir.replace(/^"|"$/g, ''), name)))) break;
  }
  exeCache.set(key, found);
  return found;
}

let exeNamesCache = null;
function listExecutables() {
  const key = process.env.PATH;
  if (exeNamesCache && exeNamesCache.key === key) return exeNamesCache.names;
  const exts = pathExts();
  const names = new Set();
  for (const dir of (key || '').split(path.delimiter)) {
    try {
      for (const f of fs.readdirSync(dir)) {
        const ext = path.extname(f).toLowerCase();
        if (IS_WIN ? exts.includes(ext) : true) names.add(f);
      }
    } catch { }
  }
  exeNamesCache = { key, names: [...names] };
  return exeNamesCache.names;
}

function quoteCmdArg(a) {
  if (a === '' ) return '""';
  if (!/[\s"&|<>^()%!]/.test(a)) return a;
  return '"' + a.replace(/"/g, '""') + '"';
}

class Interp {
  constructor(host) {
    this.host = host;
    this.global = new Scope(null);
    this.aliases = new Map();
    this.cmdlets = new Map();
    this.history = [];
    this.cancel = false;
    this.locStack = [];
    this.prevDir = null;
    this.topOut = null;
    this.exitCode = 0;
    const g = this.global;
    const ro = (n, v) => g.vars.set(n.toLowerCase(), { name: n, value: v, readOnly: true });
    ro('true', true); ro('false', false); ro('null', null);
    ro('IsWindows', IS_WIN); ro('IsLinux', process.platform === 'linux'); ro('IsMacOS', process.platform === 'darwin');
    ro('PID', process.pid); ro('HOME', os.homedir());
    g.set('?', true); g.set('LASTEXITCODE', 0); g.set('Error', []); g.set('OFS', ' ');
    g.set('ErrorActionPreference', 'Continue'); g.set('VerbosePreference', 'SilentlyContinue');
    g.set('PoshVersion', '1.0.0');
    g.set('PSVersionTable', V.obj('Hashtable', { PoshVersion: '1.0.0', Edition: 'posh', Platform: process.platform, OS: `${os.type()} ${os.release()}`, Runtime: `node ${process.version}` }));
    g.set('PROFILE', path.join(os.homedir(), '.posh', 'profile.posh'));
    require('./cmdlets').register(this);
  }

  // ---------- public entry points ----------
  parse(src) { return parse(src); }

  isIncomplete(src) {
    try { parse(src); return false; } catch (e) { return e instanceof ParseError && e.incomplete; }
  }

  // Runs source at top level, writing formatted output to the host as each statement completes.
  async runTop(src, { scope = this.global, file = null, args = null } = {}) {
    let body;
    try { body = parse(src); }
    catch (e) {
      if (e instanceof ParseError) { this.writeErrorRecord(new ErrorRecord(`ParserError: ${file ? file + ': ' : ''}${e.message}`, { category: 'ParserError' })); this.global.set('?', false); return; }
      throw e;
    }
    if (file) { scope.set('PSScriptRoot', path.dirname(file)); scope.set('PSCommandPath', file); }
    if (args || body.params) {
      // bind the script's param() block / $args from command-line strings
      const argList = (args || []).map(a => /^-[A-Za-z_]\w*:?$/.test(a) ? { param: a.slice(1).replace(/:$/, ''), raw: a } : { value: V.parseNumber(a) ?? a });
      try { await this.invokeScriptBlock(new ScriptBlock({ params: body.params, begin: null, process: null, end: [] }, ''), { args: argList, scope, newScope: false, name: file && path.basename(file) }); }
      catch (e) { if (e instanceof PoshError) { this.writeErrorRecord(e.record); this.global.set('?', false); return; } throw e; }
    }
    for (const fn of body.end || []) {
      if (this.cancel) break;
      const out = [];
      const prevTop = this.topOut;
      this.topOut = out;
      try {
        await this.execStatement(fn, scope, out, true);
      } catch (e) {
        if (e instanceof Flow) {
          if (e.kind === 'exit') { this.flushHost(); this.topOut = prevTop; throw e; }
          // break/continue/return at top level just end the statement
        } else if (e instanceof PoshError) {
          this.flushHost();
          this.recordError(e.record);
          this.writeErrorRecord(e.record);
          this.global.set('?', false);
          if (e.fatal) { this.topOut = prevTop; break; }
        } else if (e && e.cancelled) {
          this.flushHost();
          this.topOut = prevTop;
          return;
        } else throw e;
      }
      this.flushHost();
      this.topOut = prevTop;
    }
  }

  flushHost() {
    const out = this.topOut;
    if (out && out.length) {
      const text = formatOutput(out, this.host.columns(), this.host.color);
      out.length = 0;
      this.host.write(text);
    }
  }

  recordError(rec) {
    const errs = this.global.get('Error');
    if (Array.isArray(errs)) { errs.unshift(rec); if (errs.length > 256) errs.pop(); }
  }

  writeErrorRecord(rec) {
    const cmd = rec.command ? rec.command + ': ' : '';
    const msg = cmd + rec.Exception.Message;
    this.host.writeErr(this.host.color ? `\x1b[31;1m${msg}\x1b[0m\n` : msg + '\n');
  }

  checkCancel() {
    if (this.cancel) { const e = new Error('cancelled'); e.cancelled = true; throw e; }
  }

  // ---------- statements ----------
  async execBlock(stmts, scope, out, toHost = false) {
    if (!stmts) return;
    for (const s of stmts) await this.execStatement(s, scope, out, toHost);
  }

  async execStatement(node, scope, out, toHost = false) {
    this.checkCancel();
    switch (node.type) {
      case 'Assign': await this.assign(node, scope); return;
      case 'Pipeline': case 'Chain': {
        const r = await this.runChain(node, scope, toHost);
        pushOut(out, r);
        return;
      }
      case 'If': {
        for (const [cond, body] of node.clauses) {
          if (toBool(await this.evalStatementValue(cond, scope))) { await this.execBlock(body, scope, out, toHost); return; }
        }
        if (node.elseBody) await this.execBlock(node.elseBody, scope, out, toHost);
        return;
      }
      case 'Foreach': {
        let coll = await this.evalStatementValue(node.coll, scope);
        if (coll === null || coll === undefined) return;
        if (!Array.isArray(coll)) coll = isHash(coll) ? [coll] : [coll];
        for (const item of coll) {
          this.checkCancel();
          scope.set(node.varName, item);
          if (await this.loopBody(node.body, scope, out, toHost)) break;
        }
        return;
      }
      case 'For': {
        if (node.init) await this.execStatement(node.init, scope, [], false);
        while (!node.cond || toBool(await this.evalStatementValue(node.cond, scope))) {
          this.checkCancel();
          if (await this.loopBody(node.body, scope, out, toHost)) break;
          if (node.iter) await this.execStatement(node.iter, scope, [], false);
        }
        return;
      }
      case 'While': {
        while (toBool(await this.evalStatementValue(node.cond, scope))) {
          this.checkCancel();
          if (await this.loopBody(node.body, scope, out, toHost)) break;
        }
        return;
      }
      case 'DoLoop': {
        do {
          this.checkCancel();
          if (await this.loopBody(node.body, scope, out, toHost)) break;
        } while (toBool(await this.evalStatementValue(node.cond, scope)) !== node.until);
        return;
      }
      case 'Switch': return this.execSwitch(node, scope, out, toHost);
      case 'FunctionDef': {
        scope.funcs.set(node.name.toLowerCase(), { name: node.name, body: node.body, text: node.text });
        return;
      }
      case 'Try': return this.execTry(node, scope, out, toHost);
      case 'Throw': {
        const v = node.value ? await this.evalStatementValue(node.value, scope) : null;
        if (v instanceof ErrorRecord) { const e = new PoshError(v.Exception.Message, { record: v }); e.fatal = true; throw e; }
        const msg = v === null ? 'ScriptHalted' : toStr(v);
        const e = new PoshError(msg, { category: 'OperationStopped', value: v });
        e.fatal = true;
        throw e;
      }
      case 'Return': {
        if (node.value) pushOut(out, await this.evalStatementValue(node.value, scope));
        throw new Flow('return');
      }
      case 'Break': throw new Flow('break');
      case 'Continue': throw new Flow('continue');
      case 'Exit': {
        const v = node.value ? await this.evalStatementValue(node.value, scope) : 0;
        throw new Flow('exit', v === null ? 0 : toNum(v));
      }
      case 'Param': case 'Named': return;
      default: pushOut(out, await this.eval(node, scope));
    }
  }

  // returns true when the loop should stop
  async loopBody(body, scope, out, toHost) {
    try { await this.execBlock(body, scope, out, toHost); }
    catch (e) {
      if (e instanceof Flow && e.kind === 'break') return true;
      if (e instanceof Flow && e.kind === 'continue') return false;
      throw e;
    }
    return false;
  }

  async execSwitch(node, scope, out, toHost) {
    let subject = await this.evalStatementValue(node.subject, scope);
    const items = Array.isArray(subject) ? subject : [subject];
    const cs = !!node.flags.casesensitive;
    const saved = scope.vars.get('_');
    try {
      for (const item of items) {
        this.checkCancel();
        scope.set('_', item);
        let matched = false;
        for (const cl of node.clauses) {
          let ok;
          if (cl.pattern.type === 'ScriptBlock') {
            const r = [];
            await this.execBlock(cl.pattern.body.end, scope, r);
            ok = toBool(unwrap(r));
          } else {
            const p = await this.eval(cl.pattern, scope);
            if (node.flags.regex) ok = binaryOp('match', toStr(item), toStr(p), cs, m => scope.set('Matches', m));
            else if (node.flags.wildcard) ok = V.wildcardRe(toStr(p), cs).test(toStr(item));
            else ok = V.eq(item, p, cs);
          }
          if (ok) {
            matched = true;
            try { await this.execBlock(cl.body, scope, out, toHost); }
            catch (e) {
              if (e instanceof Flow && e.kind === 'break') return;
              if (e instanceof Flow && e.kind === 'continue') break;
              throw e;
            }
          }
        }
        if (!matched && node.defaultBody) {
          try { await this.execBlock(node.defaultBody, scope, out, toHost); }
          catch (e) { if (e instanceof Flow && e.kind === 'break') return; if (!(e instanceof Flow && e.kind === 'continue')) throw e; }
        }
      }
    } finally {
      if (saved) scope.vars.set('_', saved); else scope.vars.delete('_');
    }
  }

  async execTry(node, scope, out, toHost) {
    try {
      await this.execBlock(node.body, scope, out, toHost);
    } catch (e) {
      if (!(e instanceof PoshError) || !node.catches.length) throw e;
      const rec = e.record;
      this.recordError(rec);
      const saved = scope.vars.get('_');
      scope.set('_', rec);
      scope.set('PSItem', rec);
      try { await this.execBlock(node.catches[0].body, scope, out, toHost); }
      finally { if (saved) scope.vars.set('_', saved); else scope.vars.delete('_'); }
    } finally {
      if (node.fin) await this.execBlock(node.fin, scope, out, toHost);
    }
  }

  // Value of a statement used as an expression (assignment RHS, conditions, ...).
  async evalStatementValue(node, scope) {
    if (node.type === 'Pipeline' && node.elems.length === 1 && node.elems[0].type !== 'Command' && node.elems[0].type !== 'Redirected') {
      return this.eval(node.elems[0], scope);
    }
    if (node.type === 'Assign') { await this.assign(node, scope); return this.eval(node.target.type === 'Cast' ? node.target.expr : node.target, scope); }
    const out = [];
    await this.execStatement(node, scope, out);
    return unwrap(out);
  }

  async assign(node, scope) {
    let target = node.target, typeName = null;
    if (target.type === 'Cast') { typeName = target.typeName; target = target.expr; }
    let value = await this.evalStatementValue(node.value, scope);
    if (node.op !== '=') {
      const old = await this.eval(target, scope);
      value = binaryOp(node.op[0], old, value, false);
    }
    if (typeName) value = cast(typeName, value);
    switch (target.type) {
      case 'Var': {
        const s = target.scope === 'global' || target.scope === 'script' ? this.global : scope;
        if (target.name === '_' || target.name.toLowerCase() === 'null') { if (target.name.toLowerCase() === 'null') return; }
        s.set(target.name, value);
        return;
      }
      case 'EnvVar':
        if (value === null || value === undefined || value === '') delete process.env[target.name];
        else process.env[target.name] = toStr(value);
        return;
      case 'Member': {
        const o = await this.eval(target.obj, scope);
        const name = toStr(await this.eval(target.name, scope));
        setMember(o, name, value);
        return;
      }
      case 'Index': {
        const o = await this.eval(target.obj, scope);
        setIndex(o, await this.eval(target.idx, scope), value);
        return;
      }
    }
    throw new PoshError('The assignment expression is not valid.');
  }

  // ---------- pipelines ----------
  async runChain(node, scope, toHost) {
    if (node.type === 'Chain') {
      const left = await this.runChain(node.left, scope, toHost);
      const ok = this.global.get('?');
      if (node.op === '&&' ? ok : !ok) {
        if (toHost && this.topOut) { pushOut(this.topOut, left); this.flushHost(); return this.runChain(node.right, scope, toHost); }
        return [].concat(left, await this.runChain(node.right, scope, toHost));
      }
      return left;
    }
    return this.runPipeline(node, scope, toHost);
  }

  async runPipeline(node, scope, toHost) {
    let input = null;
    const n = node.elems.length;
    let ok = true;
    for (let i = 0; i < n; i++) {
      this.checkCancel();
      const el = node.elems[i];
      const last = i === n - 1;
      if (el.type === 'Command') {
        const r = await this.invokeCommand(el, scope, input, { toHost: toHost && last });
        input = r.out;
        if (!r.ok) ok = false;
      } else if (i > 0) {
        throw new PoshError('Expressions are only allowed as the first element of a pipeline.', { category: 'ParserError' });
      } else if (el.type === 'Redirected') {
        const v = await this.eval(el.expr, scope);
        input = await this.applyRedirects(el.redirs, [].concat(v === undefined ? [] : v), scope);
      } else if (el.type === 'PostUpdate' || el.type === 'PreUpdate') {
        await this.eval(el, scope);
        input = [];
      } else {
        const v = await this.eval(el, scope);
        input = v === undefined || v === null ? [] : Array.isArray(v) ? v : [v];
      }
    }
    this.global.set('?', ok);
    return input || [];
  }

  async applyRedirects(redirs, out, scope) {
    for (const r of redirs) {
      if (r.merge) continue;
      if (r.stream === '1' || r.stream === '*') {
        const target = await this.eval(r.target, scope);
        if (target !== null && target !== undefined && toStr(target) !== '$null') {
          const text = stripAnsi(formatOutput(out, 4096, false));
          const file = this.resolvePath(toStr(target));
          if (r.append) fs.appendFileSync(file, text); else fs.writeFileSync(file, text);
        }
        out = [];
      }
    }
    return out;
  }

  resolvePath(p) {
    if (p === '~' || p.startsWith('~/') || p.startsWith('~\\')) p = os.homedir() + p.slice(1);
    return path.resolve(process.cwd(), p);
  }

  // ---------- commands ----------
  resolveCommand(name, scope) {
    let n = name;
    for (let i = 0; i < 10 && this.aliases.has(n.toLowerCase()); i++) n = this.aliases.get(n.toLowerCase());
    const f = scope.getFunc(n);
    if (f) return { kind: 'function', name: f.name, fn: f };
    const c = this.cmdlets.get(n.toLowerCase());
    if (c) return { kind: 'cmdlet', name: c.name, cmdlet: c };
    const p = findExecutable(n);
    if (p) return { kind: p.toLowerCase().endsWith('.posh') ? 'script' : 'external', name: n, path: p };
    return null;
  }

  async evalArgs(el, scope) {
    const args = [];
    for (const a of el.args) {
      if (a.type === 'Param') {
        const arg = { param: a.name, raw: a.raw };
        if (a.value) { arg.value = await this.eval(a.value, scope); arg.hasValue = true; }
        args.push(arg);
      } else if (a.type === 'Splat') {
        const v = scope.get(a.name);
        if (isHash(v) || (v && typeof v === 'object' && !Array.isArray(v))) {
          for (const [k, val] of Object.entries(v)) args.push({ param: k, raw: '-' + k, value: val, hasValue: true });
        } else if (Array.isArray(v)) for (const x of v) args.push({ value: x });
      } else {
        args.push({ value: await this.eval(a.value, scope), bare: a.value.type === 'Str' && a.value.bare, raw: a.value.raw });
      }
    }
    return args;
  }

  async invokeCommand(el, scope, input, { toHost = false } = {}) {
    let nameVal = el.name.type === 'Str' ? el.name.value : await this.eval(el.name, scope);
    const args = await this.evalArgs(el, scope);
    const errors = [];
    const errRedirect = el.redirs.find(r => r.stream === '2' || r.stream === '*');
    const mergeErr = el.redirs.some(r => r.merge && r.stream === '2');
    let errFile = null;
    if (errRedirect && !errRedirect.merge) {
      const t = await this.eval(errRedirect.target, scope);
      errFile = t === null || t === undefined ? false : { path: this.resolvePath(toStr(t)), append: errRedirect.append };
    }
    const stdoutRedirected = el.redirs.some(r => !r.merge && (r.stream === '1' || r.stream === '*'));
    const ctx = {
      interp: this, scope, cmdName: null, toHost: toHost && !stdoutRedirected,
      writeError: (msg, opts = {}) => {
        const rec = msg instanceof ErrorRecord ? msg : new ErrorRecord(toStr(msg), { command: ctx.cmdName, ...opts });
        if (!rec.command) rec.command = ctx.cmdName;
        this.recordError(rec);
        errors.push(rec);
        if (mergeErr) return;
        if (errFile === false) return;
        if (errFile) { (errFile.append ? fs.appendFileSync : fs.writeFileSync)(errFile.path, rec.Exception.Message + os.EOL); errFile.append = true; return; }
        if (toBool(ctx.silentErrors)) return;
        this.flushHost();
        this.writeErrorRecord(rec);
      },
      flush: () => this.flushHost(),
    };
    let out;
    let ok = true;
    try {
      if (nameVal instanceof ScriptBlock) {
        ctx.cmdName = null;
        out = await this.invokeScriptBlock(nameVal, { args, input, scope, newScope: !el.dot });
      } else {
        const name = toStr(nameVal);
        const res = this.resolveCommand(name, scope);
        if (!res) {
          throw new PoshError(`The term '${name}' is not recognized as a name of a cmdlet, function, script file, or executable program.\nCheck the spelling of the name, or if a path was included, verify that the path is correct and try again.`, { category: 'ObjectNotFound', command: name });
        }
        ctx.cmdName = res.name;
        if (res.kind === 'cmdlet') {
          const eaArg = args.findIndex(a => a.param && /^(ErrorAction|ea)$/i.test(a.param));
          if (eaArg >= 0) {
            const ea = toStr(args[eaArg].hasValue ? args[eaArg].value : args[eaArg + 1]?.value);
            args.splice(eaArg, args[eaArg].hasValue ? 1 : 2);
            if (/^(SilentlyContinue|Ignore)$/i.test(ea)) ctx.silentErrors = true;
            if (/^Stop$/i.test(ea)) ctx.stopOnError = true;
          }
          const bound = bindArgs(res.cmdlet.spec, args, res.name, false);
          const r = await res.cmdlet.run(bound.bound, input, ctx);
          out = r === undefined ? [] : Array.isArray(r) ? r : [r];
        } else if (res.kind === 'function') {
          out = await this.invokeFunction(res.fn, args, input, scope, el.dot);
        } else if (res.kind === 'script') {
          out = await this.invokeScriptFile(res.path, args, input, scope, el.dot);
        } else {
          const r = this.runExternal(res.path, args, input, { toHost: ctx.toHost, errFile, mergeErr });
          out = r.out;
          ok = r.ok;
        }
      }
    } catch (e) {
      if (e instanceof PoshError && !e.record.command && ctx.cmdName) e.record.command = ctx.cmdName;
      throw e;
    }
    if (ctx.stopOnError && errors.length) throw new PoshError(errors[0].Exception.Message, { record: errors[0] });
    if (errors.length) ok = false;
    if (mergeErr) out = out.concat(errors);
    out = await this.applyRedirects(el.redirs, out, scope);
    return { out, ok };
  }

  runExternal(file, args, input, { toHost, errFile, mergeErr }) {
    const argv = [];
    for (const a of args) {
      if (a.param !== undefined) argv.push(a.hasValue ? a.raw + toStr(a.value) : a.raw);
      else if (Array.isArray(a.value)) for (const x of a.value) argv.push(toStr(x));
      else argv.push(a.raw && typeof a.value === 'number' ? a.raw : toStr(a.value));
    }
    let cmd = file, cmdArgs = argv, verbatim = false;
    const ext = path.extname(file).toLowerCase();
    if (IS_WIN && (ext === '.cmd' || ext === '.bat')) {
      cmd = process.env.ComSpec || 'cmd.exe';
      cmdArgs = ['/d', '/s', '/c', '"' + [quoteCmdArg(file), ...argv.map(quoteCmdArg)].join(' ') + '"'];
      verbatim = true;
    } else if (ext === '.ps1') {
      const pw = findExecutable('pwsh') || findExecutable('powershell');
      if (!pw) throw new PoshError(`Cannot run ${file}: no PowerShell found to run .ps1 scripts.`);
      cmd = pw; cmdArgs = ['-NoProfile', '-File', file, ...argv];
    }
    const stdinText = input && input.length ? stripAnsi(formatOutput(input, 4096, false)) : null;
    const capture = !toHost;
    if (toHost) this.flushHost();
    const errMode = mergeErr || errFile !== null ? 'pipe' : 'inherit';
    const r = spawnSync(cmd, cmdArgs, {
      stdio: [stdinText !== null ? 'pipe' : 'inherit', capture ? 'pipe' : 'inherit', errMode],
      input: stdinText !== null ? stdinText : undefined,
      windowsVerbatimArguments: verbatim,
      maxBuffer: 1 << 30,
      encoding: 'utf8',
      cwd: process.cwd(),
    });
    if (r.error) {
      if (r.error.code === 'ENOENT') throw new PoshError(`The term '${file}' is not recognized as a name of a cmdlet, function, script file, or executable program.`, { category: 'ObjectNotFound' });
      throw new PoshError(`Program '${path.basename(file)}' failed to run: ${r.error.message}`);
    }
    const code = r.status === null ? (r.signal ? 128 + 2 : 1) : r.status;
    this.global.set('LASTEXITCODE', code);
    const out = [];
    const lines = s => { if (!s) return []; const l = s.split(/\r?\n/); if (l[l.length - 1] === '') l.pop(); return l; };
    if (capture) out.push(...lines(r.stdout));
    if (errMode === 'pipe' && r.stderr) {
      if (mergeErr) out.push(...lines(r.stderr));
      else if (errFile) (errFile.append ? fs.appendFileSync : fs.writeFileSync)(errFile.path, r.stderr);
    }
    return { out, ok: code === 0 };
  }

  async invokeFunction(fn, args, input, scope, dot) {
    const sb = new ScriptBlock(fn.body, fn.text || '');
    return this.invokeScriptBlock(sb, { args, input, scope, newScope: !dot, name: fn.name });
  }

  async invokeScriptFile(file, args, input, scope, dot) {
    const src = fs.readFileSync(file, 'utf8');
    let body;
    try { body = parse(src); }
    catch (e) { if (e instanceof ParseError) throw new PoshError(`${file}: ParserError: ${e.message}`, { category: 'ParserError' }); throw e; }
    const sb = new ScriptBlock(body, src);
    const s = dot ? scope : new Scope(scope);
    s.set('PSScriptRoot', path.dirname(file));
    s.set('PSCommandPath', file);
    return this.invokeScriptBlock(sb, { args, input, scope: s, newScope: false, name: path.basename(file) });
  }

  // Runs a script block. newScope=false runs it in the caller's scope (ForEach-Object, dot-sourcing).
  async invokeScriptBlock(sb, { args = [], input = null, scope = this.global, newScope = true, dollarUnder, name = null } = {}) {
    const s = newScope ? new Scope(scope) : scope;
    const body = sb.body;
    const params = body.params || [];
    const spec = params.map(p => ({ name: p.name, aliases: p.aliases || [], switch: !!p.type && /^switch$/i.test(p.type), positional: !(p.type && /^switch$/i.test(p.type)) }));
    const { bound, extra } = bindArgs(spec, args, name || 'scriptblock', true);
    const pipeParam = params.find(p => p.fromPipeline || p.fromPipelineByName);
    for (const p of params) {
      let v;
      if (p.name in bound) v = bound[p.name];
      else if (p.default) v = await this.eval(p.default, s);
      else if (p.type && /^switch$/i.test(p.type)) v = false;
      else if (p.mandatory && !(p === pipeParam && input)) {
        v = await this.host.readLine(`${p.name}: `, {});
        if (v === null) throw new PoshError(`Cannot process command because of one or more missing mandatory parameters: ${p.name}.`);
      } else v = null;
      if (p.validateSet && v !== null && !p.validateSet.some(x => V.eq(x, v))) {
        throw new PoshError(`Cannot validate argument on parameter '${p.name}'. The argument "${toStr(v)}" does not belong to the set "${p.validateSet.join(',')}".`, { category: 'InvalidData' });
      }
      if (p.type && v !== null && !/^switch$/i.test(p.type)) v = cast(p.type, v);
      s.set(p.name, v);
    }
    s.set('args', extra);
    s.set('input', input || []);
    const savedUnder = newScope ? null : s.vars.get('_');
    if (dollarUnder !== undefined) s.set('_', dollarUnder);
    const out = [];
    try {
      if (body.begin) await this.execBlock(body.begin, s, out);
      if (body.process) {
        const items = input || [null];
        for (const item of items) {
          this.checkCancel();
          s.set('_', item);
          s.set('PSItem', item);
          if (pipeParam && input) {
            let v = item;
            if (pipeParam.fromPipelineByName && !pipeParam.fromPipeline) v = getMember(item, pipeParam.name);
            s.set(pipeParam.name, pipeParam.type && !/^switch$/i.test(pipeParam.type) ? cast(pipeParam.type, v) : v);
          }
          try { await this.execBlock(body.process, s, out); }
          catch (e) { if (!(e instanceof Flow && e.kind === 'return')) throw e; }
        }
      }
      if (body.end) await this.execBlock(body.end, s, out);
    } catch (e) {
      if (!(e instanceof Flow && e.kind === 'return')) throw e;
    } finally {
      if (!newScope) { if (savedUnder) s.vars.set('_', savedUnder); else if (dollarUnder !== undefined) s.vars.delete('_'); }
    }
    return out;
  }

  // Invoke a script block once per item with $_ set, in the caller's scope (ForEach-Object semantics).
  async invokeWithItem(sb, item, scope, extra = {}) {
    return this.invokeScriptBlock(sb, { scope, newScope: false, dollarUnder: item, ...extra });
  }

  async arrayWhere(arr, sb) {
    const out = [];
    for (const x of arr) if (toBool(unwrap(await this.invokeWithItem(sb, x, this.currentScope || this.global)))) out.push(x);
    return out;
  }
  async arrayForEach(arr, f, args) {
    const out = [];
    for (const x of arr) {
      if (f instanceof ScriptBlock) pushOut(out, await this.invokeWithItem(f, x, this.currentScope || this.global));
      else if (args.length) pushOut(out, await callMethod(x, toStr(f), args, this));
      else pushOut(out, getMember(x, toStr(f)));
    }
    return out;
  }

  // ---------- expressions ----------
  async eval(node, scope) {
    this.currentScope = scope;
    switch (node.type) {
      case 'Num': return node.value;
      case 'Str': return node.value;
      case 'ExpStr': {
        let s = '';
        const ofs = toStr(scope.get('OFS') ?? ' ');
        for (const p of node.parts) {
          if (typeof p === 'string') { s += p; continue; }
          const v = await this.eval(p, scope);
          s += Array.isArray(v) ? v.map(toStr).join(ofs) : toStr(v);
        }
        return s;
      }
      case 'Var': return this.getVar(node, scope);
      case 'EnvVar': return process.env[node.name] ?? null;
      case 'ArrayLit': { const a = []; for (const i of node.items) a.push(await this.eval(i, scope)); return a; }
      case 'ArrayExpr': { const out = []; await this.execBlock(node.stmts, scope, out); return out; }
      case 'SubExpr': { const out = []; await this.execBlock(node.stmts, scope, out); return unwrap(out); }
      case 'Paren': return this.evalStatementValue(node.inner, scope);
      case 'Hash': {
        const h = hashtable();
        for (const [k, v] of node.entries) {
          const key = toStr(await this.eval(k, scope));
          h[key] = await this.evalStatementValue(v, scope);
        }
        return h;
      }
      case 'ScriptBlock': return new ScriptBlock(node.body, node.text);
      case 'Logical': {
        const l = toBool(await this.eval(node.left, scope));
        if (node.op === 'and') return l ? toBool(await this.eval(node.right, scope)) : false;
        if (node.op === 'or') return l ? true : toBool(await this.eval(node.right, scope));
        return l !== toBool(await this.eval(node.right, scope));
      }
      case 'Binary': {
        const l = await this.eval(node.left, scope);
        const r = node.right.type === 'TypeLit' ? node.right.name : await this.eval(node.right, scope);
        return binaryOp(node.op, l, r, node.cs, m => scope.set('Matches', m));
      }
      case 'Not': return !toBool(await this.eval(node.expr, scope));
      case 'Neg': { const v = await this.eval(node.expr, scope); return -toNum(v); }
      case 'Plus': return toNum(await this.eval(node.expr, scope));
      case 'UnaryOp': {
        const v = await this.eval(node.expr, scope);
        if (node.op === 'join') return (Array.isArray(v) ? v : [v]).map(toStr).join('');
        return (Array.isArray(v) ? v : [v]).flatMap(x => toStr(x).trim().split(/\s+/).filter(Boolean));
      }
      case 'Range': return range(await this.eval(node.left, scope), await this.eval(node.right, scope));
      case 'Cast': return cast(node.typeName, await this.eval(node.expr, scope));
      case 'TypeLit': return new TypeRef(node.name);
      case 'Member': {
        const o = await this.eval(node.obj, scope);
        const name = toStr(await this.eval(node.name, scope));
        if (node.isStatic && o instanceof TypeRef) return V.staticMember(o.Name, name);
        return getMember(o, name);
      }
      case 'Invoke': {
        const o = await this.eval(node.obj, scope);
        const name = toStr(await this.eval(node.name, scope));
        const args = [];
        for (const a of node.args) args.push(await this.eval(a, scope));
        return await callMethod(o, name, args, this);
      }
      case 'Index': {
        const o = await this.eval(node.obj, scope);
        return index(o, await this.eval(node.idx, scope));
      }
      case 'PostUpdate': case 'PreUpdate': {
        const old = await this.eval(node.target, scope);
        const nv = toNum(old) + (node.op === '+' ? 1 : -1);
        await this.assign({ target: node.target, op: '=', value: { type: 'Pipeline', elems: [{ type: 'Num', value: nv }] } }, scope);
        return node.type === 'PostUpdate' ? old : nv;
      }
      case 'Pipeline': case 'Chain': case 'Assign': return this.evalStatementValue(node, scope);
      case 'Redirected': return this.eval(node.expr, scope);
      case 'Command': {
        const r = await this.invokeCommand(node, scope, null);
        return unwrap(r.out);
      }
    }
    throw new PoshError(`Cannot evaluate ${node.type}`);
  }

  getVar(node, scope) {
    const n = node.name.toLowerCase();
    if (n === 'pwd') return V.obj('PathInfo', { Path: process.cwd(), toString() { return this.Path; } });
    const s = node.scope === 'global' || node.scope === 'script' ? this.global : scope;
    const v = s.get(node.name);
    return v === undefined ? null : v;
  }

  // ---------- helpers for the editor / cmdlets ----------
  async getPrompt() {
    const f = this.global.getFunc('prompt');
    if (f) {
      try {
        const out = await this.invokeFunction(f, [], null, this.global, false);
        return out.map(toStr).join('');
      } catch { /* fall back */ }
    }
    const cwd = process.cwd();
    return this.host.color ? `\x1b[36mposh\x1b[0m \x1b[1m${cwd}\x1b[0m> ` : `posh ${cwd}> `;
  }

  commandNames() {
    const names = new Set([...this.cmdlets.values()].map(c => c.name));
    for (const [, v] of this.aliases) names.add(v);
    for (const k of this.aliases.keys()) names.add(k);
    for (let s = this.global; s; s = null) for (const f of s.funcs.values()) names.add(f.name);
    return [...names];
  }

  isCommand(name) {
    if (/^\.?[\\/]/.test(name) || /^\.\.?$/.test(name)) return true;
    try { return !!this.resolveCommand(name, this.global); } catch { return false; }
  }

  paramNames(cmd) {
    const r = this.resolveCommand(cmd, this.global);
    if (!r) return [];
    if (r.kind === 'cmdlet') return r.cmdlet.spec.map(p => p.name).concat(['ErrorAction']);
    if (r.kind === 'function') return (r.fn.body.params || []).map(p => p.name);
    return [];
  }

  paramValues(cmd, param) {
    const r = this.resolveCommand(cmd, this.global);
    if (!r || r.kind !== 'cmdlet') return [];
    const p = r.cmdlet.spec.find(x => x.name.toLowerCase().startsWith(param.toLowerCase()));
    return p && p.values ? p.values : [];
  }

  variableNames(scope = this.currentScope || this.global) {
    const names = new Set();
    for (let s = scope; s; s = s.parent) for (const e of s.vars.values()) names.add(e.name);
    return [...names];
  }
}

// Binds -Named and positional args to a parameter spec.
function bindArgs(spec, args, cmdName, allowExtra) {
  const bound = {};
  const positional = [];
  for (let i = 0; i < args.length; i++) {
    const a = args[i];
    if (a.param === undefined) { positional.push(a.value); continue; }
    const key = a.param.toLowerCase();
    let m = spec.filter(p => p.name.toLowerCase() === key || p.aliases.some(x => x.toLowerCase() === key));
    if (!m.length) m = spec.filter(p => p.name.toLowerCase().startsWith(key));
    if (m.length > 1) throw new PoshError(`Parameter cannot be processed because the parameter name '${a.param}' is ambiguous. Possible matches include: ${m.map(x => '-' + x.name).join(' ')}.`, { category: 'InvalidArgument', command: cmdName });
    if (!m.length) {
      if (allowExtra && /^(Verbose|Debug|ErrorAction|ea|WhatIf|Confirm)$/i.test(a.param)) { if (!a.hasValue && /^(ErrorAction|ea)$/i.test(a.param)) i++; continue; }
      if (allowExtra) { positional.push(a.hasValue ? a.raw + toStr(a.value) : a.raw); continue; }
      throw new PoshError(`A parameter cannot be found that matches parameter name '${a.param}'.`, { category: 'InvalidArgument', command: cmdName });
    }
    const p = m[0];
    if (p.switch) bound[p.name] = a.hasValue ? toBool(a.value) : true;
    else if (a.hasValue) bound[p.name] = a.value;
    else {
      const nx = args[i + 1];
      if (!nx || nx.param !== undefined) throw new PoshError(`Missing an argument for parameter '${p.name}'. Specify a parameter of type '${p.type || 'Object'}' and try again.`, { category: 'InvalidArgument', command: cmdName });
      bound[p.name] = nx.value;
      i++;
    }
  }
  const posParams = spec.filter(p => p.positional && !(p.name in bound));
  const extra = [];
  for (let i = 0; i < positional.length; i++) {
    const p = posParams.shift();
    if (!p) { extra.push(positional[i]); continue; }
    if (p.rest) { bound[p.name] = positional.slice(i); break; }
    bound[p.name] = positional[i];
  }
  if (extra.length && !allowExtra) {
    throw new PoshError(`A positional parameter cannot be found that accepts argument '${toStr(extra[0])}'.`, { category: 'InvalidArgument', command: cmdName });
  }
  return { bound, extra };
}

module.exports = { Interp, Scope, Flow, bindArgs, findExecutable, listExecutables, unwrap, pushOut };
