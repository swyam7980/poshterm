'use strict';
// posh parser: recursive descent straight over the source text. PowerShell-style
// syntax tokenizes differently in "command mode" (barewords, -Params) and
// "expression mode" (operators), so there is no separate lexer pass.

class ParseError extends Error {
  constructor(msg, src, pos, incomplete) {
    const pre = src.slice(0, pos).split('\n');
    super(`${msg} (line ${pre.length}, col ${pre[pre.length - 1].length + 1})`);
    this.incomplete = incomplete;
    this.pos = pos;
  }
}

const STMT_KEYWORDS = {
  if: '(', while: '(', for: '(', foreach: '(', param: '(', switch: '(-',
  do: '{', try: '{', begin: '{', process: '{', end: '{',
  function: 'name', filter: 'name',
  throw: '', return: '', break: '', continue: '', exit: '',
};
const CMP_RE = /-([ci]?)(eq|ne|gt|ge|lt|le|notlike|like|notmatch|match|replace|notcontains|contains|notin|in|split|join|isnot|is|as|band|bor|bxor)(?![\w-])/iy;
const LOGIC_RE = /-(and|or|xor)(?![\w-])/iy;
const NUM_RE = /(0x[0-9a-f]+|(?:\d+(?:\.\d+)?|\.\d+)(?:e[+-]?\d+)?)(kb|mb|gb|tb|pb)?(?![\w])/iy;
const VAR_RE = /\$(?:\{([^}]*)\}|(?:(env|global|script|local|private|using):)?([A-Za-z_][\w]*|\?|\$|\^))/iy;
const ARG_END = new Set([undefined, '\n', ';', ')', '}', '|']);
const BARE_STOP = /[\s|;&(){},<>]/;
const ESCAPES = { n: '\n', t: '\t', r: '\r', 0: '\0', a: '\x07', b: '\b', e: '\x1b', f: '\f', v: '\v' };

class Parser {
  constructor(src) {
    this.src = src.replace(/\r\n?/g, '\n');
    this.pos = 0;
    this.nl = [false]; // stack: are newlines plain whitespace in the current context?
  }

  get ch() { return this.src[this.pos]; }
  peek(n = 1) { return this.src[this.pos + n]; }
  eof() { return this.pos >= this.src.length; }
  err(msg, incomplete = this.eof()) { throw new ParseError(msg, this.src, this.pos, incomplete); }
  match(re) { re.lastIndex = this.pos; const m = re.exec(this.src); if (m) this.pos += m[0].length; return m; }
  test(re) { re.lastIndex = this.pos; return re.test(this.src); }
  startsWith(s) { return this.src.startsWith(s, this.pos); }

  ws(allowNl = this.nl[this.nl.length - 1]) {
    for (;;) {
      const c = this.ch;
      if (c === ' ' || c === '\t' || c === '\f' || c === ' ' || c === '﻿') this.pos++;
      else if (c === '`' && this.peek() === '\n') this.pos += 2;
      else if (c === '#') { while (!this.eof() && this.ch !== '\n') this.pos++; }
      else if (c === '<' && this.peek() === '#') {
        const e = this.src.indexOf('#>', this.pos + 2);
        if (e < 0) { this.pos = this.src.length; this.err('Missing #> at end of block comment', true); }
        this.pos = e + 2;
      }
      else if (c === '\n' && allowNl) this.pos++;
      else return;
    }
  }

  expectChar(c) {
    this.ws(true);
    if (this.ch !== c) this.err(`Missing '${c}'`);
    this.pos++;
  }

  wordAt(word) {
    if (this.src.substr(this.pos, word.length).toLowerCase() !== word) return false;
    const n = this.src[this.pos + word.length];
    return n === undefined || !/[\w\-]/.test(n);
  }

  // ---------- statements ----------
  parseScript() {
    const stmts = this.statementList(null);
    return makeBody(stmts);
  }

  statementList(term) {
    this.nl.push(false);
    const stmts = [];
    for (;;) {
      this.ws(true);
      if (this.ch === ';') { this.pos++; continue; }
      if (this.eof()) { if (term) this.err(`Missing closing '${term}'`, true); break; }
      if (this.ch === term) break;
      if (this.ch === '}' || this.ch === ')') this.err(`Unexpected token '${this.ch}' in expression or statement.`, false);
      const st = this.statement();
      stmts.push(st);
      const endCh = this.src[this.pos - 1];
      this.ws(false);
      // keyword statements ending in } or ) may be followed directly by another statement
      const blockEnd = (endCh === '}' || endCh === ')') && !['Pipeline', 'Chain', 'Assign', 'Throw', 'Return', 'Exit'].includes(st.type);
      if (!blockEnd && !this.eof() && this.ch !== '\n' && this.ch !== ';' && this.ch !== term) {
        this.err(`Unexpected token '${this.src.slice(this.pos, this.pos + 20).split('\n')[0]}' in expression or statement.`, false);
      }
    }
    this.nl.pop();
    return stmts;
  }

  keyword() {
    const m = /[a-z]+/iy; m.lastIndex = this.pos;
    const r = m.exec(this.src);
    if (!r) return null;
    const w = r[0].toLowerCase();
    const want = STMT_KEYWORDS[w];
    if (want === undefined) return null;
    const after = this.src[this.pos + w.length];
    if (after !== undefined && /[\w\-.:\\\/]/.test(after)) return null;
    if (want === '') return w;
    const save = this.pos;
    this.pos += w.length; this.ws(true);
    const c = this.ch;
    this.pos = save;
    if (want === 'name') return c && /[\w-]/.test(c) ? w : null;
    return c && want.includes(c) ? w : null;
  }

  statement() {
    const start = this.pos;
    const kw = this.keyword();
    if (kw) {
      this.pos += kw.length;
      const node = this['kw_' + kw](kw);
      node.pos = start;
      return node;
    }
    return this.assignmentOrChain();
  }

  block() {
    this.ws(true);
    if (this.ch !== '{') this.err("Missing '{' in statement block.");
    this.pos++;
    const stmts = this.statementList('}');
    this.pos++;
    return stmts;
  }

  parenCond() {
    this.expectChar('(');
    this.nl.push(true);
    this.ws();
    const s = this.statement();
    this.ws();
    this.nl.pop();
    this.expectChar(')');
    return s;
  }

  kw_if() {
    const clauses = [[this.parenCond(), this.block()]];
    let elseBody = null;
    for (;;) {
      const save = this.pos;
      this.ws(true);
      if (this.wordAt('elseif')) { this.pos += 6; clauses.push([this.parenCond(), this.block()]); }
      else if (this.wordAt('else')) { this.pos += 4; elseBody = this.block(); break; }
      else { this.pos = save; break; }
    }
    return { type: 'If', clauses, elseBody };
  }

  kw_while() { return { type: 'While', cond: this.parenCond(), body: this.block() }; }

  kw_do() {
    const body = this.block();
    this.ws(true);
    let until;
    if (this.wordAt('while')) { this.pos += 5; until = false; }
    else if (this.wordAt('until')) { this.pos += 5; until = true; }
    else this.err("Missing 'while' or 'until' in do loop.");
    return { type: 'DoLoop', body, cond: this.parenCond(), until };
  }

  kw_for() {
    this.expectChar('(');
    this.nl.push(true);
    const part = (end) => {
      this.ws();
      if (this.ch === end) { this.pos++; return null; }
      const s = this.statement();
      this.ws();
      if (this.ch !== end) this.err(`Missing '${end}' in for loop.`);
      this.pos++;
      return s;
    };
    const init = part(';'), cond = part(';'), iter = part(')');
    this.nl.pop();
    return { type: 'For', init, cond, iter, body: this.block() };
  }

  kw_foreach() {
    this.expectChar('(');
    this.nl.push(true);
    this.ws();
    const v = this.match(VAR_RE);
    if (!v) this.err('Missing variable name after foreach.');
    this.ws();
    if (!this.wordAt('in')) this.err("Missing 'in' after variable in foreach loop.");
    this.pos += 2;
    this.ws();
    const coll = this.statement();
    this.ws();
    this.nl.pop();
    this.expectChar(')');
    return { type: 'Foreach', varName: v[3] || v[1], coll, body: this.block() };
  }

  kw_switch() {
    const flags = {};
    for (;;) {
      this.ws();
      const m = this.match(/-(regex|wildcard|exact|casesensitive|file)\b/iy);
      if (!m) break;
      flags[m[1].toLowerCase()] = true;
    }
    const subject = this.parenCond();
    this.ws(true);
    if (this.ch !== '{') this.err("Missing '{' in switch statement.");
    this.pos++;
    this.nl.push(false);
    const clauses = [];
    let defaultBody = null;
    for (;;) {
      this.ws(true);
      if (this.ch === ';') { this.pos++; continue; }
      if (this.eof()) this.err("Missing '}' in switch statement.", true);
      if (this.ch === '}') { this.pos++; break; }
      if (this.wordAt('default')) { this.pos += 7; defaultBody = this.block(); continue; }
      let pattern;
      if (this.ch === '{') pattern = this.scriptBlockLiteral();
      else if ('$"\'(@[0123456789'.includes(this.ch) || (this.ch === '-' && /\d/.test(this.peek()))) pattern = this.expression(true);
      else pattern = this.bareArg();
      clauses.push({ pattern, body: this.block() });
    }
    this.nl.pop();
    return { type: 'Switch', flags, subject, clauses, defaultBody };
  }

  kw_function(kw) {
    this.ws();
    const m = this.match(/[^\s(){}]+/y);
    if (!m) this.err('Missing function name.');
    this.ws(true);
    let params = null;
    if (this.ch === '(') { this.pos++; params = this.paramList(); }
    const start = this.pos;
    const stmts = this.block();
    const body = makeBody(stmts);
    if (params) body.params = params;
    if (kw === 'filter' && !body.process) { body.process = body.end; body.end = null; }
    return { type: 'FunctionDef', name: m[0], body, text: this.src.slice(start, this.pos) };
  }
  kw_filter(kw) { return this.kw_function(kw); }

  kw_param() {
    this.expectChar('(');
    return { type: 'Param', params: this.paramList() };
  }

  paramList() {
    this.nl.push(true);
    const params = [];
    for (;;) {
      this.ws();
      if (this.ch === ')') { this.pos++; break; }
      if (this.eof()) this.err("Missing ')' in function parameter list.", true);
      const p = { name: null, type: null, default: null, mandatory: false, fromPipeline: false, aliases: [], validateSet: null };
      while (this.ch === '[') {
        const t = this.bracketText();
        const am = /^(\w+)\s*\((.*)\)$/s.exec(t);
        if (am) {
          const a = am[1].toLowerCase(), body = am[2];
          if (a === 'parameter') {
            if (/Mandatory\s*(=\s*\$true)?(?![\w])/i.test(body) && !/Mandatory\s*=\s*\$false/i.test(body)) p.mandatory = true;
            if (/ValueFromPipeline(?!ByPropertyName)/i.test(body)) p.fromPipeline = true;
            if (/ValueFromPipelineByPropertyName/i.test(body)) p.fromPipelineByName = true;
          } else if (a === 'alias') p.aliases = [...body.matchAll(/['"]([^'"]+)['"]/g)].map(x => x[1]);
          else if (a === 'validateset') p.validateSet = [...body.matchAll(/['"]([^'"]+)['"]/g)].map(x => x[1]);
        } else if (!/^(cmdletbinding|outputtype)/i.test(t)) p.type = t;
        this.ws();
      }
      const v = this.match(VAR_RE);
      if (!v) this.err('Missing parameter variable name.');
      p.name = v[3] || v[1];
      this.ws();
      if (this.ch === '=') { this.pos++; this.ws(); p.default = this.expression(true); this.ws(); }
      params.push(p);
      if (this.ch === ',') this.pos++;
      else if (this.ch !== ')') this.err("Missing ')' in function parameter list.");
    }
    this.nl.pop();
    return params;
  }

  bracketText() {
    // text between balanced [ ], respecting quotes and nested parens
    const start = ++this.pos;
    let depth = 1;
    while (!this.eof()) {
      const c = this.ch;
      if (c === '"' || c === "'") { const q = c; this.pos++; while (!this.eof() && this.ch !== q) this.pos++; }
      else if (c === '[') depth++;
      else if (c === ']' && --depth === 0) { this.pos++; return this.src.slice(start, this.pos - 1).trim(); }
      this.pos++;
    }
    this.err("Missing ']'", true);
  }

  kw_begin(kw) { return { type: 'Named', name: kw, body: this.block() }; }
  kw_process(kw) { return this.kw_begin(kw); }
  kw_end(kw) { return this.kw_begin(kw); }

  kw_try() {
    const body = this.block();
    const catches = [];
    let fin = null;
    for (;;) {
      const save = this.pos;
      this.ws(true);
      if (this.wordAt('catch')) {
        this.pos += 5;
        const types = [];
        for (;;) {
          this.ws();
          if (this.ch === '[') types.push(this.bracketText());
          else if (this.ch === ',') this.pos++;
          else break;
        }
        catches.push({ types, body: this.block() });
      } else if (this.wordAt('finally')) { this.pos += 7; fin = this.block(); break; }
      else { this.pos = save; break; }
    }
    if (!catches.length && !fin) this.err('The Try statement is missing its Catch or Finally block.');
    return { type: 'Try', body, catches, fin };
  }

  optionalValue() {
    this.ws(false);
    if (ARG_END.has(this.ch) || this.startsWith('&&') || this.startsWith('||')) return null;
    return this.assignmentOrChain();
  }
  kw_throw() { return { type: 'Throw', value: this.optionalValue() }; }
  kw_return() { return { type: 'Return', value: this.optionalValue() }; }
  kw_exit() { return { type: 'Exit', value: this.optionalValue() }; }
  kw_break() { this.ws(false); this.match(/[A-Za-z_]\w*/y); return { type: 'Break' }; }
  kw_continue() { this.ws(false); this.match(/[A-Za-z_]\w*/y); return { type: 'Continue' }; }

  assignmentOrChain() {
    const start = this.pos;
    if (this.ch === '$' || this.ch === '[') {
      try {
        let target = this.unary();
        this.ws(false);
        const m = this.match(/(=|\+=|-=|\*=|\/=|%=)(?!=)/y);
        if (m && ['Var', 'EnvVar', 'Member', 'Index', 'Cast'].includes(target.type)) {
          this.ws(true);
          return { type: 'Assign', target, op: m[1], value: this.statement(), pos: start };
        }
      } catch (e) { if (!(e instanceof ParseError)) throw e; }
      this.pos = start;
    }
    return this.chain();
  }

  chain() {
    let left = this.pipeline();
    for (;;) {
      this.ws(false);
      const op = this.startsWith('&&') ? '&&' : this.startsWith('||') ? '||' : null;
      if (!op) return left;
      this.pos += 2;
      this.ws(true);
      left = { type: 'Chain', op, left, right: this.pipeline() };
    }
  }

  pipeline() {
    const start = this.pos;
    const elems = [this.element()];
    for (;;) {
      this.ws(false);
      if (this.ch === '|' && this.peek() !== '|') {
        this.pos++;
        this.ws(true);
        if (this.eof()) this.err('An empty pipe element is not allowed.', true);
        elems.push(this.element());
      } else break;
    }
    return { type: 'Pipeline', elems, pos: start, text: this.src.slice(start, this.pos) };
  }

  element() {
    this.ws(false);
    const c = this.ch, start = this.pos;
    if (c === '&' && this.peek() !== '&') {
      this.pos++; this.ws(false);
      return this.command(this.cmdArgValue(), false, start);
    }
    if (c === '.' && (this.peek() === ' ' || this.peek() === '\t')) {
      this.pos++; this.ws(false);
      return this.command(this.cmdArgValue(), true, start);
    }
    if (this.isCommandStart()) return this.command(null, false, start);
    const expr = this.expression();
    const redirs = [];
    for (;;) { this.ws(false); const r = this.redirection(); if (!r) break; redirs.push(r); }
    return redirs.length ? { type: 'Redirected', expr, redirs } : expr;
  }

  isCommandStart() {
    const c = this.ch;
    if (c === undefined) return false;
    if (/[A-Za-z_\\\/~?%]/.test(c) || c > '\x7f') return true;
    if (c === '.') return this.peek() === '\\' || this.peek() === '/' || this.peek() === '.' || /[A-Za-z_]/.test(this.peek() || '');
    if (/\d/.test(c)) {
      NUM_RE.lastIndex = this.pos;
      const m = NUM_RE.exec(this.src);
      if (!m) return true;
      const after = this.src[this.pos + m[0].length];
      return after !== undefined && !/[\s;|)}\],+\-*\/%=.!<>]/.test(after);
    }
    return false;
  }

  command(nameNode, dot, start) {
    const name = nameNode || { type: 'Str', value: this.bareword() };
    const args = [], redirs = [];
    for (;;) {
      this.ws(false);
      const c = this.ch;
      if (ARG_END.has(c) || this.startsWith('&&') || this.startsWith('||')) break;
      if (c === '&') this.err("The ampersand (&) character is not allowed. Wrap it in quotes (\"&\") to pass it as part of a string.", false);
      const r = this.redirection();
      if (r) { redirs.push(r); continue; }
      if (c === '-' && /[A-Za-z_?]/.test(this.peek() || '')) {
        const m = this.match(/-([A-Za-z_?][\w?\-]*)(:?)/y);
        const node = { type: 'Param', name: m[1], raw: m[0] };
        if (m[2]) {
          if (ARG_END.has(this.ch) || /\s/.test(this.ch)) node.raw = m[0];
          else { node.value = this.cmdArg(); node.raw = m[0]; }
        }
        args.push(node);
        continue;
      }
      if (c === '-' && this.peek() === '-' && /[\s]/.test(this.src[this.pos + 2] || ' ')) {
        this.pos += 2; args.push({ type: 'Arg', value: { type: 'Str', value: '--' }, raw: '--' }); continue;
      }
      if (c === '@' && /[A-Za-z_]/.test(this.peek() || '')) {
        const m = this.match(/@(\w+)/y);
        args.push({ type: 'Splat', name: m[1] });
        continue;
      }
      args.push({ type: 'Arg', value: this.cmdArg() });
    }
    return { type: 'Command', name, args, redirs, dot, pos: start, text: this.src.slice(start, this.pos) };
  }

  redirection() {
    const m = this.match(/([*1-6])?(>>?)(&[12])?/y);
    if (!m) return null;
    const stream = m[1] || '1';
    if (m[3]) return { stream, merge: true };
    this.ws(false);
    if (ARG_END.has(this.ch)) this.err('Missing file specification after redirection operator.');
    return { stream, append: m[2] === '>>', target: this.cmdArgValue() };
  }

  cmdArg() {
    const v = this.cmdArgValue();
    if (this.ch !== ',') return v;
    const items = [v];
    while (this.ch === ',') { this.pos++; this.ws(true); items.push(this.cmdArgValue()); }
    return { type: 'ArrayLit', items };
  }

  cmdArgValue() {
    const c = this.ch;
    if (c === '$' || c === '"' || c === "'" || c === '(' || c === '{' || (c === '@' && '({"\''.includes(this.peek()))) {
      const v = this.postfix();
      if (this.ch && !BARE_STOP.test(this.ch) && (c === '"' || c === "'")) {
        // "abc"def  ->  single concatenated argument
        const rest = this.bareArg();
        return { type: 'ExpStr', parts: [v, rest] };
      }
      return v;
    }
    return this.bareArg();
  }

  bareArg() {
    const start = this.pos;
    const raw = this.bareword();
    if (raw.includes('$') && /\$[\w{(]/.test(raw)) {
      return { type: 'ExpStr', parts: new Parser(raw).expandParts(null), bare: true };
    }
    const n = /^[+-]?(0x[0-9a-f]+|(\d+(\.\d+)?|\.\d+)(e[+-]?\d+)?)(kb|mb|gb|tb|pb)?$/i.test(raw) ? require('./values').parseNumber(raw) : null;
    if (n !== null) return { type: 'Num', value: n, raw };
    return { type: 'Str', value: unescapeBare(raw), bare: true, pos: start };
  }

  bareword() {
    const start = this.pos;
    while (!this.eof()) {
      const c = this.ch;
      if (c === '`' && this.pos + 1 < this.src.length) { this.pos += 2; continue; }
      if (c === '"' || c === "'") {
        if (this.pos === start) break;
        // embedded quotes: --name="a b"
        const q = c; this.pos++;
        while (!this.eof() && this.ch !== q) this.pos++;
        if (this.eof()) this.err('The string is missing the terminator: ' + q, true);
        this.pos++;
        continue;
      }
      if (c === '$' && this.peek() === '(') { // $( ... ) inside a bareword
        let depth = 0;
        do { if (this.ch === '(') depth++; else if (this.ch === ')') depth--; this.pos++; } while (!this.eof() && depth > 0);
        continue;
      }
      if (BARE_STOP.test(c)) break;
      this.pos++;
    }
    if (this.pos === start) this.err(`Unexpected token '${this.ch}'.`);
    return this.src.slice(start, this.pos);
  }

  // ---------- expressions ----------
  expression(noComma = false) { return this.logical(noComma); }

  logical(nc) {
    let left = this.comparison(nc);
    for (;;) {
      const save = this.pos;
      this.ws();
      const m = this.match(LOGIC_RE);
      if (!m) { this.pos = save; return left; }
      this.ws(true);
      left = { type: 'Logical', op: m[1].toLowerCase(), left, right: this.comparison(nc) };
    }
  }

  comparison(nc) {
    let left = this.additive(nc);
    for (;;) {
      const save = this.pos;
      this.ws();
      const m = this.match(CMP_RE);
      if (!m) { this.pos = save; return left; }
      this.ws(true);
      left = { type: 'Binary', op: m[2].toLowerCase(), cs: m[1].toLowerCase() === 'c', left, right: this.additive(nc) };
    }
  }

  additive(nc) {
    let left = this.multiplicative(nc);
    for (;;) {
      const save = this.pos;
      this.ws();
      const c = this.ch, n = this.peek();
      if ((c === '+' && n !== '+' && n !== '=') || (c === '-' && n !== '=' && n !== '-' && !/[A-Za-z]/.test(n || ''))) {
        this.pos++; this.ws(true);
        left = { type: 'Binary', op: c, left, right: this.multiplicative(nc) };
      } else { this.pos = save; return left; }
    }
  }

  multiplicative(nc) {
    let left = this.format(nc);
    for (;;) {
      const save = this.pos;
      this.ws();
      const c = this.ch;
      if ((c === '*' || c === '/' || c === '%') && this.peek() !== '=') {
        this.pos++; this.ws(true);
        left = { type: 'Binary', op: c, left, right: this.format(nc) };
      } else { this.pos = save; return left; }
    }
  }

  format(nc) {
    let left = this.rangeExpr(nc);
    for (;;) {
      const save = this.pos;
      this.ws();
      if (!this.match(/-f(?![\w-])/iy)) { this.pos = save; return left; }
      this.ws(true);
      left = { type: 'Binary', op: 'f', left, right: this.rangeExpr(nc) };
    }
  }

  rangeExpr(nc) {
    const left = this.commaExpr(nc);
    const save = this.pos;
    this.ws();
    if (this.startsWith('..') && this.src[this.pos + 2] !== '.') {
      this.pos += 2; this.ws(true);
      return { type: 'Range', left, right: this.commaExpr(nc) };
    }
    this.pos = save;
    return left;
  }

  commaExpr(nc) {
    const first = this.unary();
    if (nc) return first;
    const save = this.pos;
    this.ws();
    if (this.ch !== ',') { this.pos = save; return first; }
    const items = [first];
    while (this.ch === ',') { this.pos++; this.ws(true); items.push(this.unary()); const s = this.pos; this.ws(); if (this.ch !== ',') { this.pos = s; break; } }
    return { type: 'ArrayLit', items };
  }

  unary() {
    const c = this.ch;
    if (c === '!') { this.pos++; this.ws(); return { type: 'Not', expr: this.unary() }; }
    if (c === '-') {
      if (this.match(/-not(?![\w-])/iy)) { this.ws(); return { type: 'Not', expr: this.unary() }; }
      const m = this.match(/-(split|join)(?![\w-])/iy);
      if (m) { this.ws(); return { type: 'UnaryOp', op: m[1].toLowerCase(), expr: this.unary() }; }
      if (this.peek() === '-') { this.pos += 2; return { type: 'PreUpdate', op: '-', target: this.unary() }; }
      this.pos++; this.ws();
      return { type: 'Neg', expr: this.unary() };
    }
    if (c === '+' && this.peek() === '+') { this.pos += 2; return { type: 'PreUpdate', op: '+', target: this.unary() }; }
    if (c === '+') { this.pos++; this.ws(); return { type: 'Plus', expr: this.unary() }; }
    if (c === ',') { this.pos++; this.ws(); return { type: 'ArrayLit', items: [this.unary()] }; }
    if (c === '[') {
      const save = this.pos;
      const t = this.bracketText();
      if (this.startsWith('::') || this.ch === '.' || this.ch === '[') { this.pos = save; return this.postfix(); }
      const afterType = this.pos;
      this.ws(false);
      const n = this.ch;
      if (n !== undefined && ('$("\'@[,!+'.includes(n) || /[\d]/.test(n) || (n === '-' && /[\d.$(]/.test(this.peek() || '')))) {
        return { type: 'Cast', typeName: t, expr: this.unary() };
      }
      this.pos = afterType;
      return { type: 'TypeLit', name: t };
    }
    return this.postfix();
  }

  postfix() {
    let node = this.primary();
    for (;;) {
      const c = this.ch;
      const isStatic = this.startsWith('::');
      if (isStatic || (c === '.' && this.peek() !== '.')) {
        this.pos += isStatic ? 2 : 1;
        let name;
        const m = this.match(/[A-Za-z_][\w]*/y);
        if (m) name = { type: 'Str', value: m[0] };
        else if (this.ch === '"' || this.ch === "'" || this.ch === '$' || this.ch === '(') name = this.primary();
        else this.err('Missing property name after reference operator.');
        if (this.ch === '(') {
          this.pos++;
          this.nl.push(true);
          const args = [];
          this.ws();
          if (this.ch !== ')') {
            for (;;) {
              this.ws();
              args.push(this.expression(true));
              this.ws();
              if (this.ch === ',') { this.pos++; continue; }
              break;
            }
          }
          this.nl.pop();
          this.expectChar(')');
          node = { type: 'Invoke', obj: node, name, args, isStatic };
        } else node = { type: 'Member', obj: node, name, isStatic };
      } else if (c === '[' && node.type !== 'TypeLit') {
        this.pos++;
        this.nl.push(true);
        this.ws();
        const idx = this.expression();
        this.ws();
        this.nl.pop();
        this.expectChar(']');
        node = { type: 'Index', obj: node, idx };
      } else if ((c === '+' && this.peek() === '+') || (c === '-' && this.peek() === '-')) {
        if (!['Var', 'Member', 'Index', 'EnvVar'].includes(node.type)) return node;
        this.pos += 2;
        node = { type: 'PostUpdate', op: c, target: node };
      } else return node;
    }
  }

  primary() {
    const c = this.ch, n = this.peek();
    if (c === undefined) this.err('Missing expression.', true);
    if (c === '$') {
      if (n === '(') { this.pos += 2; const stmts = this.statementList(')'); this.pos++; return { type: 'SubExpr', stmts }; }
      const v = this.variable();
      if (v) return v;
      this.pos++;
      return { type: 'Str', value: '$' };
    }
    if (c === '@') {
      if (n === '(') { this.pos += 2; const stmts = this.statementList(')'); this.pos++; return { type: 'ArrayExpr', stmts }; }
      if (n === '{') return this.hashLiteral();
      if ((n === '"' || n === "'") && this.src[this.pos + 2] === '\n') return this.hereString();
    }
    if (c === '(') {
      this.pos++;
      this.nl.push(true);
      this.ws();
      if (this.ch === ')') this.err('An expression was expected after \'(\'.', false);
      const inner = this.statement();
      this.ws();
      this.nl.pop();
      this.expectChar(')');
      return { type: 'Paren', inner };
    }
    if (c === '{') return this.scriptBlockLiteral();
    if (c === '"' || c === '“' || c === '”') {
      this.pos++;
      const parts = this.expandParts('"');
      return parts.every(p => typeof p === 'string') ? { type: 'Str', value: parts.join('') } : { type: 'ExpStr', parts };
    }
    if (c === "'" || c === '‘' || c === '’') return this.literalString();
    if (/[\d.]/.test(c)) {
      const m = this.match(NUM_RE);
      if (m) {
        let v = /^0x/i.test(m[1]) ? parseInt(m[1], 16) : parseFloat(m[1]);
        if (m[2]) v *= { kb: 1024, mb: 1024 ** 2, gb: 1024 ** 3, tb: 1024 ** 4, pb: 1024 ** 5 }[m[2].toLowerCase()];
        return { type: 'Num', value: v };
      }
    }
    if (c === '[') return { type: 'TypeLit', name: this.bracketText() };
    if (this.isCommandStart()) {
      // a bareword in expression position, e.g. hashtable member names or switch patterns
      return this.bareArg();
    }
    this.err(`Unexpected token '${c}' in expression or statement.`, false);
  }

  variable() {
    const m = this.match(VAR_RE);
    if (!m) return null;
    if (m[1] !== undefined) {
      const em = /^(env|global|script|local|private):(.*)$/i.exec(m[1]);
      if (em && em[1].toLowerCase() === 'env') return { type: 'EnvVar', name: em[2] };
      return { type: 'Var', name: em ? em[2] : m[1], scope: em ? em[1].toLowerCase() : null };
    }
    if (m[2] && m[2].toLowerCase() === 'env') return { type: 'EnvVar', name: m[3] };
    return { type: 'Var', name: m[3], scope: m[2] ? m[2].toLowerCase() : null };
  }

  literalString() {
    this.pos++;
    let out = '';
    for (;;) {
      if (this.eof()) this.err("The string is missing the terminator: '.", true);
      const c = this.ch;
      if (c === "'" || c === '‘' || c === '’') {
        if (this.peek() === c) { out += c; this.pos += 2; continue; }
        this.pos++;
        return { type: 'Str', value: out };
      }
      out += c; this.pos++;
    }
  }

  hereString() {
    const q = this.peek();
    this.pos += 3;
    const endTok = '\n' + q + '@';
    const end = this.src.indexOf(endTok, this.pos - 1);
    if (end < 0) { this.pos = this.src.length; this.err(`The here-string is missing its terminator: ${q}@`, true); }
    const body = end < this.pos ? '' : this.src.slice(this.pos, end);
    this.pos = end + endTok.length;
    if (q === "'") return { type: 'Str', value: body };
    const parts = new Parser(body).expandParts(null);
    return parts.every(p => typeof p === 'string') ? { type: 'Str', value: parts.join('') } : { type: 'ExpStr', parts };
  }

  // Parses the inside of an expandable string into literal text and embedded nodes.
  expandParts(term) {
    const parts = [];
    let buf = '';
    const isTerm = c => term && (c === term || (term === '"' && (c === '“' || c === '”')));
    for (;;) {
      if (this.eof()) { if (term) this.err('The string is missing the terminator: ".', true); break; }
      const c = this.ch;
      if (isTerm(c)) {
        if (isTerm(this.peek())) { buf += c; this.pos += 2; continue; }
        this.pos++;
        break;
      }
      if (c === '`' && this.pos + 1 < this.src.length) {
        const e = this.peek();
        if (e === 'u' && this.src[this.pos + 2] === '{') {
          const close = this.src.indexOf('}', this.pos);
          buf += String.fromCodePoint(parseInt(this.src.slice(this.pos + 3, close), 16));
          this.pos = close + 1;
          continue;
        }
        buf += ESCAPES[e] ?? e;
        this.pos += 2;
        continue;
      }
      if (c === '$') {
        if (this.peek() === '(') {
          if (buf) { parts.push(buf); buf = ''; }
          this.pos += 2;
          parts.push({ type: 'SubExpr', stmts: this.statementList(')') });
          this.pos++;
          continue;
        }
        const v = this.variable();
        if (v) { if (buf) { parts.push(buf); buf = ''; } parts.push(v); continue; }
      }
      buf += c;
      this.pos++;
    }
    if (buf) parts.push(buf);
    return parts;
  }

  hashLiteral() {
    this.pos += 2;
    this.nl.push(false);
    const entries = [];
    for (;;) {
      this.ws(true);
      if (this.ch === ';') { this.pos++; continue; }
      if (this.eof()) this.err("Missing '}' in hash literal.", true);
      if (this.ch === '}') { this.pos++; break; }
      let key;
      if (this.ch === '"' || this.ch === "'" || this.ch === '(' || this.ch === '$') key = this.postfix();
      else {
        const m = this.match(/[^\s=;}]+/y);
        if (!m) this.err('Missing key before \'=\' in hash literal.');
        key = { type: 'Str', value: m[0] };
      }
      this.ws(false);
      if (this.ch !== '=') this.err("Missing '=' operator after key in hash literal.");
      this.pos++;
      this.ws(true);
      entries.push([key, this.statement()]);
      this.ws(false);
    }
    this.nl.pop();
    return { type: 'Hash', entries };
  }

  scriptBlockLiteral() {
    const start = this.pos;
    this.pos++;
    const stmts = this.statementList('}');
    this.pos++;
    return { type: 'ScriptBlock', body: makeBody(stmts), text: this.src.slice(start + 1, this.pos - 1) };
  }
}

function unescapeBare(s) {
  // drop backtick escapes and embedded quote characters:  --name="a b"  ->  --name=a b
  let out = '';
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (c === '`' && i + 1 < s.length) { out += s[++i]; continue; }
    if ((c === '"' || c === "'") && i > 0) {
      const j = s.indexOf(c, i + 1);
      if (j > i) { out += s.slice(i + 1, j); i = j; continue; }
    }
    out += c;
  }
  return out;
}

// Splits a statement list into param() block and begin/process/end blocks.
function makeBody(stmts) {
  const body = { params: null, begin: null, process: null, end: null };
  let rest = stmts;
  if (rest[0] && rest[0].type === 'Param') { body.params = rest[0].params; rest = rest.slice(1); }
  if (rest.some(s => s.type === 'Named')) {
    for (const s of rest) if (s.type === 'Named') body[s.name] = s.body;
  } else body.end = rest;
  return body;
}

function parse(src) { return new Parser(src).parseScript(); }

module.exports = { parse, Parser, ParseError };
