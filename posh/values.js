'use strict';
// Value semantics shared by the interpreter, cmdlets and formatter: PowerShell-style
// truthiness, conversion, comparison and member access over plain JS values.
// Objects are plain JS objects; a hidden TYPE symbol names their "type" for views.
const os = require('os');
const path = require('path');
const fs = require('fs');
const crypto = require('crypto');

const TYPE = Symbol.for('posh.type');

class PoshError extends Error {
  constructor(message, opts = {}) {
    super(message);
    this.record = opts.record || new ErrorRecord(message, opts);
  }
}

class ErrorRecord {
  constructor(message, { category = 'NotSpecified', target = null, command = null, value } = {}) {
    this.Exception = obj('Exception', { Message: message });
    this.CategoryInfo = category;
    this.TargetObject = target;
    this.FullyQualifiedErrorId = category;
    Object.defineProperty(this, 'command', { value: command, writable: true });
    Object.defineProperty(this, 'thrown', { value });
  }
  get [TYPE]() { return 'ErrorRecord'; }
  toString() { return this.Exception.Message; }
}

class ScriptBlock {
  constructor(body, text) { this.body = body; Object.defineProperty(this, 'text', { value: text }); }
  get [TYPE]() { return 'ScriptBlock'; }
  toString() { return this.text; }
}

class TypeRef {
  constructor(name) { this.Name = name; }
  get [TYPE]() { return 'Type'; }
  toString() { return this.Name; }
}

function obj(type, props) {
  const o = { ...props };
  if (type) Object.defineProperty(o, TYPE, { value: type, writable: true });
  if (typeof o.toString === 'function') Object.defineProperty(o, 'toString', { value: o.toString, enumerable: false });
  return o;
}
const hashtable = (props = {}) => obj('Hashtable', props);
const isHash = v => v !== null && typeof v === 'object' && v[TYPE] === 'Hashtable';
const isObj = v => v !== null && typeof v === 'object' && !Array.isArray(v) && !(v instanceof Date) && !(v instanceof RegExp);

function typeOf(v) {
  if (v === null || v === undefined) return 'null';
  if (Array.isArray(v)) return 'Object[]';
  if (v instanceof Date) return 'DateTime';
  if (v instanceof RegExp) return 'Regex';
  if (typeof v === 'string') return 'String';
  if (typeof v === 'boolean') return 'Boolean';
  if (typeof v === 'number') return Number.isInteger(v) ? 'Int32' : 'Double';
  return v[TYPE] || 'PSCustomObject';
}

// ---------- conversions ----------
function toBool(v) {
  if (v === null || v === undefined) return false;
  if (typeof v === 'boolean') return v;
  if (typeof v === 'number') return v !== 0 && !Number.isNaN(v);
  if (typeof v === 'string') return v.length > 0;
  if (Array.isArray(v)) return v.length === 0 ? false : v.length === 1 ? toBool(v[0]) : true;
  return true;
}

const NUM_RE = /^\s*([+-]?)(0x[0-9a-f]+|(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?)(kb|mb|gb|tb|pb)?\s*$/i;
const MULT = { kb: 1024, mb: 1024 ** 2, gb: 1024 ** 3, tb: 1024 ** 4, pb: 1024 ** 5 };
function parseNumber(s) {
  const m = NUM_RE.exec(s);
  if (!m) return null;
  let n = /^0x/i.test(m[2]) ? parseInt(m[2], 16) : parseFloat(m[2]);
  if (m[3]) n *= MULT[m[3].toLowerCase()];
  return m[1] === '-' ? -n : n;
}
function tryNum(v) {
  if (typeof v === 'number') return v;
  if (typeof v === 'boolean') return v ? 1 : 0;
  if (v === null || v === undefined) return 0;
  if (typeof v === 'string') return v.trim() === '' ? 0 : parseNumber(v);
  if (v instanceof Date) return v.getTime();
  if (Array.isArray(v) && v.length === 1) return tryNum(v[0]);
  return null;
}
function toNum(v) {
  const n = tryNum(v);
  if (n === null) throw new PoshError(`Cannot convert value "${toStr(v)}" to type "System.Int32". Error: "Input string was not in a correct format."`, { category: 'InvalidArgument' });
  return n;
}
// .NET rounds half to even when casting to integer types
function roundEven(n) {
  const r = Math.round(n);
  return Math.abs(n % 1) === 0.5 && r % 2 !== 0 ? r - 1 : r;
}

function numStr(n) {
  if (Number.isNaN(n)) return 'NaN';
  if (n === Infinity) return '∞';
  if (n === -Infinity) return '-∞';
  if (Number.isInteger(n)) return Math.abs(n) >= 1e21 ? n.toExponential().toUpperCase() : String(n);
  return String(parseFloat(n.toPrecision(15)));
}

function toStr(v) {
  if (v === null || v === undefined) return '';
  if (typeof v === 'string') return v;
  if (typeof v === 'boolean') return v ? 'True' : 'False';
  if (typeof v === 'number') return numStr(v);
  if (Array.isArray(v)) return v.map(toStr).join(' ');
  if (v instanceof Date) return fmtDate(v, 'MM/dd/yyyy HH:mm:ss');
  if (v instanceof RegExp) return v.source;
  const t = v[TYPE];
  if (t === 'Hashtable') return 'System.Collections.Hashtable';
  if (t === 'FileInfo' || t === 'DirectoryInfo') return v.FullName;
  if (t === 'Process') return `System.Diagnostics.Process (${v.ProcessName})`;
  if (Object.prototype.hasOwnProperty.call(v, 'toString') || (v.toString !== Object.prototype.toString && typeof v.toString === 'function')) return v.toString();
  return '@{' + Object.keys(v).map(k => `${k}=${toStr(v[k])}`).join('; ') + '}';
}

function toDate(v) {
  if (v instanceof Date) return v;
  if (typeof v === 'number') return new Date(v);
  const d = new Date(toStr(v));
  return Number.isNaN(d.getTime()) ? null : d;
}

// ---------- types / casts ----------
const TYPE_ALIASES = {
  int32: 'int', int64: 'long', uint32: 'int', uint64: 'long', int16: 'int', byte: 'int', short: 'int', uint: 'int', ulong: 'long', sbyte: 'int',
  boolean: 'bool', single: 'double', float: 'double', decimal: 'double', 'object[]': 'array', psobject: 'pscustomobject',
  'collections.hashtable': 'hashtable', 'management.automation.pscustomobject': 'pscustomobject', ordered: 'hashtable',
  'text.regularexpressions.regex': 'regex', 'management.automation.scriptblock': 'scriptblock', 'collections.specialized.ordereddictionary': 'hashtable',
};
function normType(t) {
  t = t.toLowerCase().replace(/^system\./, '').trim();
  return TYPE_ALIASES[t] || t;
}

function cast(t, v) {
  const nt = normType(t);
  if (nt.endsWith('[]')) {
    const arr = v === null || v === undefined ? [] : Array.isArray(v) ? v : [v];
    const el = nt.slice(0, -2);
    return el === 'object' ? arr : arr.map(x => cast(el, x));
  }
  switch (nt) {
    case 'int': case 'long': return roundEven(toNum(v));
    case 'double': return toNum(v);
    case 'string': return v === null || v === undefined ? '' : toStr(v);
    case 'bool': case 'switch': return toBool(v);
    case 'char': {
      if (typeof v === 'number') return String.fromCharCode(v);
      const s = toStr(v);
      if (s.length !== 1) throw new PoshError(`Cannot convert value "${s}" to type "System.Char". Error: "String must be exactly one character long."`);
      return s;
    }
    case 'array': return v === null || v === undefined ? [] : Array.isArray(v) ? v : [v];
    case 'hashtable': if (isHash(v)) return v; if (isObj(v)) return hashtable({ ...v }); break;
    case 'pscustomobject': if (isObj(v)) return obj(null, { ...v }); return v;
    case 'datetime': { const d = toDate(v); if (d) return d; break; }
    case 'regex': return v instanceof RegExp ? v : makeRegex(toStr(v), true);
    case 'scriptblock': if (v instanceof ScriptBlock) return v; break;
    case 'void': return undefined;
    case 'object': case 'psobject': return v;
    case 'guid': case 'version': case 'uri': return toStr(v);
    default: throw new PoshError(`Unable to find type [${t}].`, { category: 'InvalidOperation' });
  }
  throw new PoshError(`Cannot convert "${toStr(v)}" to type "${t}".`, { category: 'InvalidArgument' });
}

function isType(v, t) {
  const nt = normType(t);
  switch (nt) {
    case 'int': case 'long': return typeof v === 'number' && Number.isInteger(v);
    case 'double': return typeof v === 'number';
    case 'string': return typeof v === 'string';
    case 'bool': return typeof v === 'boolean';
    case 'char': return typeof v === 'string' && v.length === 1;
    case 'array': return Array.isArray(v);
    case 'hashtable': return isHash(v);
    case 'pscustomobject': return isObj(v) && !v[TYPE];
    case 'datetime': return v instanceof Date;
    case 'regex': return v instanceof RegExp;
    case 'scriptblock': return v instanceof ScriptBlock;
    case 'object': return v !== null && v !== undefined;
    default: return typeOf(v).toLowerCase() === nt;
  }
}

// ---------- regex / wildcard ----------
function makeRegex(pattern, cs, global = false) {
  let flags = cs ? '' : 'i';
  // .NET inline options aren't valid JS; translate the common (?i) prefix
  const m = /^\(\?([imsx]+)\)/.exec(pattern);
  if (m) { pattern = pattern.slice(m[0].length); if (m[1].includes('i')) flags = 'i'; if (m[1].includes('s')) flags += 's'; if (m[1].includes('m')) flags += 'm'; }
  try { return new RegExp(pattern, flags + (global ? 'g' : '') + 'u'); }
  catch { try { return new RegExp(pattern, flags + (global ? 'g' : '')); }
  catch (e) { throw new PoshError(`Invalid regular expression pattern: ${pattern}. ${e.message}`, { category: 'InvalidArgument' }); } }
}

function wildcardRe(p, cs = false) {
  let re = '';
  for (let i = 0; i < p.length; i++) {
    const c = p[i];
    if (c === '`' && i + 1 < p.length) re += p[++i].replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    else if (c === '*') re += '.*';
    else if (c === '?') re += '.';
    else if (c === '[') { const j = p.indexOf(']', i); if (j > i) { re += '[' + p.slice(i + 1, j).replace(/\\/g, '\\\\') + ']'; i = j; } else re += '\\['; }
    else re += c.replace(/[.*+?^${}()|[\]\\/]/g, '\\$&');
  }
  return new RegExp('^' + re + '$', cs ? 's' : 'is');
}
const hasWildcard = s => /[*?[]/.test(s);

// ---------- comparison ----------
function eq(a, b, cs) {
  if (a === null || a === undefined) return b === null || b === undefined;
  if (b === null || b === undefined) return false;
  if (typeof a === 'string') { const s = toStr(b); return cs ? a === s : a.toLowerCase() === s.toLowerCase(); }
  if (typeof a === 'number') { const n = typeof b === 'string' && b.trim() === '' ? null : tryNum(b); return n !== null && a === n; }
  if (typeof a === 'boolean') return a === toBool(b);
  if (a instanceof Date) { const d = toDate(b); return !!d && a.getTime() === d.getTime(); }
  return a === b;
}

function compare(a, b, cs) {
  if (a === null || a === undefined) return b === null || b === undefined ? 0 : -1;
  if (b === null || b === undefined) return 1;
  if (typeof a === 'number' || typeof a === 'boolean') { const x = toNum(a), y = toNum(b); return x < y ? -1 : x > y ? 1 : 0; }
  if (a instanceof Date) { const d = toDate(b); const x = a.getTime(), y = d ? d.getTime() : 0; return x < y ? -1 : x > y ? 1 : 0; }
  const s = toStr(a), t = toStr(b);
  const r = s.localeCompare(t, undefined, { sensitivity: cs ? 'variant' : 'accent', numeric: false });
  return r < 0 ? -1 : r > 0 ? 1 : 0;
}

// Comparison operators applied to an array on the left filter the array.
const FILTER_OPS = new Set(['eq', 'ne', 'gt', 'ge', 'lt', 'le', 'like', 'notlike', 'match', 'notmatch']);

function scalarCompare(op, l, r, cs, setMatches) {
  switch (op) {
    case 'eq': return eq(l, r, cs);
    case 'ne': return !eq(l, r, cs);
    case 'gt': return compare(l, r, cs) > 0;
    case 'ge': return compare(l, r, cs) >= 0;
    case 'lt': return compare(l, r, cs) < 0;
    case 'le': return compare(l, r, cs) <= 0;
    case 'like': return wildcardRe(toStr(r), cs).test(toStr(l));
    case 'notlike': return !wildcardRe(toStr(r), cs).test(toStr(l));
    case 'match': case 'notmatch': {
      const m = makeRegex(toStr(r), cs).exec(toStr(l));
      if (m && setMatches) setMatches(matchesTable(m));
      return op === 'match' ? !!m : !m;
    }
  }
  throw new Error('bad op ' + op);
}

function matchesTable(m) {
  const h = hashtable();
  m.forEach((g, i) => { if (g !== undefined) h[i] = g; });
  if (m.groups) for (const [k, v] of Object.entries(m.groups)) if (v !== undefined) h[k] = v;
  return h;
}

function add(l, r) {
  if (Array.isArray(l)) return l.concat(Array.isArray(r) ? r : [r]);
  if (typeof l === 'string') return l + toStr(r);
  if (isHash(l) && isHash(r)) {
    for (const k of Object.keys(r)) if (findKey(l, k) !== undefined) throw new PoshError(`Item has already been added. Key in dictionary: '${k}'`);
    return hashtable({ ...l, ...r });
  }
  if (l instanceof Date) return new Date(l.getTime() + (isObj(r) && r[TYPE] === 'TimeSpan' ? r.TotalMilliseconds : toNum(r) / 1e4));
  if (l === null || l === undefined) return r;
  return toNum(l) + toNum(r);
}

function binaryOp(op, l, r, cs, setMatches) {
  switch (op) {
    case '+': return add(l, r);
    case '-':
      if (l instanceof Date && r instanceof Date) return timeSpan(l - r);
      if (l instanceof Date) return new Date(l.getTime() - (isObj(r) && r[TYPE] === 'TimeSpan' ? r.TotalMilliseconds : toNum(r) / 1e4));
      return toNum(l) - toNum(r);
    case '*':
      if (typeof l === 'string') return l.repeat(Math.max(0, toNum(r)));
      if (Array.isArray(l)) { const out = []; for (let i = 0; i < toNum(r); i++) out.push(...l); return out; }
      return toNum(l) * toNum(r);
    case '/': { const d = toNum(r); if (d === 0) throw new PoshError('Attempted to divide by zero.', { category: 'DivideByZero' }); return toNum(l) / d; }
    case '%': { const d = toNum(r); if (d === 0) throw new PoshError('Attempted to divide by zero.', { category: 'DivideByZero' }); return toNum(l) % d; }
    case 'f': return formatComposite(toStr(l), Array.isArray(r) ? r : [r]);
    case 'band': return toNum(l) & toNum(r);
    case 'bor': return toNum(l) | toNum(r);
    case 'bxor': return toNum(l) ^ toNum(r);
    case 'contains': return (Array.isArray(l) ? l : [l]).some(x => eq(x, r, cs));
    case 'notcontains': return !(Array.isArray(l) ? l : [l]).some(x => eq(x, r, cs));
    case 'in': return (Array.isArray(r) ? r : [r]).some(x => eq(x, l, cs));
    case 'notin': return !(Array.isArray(r) ? r : [r]).some(x => eq(x, l, cs));
    case 'is': return isType(l, toStr(r));
    case 'isnot': return !isType(l, toStr(r));
    case 'as': try { return cast(toStr(r), l); } catch { return null; }
    case 'join': return (Array.isArray(l) ? l : [l]).map(toStr).join(toStr(r));
    case 'split': {
      const [pat, max] = Array.isArray(r) ? r : [r, 0];
      const one = s => {
        const parts = toStr(s).split(makeRegex(toStr(pat), cs));
        if (max > 0 && parts.length > max) {
          // .NET semantics: last piece keeps the remainder
          const re = makeRegex(toStr(pat), cs, true); let count = 0, idx = 0, m;
          while (count < max - 1 && (m = re.exec(s))) { idx = m.index + m[0].length; count++; }
          return [...parts.slice(0, max - 1), toStr(s).slice(idx)];
        }
        return parts;
      };
      return Array.isArray(l) ? l.flatMap(one) : one(l);
    }
    case 'replace': {
      const [pat, rep = ''] = Array.isArray(r) ? r : [r];
      const re = makeRegex(toStr(pat), cs, true);
      const repl = toStr(rep).replace(/\$\{(\w+)\}/g, '$$<$1>');
      const one = s => toStr(s).replace(re, repl);
      return Array.isArray(l) ? l.map(one) : one(l);
    }
  }
  if (FILTER_OPS.has(op)) {
    if (Array.isArray(l)) return l.filter(x => scalarCompare(op, x, r, cs, null));
    return scalarCompare(op, l, r, cs, setMatches);
  }
  throw new PoshError(`Unknown operator -${op}`);
}

function range(a, b) {
  if (typeof a === 'string' && typeof b === 'string' && a.length === 1 && b.length === 1 && !/\d/.test(a + b)) {
    return range(a.charCodeAt(0), b.charCodeAt(0)).map(c => String.fromCharCode(c));
  }
  const x = roundEven(toNum(a)), y = roundEven(toNum(b));
  if (Math.abs(y - x) > 5e7) throw new PoshError('The range is too large.');
  const out = [];
  if (x <= y) for (let i = x; i <= y; i++) out.push(i); else for (let i = x; i >= y; i--) out.push(i);
  return out;
}

// ---------- member access ----------
function findKey(o, name) {
  if (Object.prototype.hasOwnProperty.call(o, name)) return name;
  const l = String(name).toLowerCase();
  return Object.keys(o).find(k => k.toLowerCase() === l);
}

const DATE_PROPS = {
  year: d => d.getFullYear(), month: d => d.getMonth() + 1, day: d => d.getDate(), hour: d => d.getHours(),
  minute: d => d.getMinutes(), second: d => d.getSeconds(), millisecond: d => d.getMilliseconds(),
  dayofweek: d => DAYS[d.getDay()], dayofyear: d => Math.floor((d - new Date(d.getFullYear(), 0, 0)) / 864e5),
  date: d => new Date(d.getFullYear(), d.getMonth(), d.getDate()), ticks: d => d.getTime() * 1e4 + 621355968000000000,
  timeofday: d => timeSpan(d - new Date(d.getFullYear(), d.getMonth(), d.getDate())),
};

function getMember(v, name) {
  if (v === null || v === undefined) return null;
  const n = name.toLowerCase();
  if (Array.isArray(v)) {
    if (n === 'count' || n === 'length') return v.length;
    const out = [];
    for (const x of v) { const r = getMember(x, name); if (Array.isArray(r)) out.push(...r); else if (r !== null && r !== undefined) out.push(r); }
    return out.length === 0 ? null : out.length === 1 ? out[0] : out;
  }
  if (typeof v === 'string') return n === 'length' ? v.length : n === 'count' ? 1 : null;
  if (v instanceof Date) return DATE_PROPS[n] ? DATE_PROPS[n](v) : null;
  if (typeof v === 'object') {
    if (v instanceof TypeRef) return staticMember(v.Name, name);
    if (isHash(v)) {
      const k = findKey(v, name);
      if (k !== undefined) return v[k];
      if (n === 'count') return Object.keys(v).length;
      if (n === 'keys') return Object.keys(v);
      if (n === 'values') return Object.values(v);
      return null;
    }
    const k = findKey(v, name);
    if (k !== undefined) return v[k];
    if (n === 'count') return 1;
    if (v instanceof ScriptBlock && n === 'ast') return v.text;
    return null;
  }
  if (n === 'count') return 1;
  return null;
}

function setMember(o, name, value) {
  if (o === null || o === undefined) throw new PoshError(`The property '${name}' cannot be found on this object. Verify that the property exists and can be set.`);
  if (Array.isArray(o)) { for (const x of o) setMember(x, name, value); return; }
  if (typeof o !== 'object' || o instanceof Date) throw new PoshError(`'${name}' is a ReadOnly property.`);
  const k = findKey(o, name);
  if (k === undefined && !isHash(o) && o[TYPE]) throw new PoshError(`The property '${name}' cannot be found on this object. Verify that the property exists and can be set.`);
  o[k ?? name] = value;
}

function index(v, idx) {
  if (v === null || v === undefined) throw new PoshError('Cannot index into a null array.', { category: 'InvalidOperation' });
  if (Array.isArray(idx)) { const out = []; for (const i of idx) { const r = index(v, i); if (r !== null && r !== undefined) out.push(r); } return out; }
  if (Array.isArray(v) || typeof v === 'string') {
    let i = toNum(idx);
    if (i < 0) i += v.length;
    return i >= 0 && i < v.length ? v[i] : null;
  }
  if (isObj(v)) { const k = findKey(v, toStr(idx)); return k === undefined ? null : v[k]; }
  return toNum(idx) === 0 || toNum(idx) === -1 ? v : null;
}

function setIndex(v, idx, value) {
  if (Array.isArray(v)) {
    let i = toNum(idx); if (i < 0) i += v.length;
    if (i < 0 || i >= v.length) throw new PoshError('Index was outside the bounds of the array.', { category: 'InvalidOperation' });
    v[i] = value; return;
  }
  if (isObj(v)) { const k = findKey(v, toStr(idx)); v[k ?? toStr(idx)] = value; return; }
  throw new PoshError(`Unable to index into an object of type ${typeOf(v)}.`);
}

function timeSpan(ms) {
  const sign = ms < 0 ? -1 : 1, a = Math.abs(ms);
  return obj('TimeSpan', {
    Days: sign * Math.floor(a / 864e5), Hours: sign * Math.floor(a / 36e5) % 24, Minutes: sign * Math.floor(a / 6e4) % 60,
    Seconds: sign * Math.floor(a / 1e3) % 60, Milliseconds: sign * Math.floor(a % 1e3), Ticks: Math.round(ms * 1e4),
    TotalDays: ms / 864e5, TotalHours: ms / 36e5, TotalMinutes: ms / 6e4, TotalSeconds: ms / 1e3, TotalMilliseconds: ms,
  });
}

const regexMatchObj = m => obj('Match', {
  Success: true, Value: m[0], Index: m.index, Length: m[0].length,
  Groups: m.map((g, i) => obj('Group', { Name: String(i), Value: g ?? '', Success: g !== undefined })).concat(
    Object.entries(m.groups || {}).map(([k, g]) => obj('Group', { Name: k, Value: g ?? '', Success: g !== undefined }))),
});

function callMethod(v, name, args, interp) {
  const n = name.toLowerCase();
  if (v === null || v === undefined) throw new PoshError('You cannot call a method on a null-valued expression.', { category: 'InvalidOperation' });
  const a = args;
  if (v instanceof TypeRef) {
    const m = staticMember(v.Name, name, true);
    if (typeof m !== 'function') throw new PoshError(`Method invocation failed because [${v.Name}] does not contain a method named '${name}'.`);
    return m(...a);
  }
  if (typeof v === 'string') {
    const s = v;
    switch (n) {
      case 'toupper': case 'toupperinvariant': return s.toUpperCase();
      case 'tolower': case 'tolowerinvariant': return s.toLowerCase();
      case 'trim': return a.length ? trimChars(s, toStr(a[0]), true, true) : s.trim();
      case 'trimstart': return a.length ? trimChars(s, toStr(a[0]), true, false) : s.trimStart();
      case 'trimend': return a.length ? trimChars(s, toStr(a[0]), false, true) : s.trimEnd();
      case 'split': {
        if (!a.length) return s.split(/\s/);
        const seps = (Array.isArray(a[0]) ? a[0] : [a[0]]).map(toStr);
        // a string separator splits on any of its chars (char[] overload) unless it's a single string arg of length>1
        const parts = seps.length === 1 && seps[0].length > 1 && typeof a[0] === 'string'
          ? s.split(seps[0]) : s.split(new RegExp('[' + seps.join('').replace(/[\]\\^-]/g, '\\$&') + ']'));
        return a[1] && /RemoveEmpty/i.test(toStr(a[a.length - 1])) ? parts.filter(Boolean) : parts;
      }
      case 'replace': return s.split(toStr(a[0])).join(toStr(a[1]));
      case 'contains': return a[1] && /IgnoreCase/i.test(toStr(a[1])) ? s.toLowerCase().includes(toStr(a[0]).toLowerCase()) : s.includes(toStr(a[0]));
      case 'startswith': return a[1] && /IgnoreCase/i.test(toStr(a[1])) ? s.toLowerCase().startsWith(toStr(a[0]).toLowerCase()) : s.startsWith(toStr(a[0]));
      case 'endswith': return a[1] && /IgnoreCase/i.test(toStr(a[1])) ? s.toLowerCase().endsWith(toStr(a[0]).toLowerCase()) : s.endsWith(toStr(a[0]));
      case 'substring': return a.length > 1 ? s.substr(toNum(a[0]), toNum(a[1])) : s.slice(toNum(a[0]));
      case 'indexof': return s.indexOf(toStr(a[0]), a[1] ? toNum(a[1]) : 0);
      case 'lastindexof': return s.lastIndexOf(toStr(a[0]));
      case 'padleft': return s.padStart(toNum(a[0]), a[1] ? toStr(a[1]) : ' ');
      case 'padright': return s.padEnd(toNum(a[0]), a[1] ? toStr(a[1]) : ' ');
      case 'insert': return s.slice(0, toNum(a[0])) + toStr(a[1]) + s.slice(toNum(a[0]));
      case 'remove': return a.length > 1 ? s.slice(0, toNum(a[0])) + s.slice(toNum(a[0]) + toNum(a[1])) : s.slice(0, toNum(a[0]));
      case 'tochararray': return [...s];
      case 'equals': return s === toStr(a[0]);
      case 'compareto': return compare(s, a[0], true);
      case 'tostring': return s;
      case 'gettype': return new TypeRef('String');
      case 'normalize': return s.normalize();
      case 'clone': return s;
    }
  }
  if (Array.isArray(v)) {
    switch (n) {
      case 'contains': return v.some(x => eq(x, a[0], true));
      case 'indexof': return v.findIndex(x => eq(x, a[0], true));
      case 'where': return interp.arrayWhere(v, a[0]);
      case 'foreach': return interp.arrayForEach(v, a[0], a.slice(1));
      case 'clone': return v.slice();
      case 'add': case 'remove': case 'insert': case 'clear':
        throw new PoshError(`Exception calling "${name}" with "${a.length}" argument(s): "Collection was of a fixed size." Use $array += $item instead.`);
      case 'gettype': return new TypeRef('Object[]');
      case 'tostring': return 'System.Object[]';
      case 'getvalue': return v[toNum(a[0])];
    }
  }
  if (v instanceof Date) {
    const d = new Date(v);
    const addMs = ms => new Date(v.getTime() + ms);
    switch (n) {
      case 'adddays': return addMs(toNum(a[0]) * 864e5);
      case 'addhours': return addMs(toNum(a[0]) * 36e5);
      case 'addminutes': return addMs(toNum(a[0]) * 6e4);
      case 'addseconds': return addMs(toNum(a[0]) * 1e3);
      case 'addmilliseconds': return addMs(toNum(a[0]));
      case 'addmonths': d.setMonth(d.getMonth() + toNum(a[0])); return d;
      case 'addyears': d.setFullYear(d.getFullYear() + toNum(a[0])); return d;
      case 'add': return add(v, a[0]);
      case 'subtract': return a[0] instanceof Date ? timeSpan(v - a[0]) : binaryOp('-', v, a[0]);
      case 'tostring': return a.length ? fmtDate(v, toStr(a[0])) : toStr(v);
      case 'toshortdatestring': return fmtDate(v, 'M/d/yyyy');
      case 'tolongdatestring': return fmtDate(v, 'dddd, MMMM d, yyyy');
      case 'toshorttimestring': return fmtDate(v, 'h:mm tt');
      case 'tolongtimestring': return fmtDate(v, 'h:mm:ss tt');
      case 'touniversaltime': case 'tolocaltime': return d;
      case 'toiso8601': return v.toISOString();
      case 'gettype': return new TypeRef('DateTime');
      case 'compareto': return compare(v, a[0]);
      case 'equals': return eq(v, a[0]);
    }
  }
  if (typeof v === 'number') {
    switch (n) {
      case 'tostring': return a.length ? formatValue(v, toStr(a[0])) : numStr(v);
      case 'gettype': return new TypeRef(typeOf(v));
      case 'equals': return eq(v, a[0]);
      case 'compareto': return compare(v, a[0]);
    }
  }
  if (typeof v === 'boolean' && n === 'tostring') return toStr(v);
  if (v instanceof RegExp) {
    const s = toStr(a[0]);
    switch (n) {
      case 'ismatch': return v.test(s);
      case 'match': { const m = new RegExp(v.source, v.flags.replace('g', '')).exec(s); return m ? regexMatchObj(m) : obj('Match', { Success: false, Value: '' }); }
      case 'matches': return [...s.matchAll(new RegExp(v.source, v.flags.includes('g') ? v.flags : v.flags + 'g'))].map(regexMatchObj);
      case 'replace': return s.replace(new RegExp(v.source, v.flags.includes('g') ? v.flags : v.flags + 'g'), toStr(a[1]));
      case 'split': return s.split(v);
      case 'tostring': return v.source;
    }
  }
  if (v instanceof ScriptBlock) {
    if (n === 'invoke' || n === 'invokereturnasis') return interp.invokeScriptBlock(v, { args: a.map(value => ({ value })), newScope: true }).then(r => r.length === 0 ? null : r.length === 1 ? r[0] : r);
    if (n === 'tostring') return v.text;
  }
  if (typeof v === 'object') {
    if (isHash(v)) {
      switch (n) {
        case 'containskey': case 'contains': return findKey(v, toStr(a[0])) !== undefined;
        case 'containsvalue': return Object.values(v).some(x => eq(x, a[0], true));
        case 'add': if (findKey(v, toStr(a[0])) !== undefined) throw new PoshError(`Exception calling "Add" with "2" argument(s): "Item has already been added. Key in dictionary: '${toStr(a[0])}'"`); v[toStr(a[0])] = a[1]; return undefined;
        case 'remove': { const k = findKey(v, toStr(a[0])); if (k !== undefined) delete v[k]; return undefined; }
        case 'clear': for (const k of Object.keys(v)) delete v[k]; return undefined;
        case 'clone': return hashtable({ ...v });
        case 'getenumerator': return Object.entries(v).map(([Key, Value]) => obj('DictionaryEntry', { Key, Value, Name: Key }));
        case 'keys': return Object.keys(v);
      }
    }
    const k = findKey(v, name);
    if (k !== undefined && typeof v[k] === 'function') return v[k](...a);
    if (k !== undefined && v[k] instanceof ScriptBlock) return callMethod(v[k], 'invoke', a, interp);
    switch (n) {
      case 'tostring': return toStr(v);
      case 'gettype': return new TypeRef(typeOf(v));
      case 'equals': return v === a[0];
      case 'psobject': return v;
    }
  }
  throw new PoshError(`Method invocation failed because [${typeOf(v)}] does not contain a method named '${name}'.`, { category: 'InvalidOperation' });
}

function trimChars(s, chars, start, end) {
  let i = 0, j = s.length;
  if (start) while (i < j && chars.includes(s[i])) i++;
  if (end) while (j > i && chars.includes(s[j - 1])) j--;
  return s.slice(i, j);
}

// ---------- static members: [math]::Round(...) etc. (a small, hand-picked set) ----------
const enc = (name) => ({ GetBytes: s => [...Buffer.from(toStr(s), name)], GetString: b => Buffer.from(b).toString(name), toString: () => name });
const STATICS = {
  math: {
    PI: Math.PI, E: Math.E,
    Round: (x, d = 0) => { const f = 10 ** toNum(d); return roundEven(toNum(x) * f) / f; },
    Floor: x => Math.floor(toNum(x)), Ceiling: x => Math.ceil(toNum(x)), Abs: x => Math.abs(toNum(x)),
    Sqrt: x => Math.sqrt(toNum(x)), Pow: (x, y) => Math.pow(toNum(x), toNum(y)), Max: (x, y) => Math.max(toNum(x), toNum(y)),
    Min: (x, y) => Math.min(toNum(x), toNum(y)), Log: (x, b) => b === undefined ? Math.log(toNum(x)) : Math.log(toNum(x)) / Math.log(toNum(b)),
    Log10: x => Math.log10(toNum(x)), Log2: x => Math.log2(toNum(x)), Exp: x => Math.exp(toNum(x)), Sin: x => Math.sin(toNum(x)),
    Cos: x => Math.cos(toNum(x)), Tan: x => Math.tan(toNum(x)), Atan2: (y, x) => Math.atan2(toNum(y), toNum(x)),
    Truncate: x => Math.trunc(toNum(x)), Sign: x => Math.sign(toNum(x)), Clamp: (x, a, b) => Math.min(Math.max(toNum(x), toNum(a)), toNum(b)),
  },
  datetime: {
    get Now() { return new Date(); }, get Today() { const d = new Date(); d.setHours(0, 0, 0, 0); return d; }, get UtcNow() { return new Date(); },
    Parse: s => { const d = toDate(s); if (!d) throw new PoshError(`String '${toStr(s)}' was not recognized as a valid DateTime.`); return d; },
    DaysInMonth: (y, m) => new Date(toNum(y), toNum(m), 0).getDate(), IsLeapYear: y => new Date(toNum(y), 1, 29).getDate() === 29,
  },
  string: {
    Empty: '', IsNullOrEmpty: s => s === null || s === undefined || toStr(s) === '', IsNullOrWhiteSpace: s => toStr(s).trim() === '',
    Join: (sep, ...items) => (items.length === 1 && Array.isArray(items[0]) ? items[0] : items).map(toStr).join(toStr(sep)),
    Format: (f, ...args) => formatComposite(toStr(f), args.length === 1 && Array.isArray(args[0]) ? args[0] : args),
    Concat: (...items) => items.flat().map(toStr).join(''), Compare: (a, b, ic) => compare(a, b, !ic),
  },
  environment: {
    get NewLine() { return os.EOL; }, get MachineName() { return os.hostname(); }, get UserName() { return os.userInfo().username; },
    get ProcessorCount() { return os.cpus().length; }, get CurrentDirectory() { return process.cwd(); }, get Is64BitOperatingSystem() { return os.arch().includes('64'); },
    get OSVersion() { return obj('OperatingSystem', { Platform: os.platform(), Version: os.release(), VersionString: `${os.type()} ${os.release()}` }); },
    get TickCount() { return Math.floor(performance.now()); }, get UserDomainName() { return process.env.USERDOMAIN || os.hostname(); },
    GetEnvironmentVariable: n => process.env[toStr(n)] ?? null, SetEnvironmentVariable: (n, v) => { if (v === null || v === undefined || v === '') delete process.env[toStr(n)]; else process.env[toStr(n)] = toStr(v); },
    GetFolderPath: f => ({ desktop: path.join(os.homedir(), 'Desktop'), mydocuments: path.join(os.homedir(), 'Documents'), userprofile: os.homedir(), applicationdata: process.env.APPDATA || path.join(os.homedir(), '.config'), localapplicationdata: process.env.LOCALAPPDATA || path.join(os.homedir(), '.local', 'share') }[toStr(f).toLowerCase()] ?? ''),
    Exit: c => { process.exit(toNum(c)); },
  },
  'io.path': {
    DirectorySeparatorChar: path.sep, PathSeparator: path.delimiter,
    Combine: (...p) => path.join(...p.flat().map(toStr)), GetFileName: p => path.basename(toStr(p)),
    GetFileNameWithoutExtension: p => path.parse(toStr(p)).name, GetExtension: p => path.extname(toStr(p)),
    GetDirectoryName: p => path.dirname(toStr(p)), GetTempPath: () => os.tmpdir() + path.sep, GetFullPath: p => path.resolve(toStr(p)),
    GetTempFileName: () => { const f = path.join(os.tmpdir(), 'tmp' + crypto.randomBytes(4).toString('hex') + '.tmp'); fs.writeFileSync(f, ''); return f; },
    ChangeExtension: (p, e) => { const x = path.parse(toStr(p)); return path.join(x.dir, x.name + (e ? (toStr(e).startsWith('.') ? '' : '.') + toStr(e) : '')); },
    IsPathRooted: p => path.isAbsolute(toStr(p)), GetRandomFileName: () => crypto.randomBytes(6).toString('hex').slice(0, 8) + '.' + crypto.randomBytes(2).toString('hex').slice(0, 3),
  },
  'io.file': {
    Exists: p => { try { return fs.statSync(path.resolve(toStr(p))).isFile(); } catch { return false; } },
    ReadAllText: p => fs.readFileSync(path.resolve(toStr(p)), 'utf8'), ReadAllLines: p => fs.readFileSync(path.resolve(toStr(p)), 'utf8').split(/\r?\n/).filter((l, i, a) => i < a.length - 1 || l !== ''),
    WriteAllText: (p, t) => fs.writeFileSync(path.resolve(toStr(p)), toStr(t)), AppendAllText: (p, t) => fs.appendFileSync(path.resolve(toStr(p)), toStr(t)),
    WriteAllLines: (p, l) => fs.writeFileSync(path.resolve(toStr(p)), (Array.isArray(l) ? l : [l]).map(toStr).join(os.EOL) + os.EOL),
    Delete: p => fs.rmSync(path.resolve(toStr(p)), { force: true }), Copy: (a, b) => fs.copyFileSync(path.resolve(toStr(a)), path.resolve(toStr(b))),
    ReadAllBytes: p => [...fs.readFileSync(path.resolve(toStr(p)))], WriteAllBytes: (p, b) => fs.writeFileSync(path.resolve(toStr(p)), Buffer.from(b)),
  },
  'io.directory': {
    Exists: p => { try { return fs.statSync(path.resolve(toStr(p))).isDirectory(); } catch { return false; } },
    CreateDirectory: p => { fs.mkdirSync(path.resolve(toStr(p)), { recursive: true }); return path.resolve(toStr(p)); },
    GetFiles: p => fs.readdirSync(path.resolve(toStr(p)), { withFileTypes: true }).filter(d => d.isFile()).map(d => path.join(path.resolve(toStr(p)), d.name)),
    GetDirectories: p => fs.readdirSync(path.resolve(toStr(p)), { withFileTypes: true }).filter(d => d.isDirectory()).map(d => path.join(path.resolve(toStr(p)), d.name)),
    GetCurrentDirectory: () => process.cwd(), Delete: (p, r) => fs.rmSync(path.resolve(toStr(p)), { recursive: !!r }),
  },
  int: { MaxValue: 2147483647, MinValue: -2147483648, Parse: s => roundEven(toNum(toStr(s))), TryParse: s => parseNumber(toStr(s)) !== null },
  long: { MaxValue: Number.MAX_SAFE_INTEGER, MinValue: Number.MIN_SAFE_INTEGER, Parse: s => roundEven(toNum(toStr(s))) },
  double: { MaxValue: Number.MAX_VALUE, MinValue: -Number.MAX_VALUE, NaN: NaN, PositiveInfinity: Infinity, NegativeInfinity: -Infinity, Epsilon: Number.MIN_VALUE, Parse: s => toNum(toStr(s)), IsNaN: x => Number.isNaN(toNum(x)) },
  char: {
    IsDigit: c => /^\d$/.test(toStr(c)), IsLetter: c => /^\p{L}$/u.test(toStr(c)), IsWhiteSpace: c => /^\s$/.test(toStr(c)),
    IsUpper: c => /^\p{Lu}$/u.test(toStr(c)), IsLower: c => /^\p{Ll}$/u.test(toStr(c)), IsLetterOrDigit: c => /^[\p{L}\d]$/u.test(toStr(c)),
    ToUpper: c => toStr(c).toUpperCase(), ToLower: c => toStr(c).toLowerCase(),
  },
  guid: { NewGuid: () => obj('Guid', { Guid: crypto.randomUUID(), toString() { return this.Guid; } }), Empty: '00000000-0000-0000-0000-000000000000' },
  regex: {
    Escape: s => toStr(s).replace(/[.*+?^${}()|[\]\\#\s]/g, '\\$&'), IsMatch: (s, p) => makeRegex(toStr(p), true).test(toStr(s)),
    Match: (s, p) => callMethod(makeRegex(toStr(p), true), 'match', [s]), Matches: (s, p) => callMethod(makeRegex(toStr(p), true), 'matches', [s]),
    Replace: (s, p, r) => toStr(s).replace(makeRegex(toStr(p), true, true), toStr(r)), Split: (s, p) => toStr(s).split(makeRegex(toStr(p), true)),
    Unescape: s => toStr(s).replace(/\\(.)/g, '$1'),
  },
  convert: {
    ToBase64String: b => Buffer.from(Array.isArray(b) ? b : [...Buffer.from(toStr(b))]).toString('base64'),
    FromBase64String: s => [...Buffer.from(toStr(s), 'base64')],
    ToInt32: (v, base) => base ? parseInt(toStr(v), toNum(base)) : roundEven(toNum(v)), ToInt64: (v, base) => base ? parseInt(toStr(v), toNum(base)) : roundEven(toNum(v)),
    ToString: (v, base) => base ? toNum(v).toString(toNum(base)) : toStr(v), ToDouble: v => toNum(v), ToBoolean: v => toBool(v),
    ToHexString: b => Buffer.from(b).toString('hex').toUpperCase(), FromHexString: s => [...Buffer.from(toStr(s), 'hex')],
  },
  'text.encoding': { UTF8: enc('utf8'), ASCII: enc('ascii'), Unicode: enc('utf16le'), Default: enc('utf8'), Latin1: enc('latin1') },
  console: {
    get WindowWidth() { return process.stdout.columns || 120; }, get WindowHeight() { return process.stdout.rows || 30; },
    WriteLine: (s = '') => { process.stdout.write(toStr(s) + '\n'); }, Write: s => { process.stdout.write(toStr(s)); }, Beep: () => process.stdout.write('\x07'),
  },
  bool: { Parse: s => /^true$/i.test(toStr(s).trim()) },
  timespan: { FromSeconds: s => timeSpan(toNum(s) * 1e3), FromMinutes: m => timeSpan(toNum(m) * 6e4), FromHours: h => timeSpan(toNum(h) * 36e5), FromDays: d => timeSpan(toNum(d) * 864e5), FromMilliseconds: m => timeSpan(toNum(m)) },
};
STATICS['io.fileinfo'] = STATICS['io.file'];

function staticMember(typeName, member) {
  let t = normType(typeName);
  const table = STATICS[t] || STATICS[t.replace(/^io\./, '')] || STATICS['io.' + t] || STATICS['text.' + t];
  if (!table) throw new PoshError(`Unable to find type [${typeName}].`, { category: 'InvalidOperation' });
  const k = Object.keys(Object.getOwnPropertyDescriptors(table)).find(x => x.toLowerCase() === member.toLowerCase());
  if (k === undefined) throw new PoshError(`[${typeName}] does not contain a member named '${member}'.`, { category: 'InvalidOperation' });
  return table[k];
}

// ---------- formatting: .NET composite format strings and date formats ----------
const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
const STD_DATE = {
  d: 'M/d/yyyy', D: 'dddd, MMMM d, yyyy', t: 'h:mm tt', T: 'h:mm:ss tt', g: 'M/d/yyyy h:mm tt', G: 'M/d/yyyy h:mm:ss tt',
  f: 'dddd, MMMM d, yyyy h:mm tt', F: 'dddd, MMMM d, yyyy h:mm:ss tt', s: "yyyy-MM-dd'T'HH:mm:ss", o: "yyyy-MM-dd'T'HH:mm:ss.fffffffK",
  O: "yyyy-MM-dd'T'HH:mm:ss.fffffffK", M: 'MMMM d', m: 'MMMM d', Y: 'MMMM yyyy', y: 'MMMM yyyy',
};

function fmtDate(d, f) {
  if (f === 'u') { const u = new Date(d.getTime() + d.getTimezoneOffset() * 6e4); return fmtDate(u, "yyyy-MM-dd HH:mm:ss'Z'"); }
  if (f === 'R' || f === 'r') return d.toUTCString();
  f = STD_DATE[f] || f;
  const p = (n, w = 2) => String(n).padStart(w, '0');
  const off = () => { const o = -d.getTimezoneOffset(); return (o >= 0 ? '+' : '-') + p(Math.floor(Math.abs(o) / 60)) + ':' + p(Math.abs(o) % 60); };
  return f.replace(/dddd|ddd|dd|d|MMMM|MMM|MM|M|yyyy|yy|HH|H|hh|h|mm|m|ss|s|fffffff|ffffff|fffff|ffff|fff|ff|f|tt|K|zzz|zz|'[^']*'|"[^"]*"|\\./g, t => {
    switch (t) {
      case 'dddd': return DAYS[d.getDay()]; case 'ddd': return DAYS[d.getDay()].slice(0, 3);
      case 'dd': return p(d.getDate()); case 'd': return String(d.getDate());
      case 'MMMM': return MONTHS[d.getMonth()]; case 'MMM': return MONTHS[d.getMonth()].slice(0, 3);
      case 'MM': return p(d.getMonth() + 1); case 'M': return String(d.getMonth() + 1);
      case 'yyyy': return p(d.getFullYear(), 4); case 'yy': return p(d.getFullYear() % 100);
      case 'HH': return p(d.getHours()); case 'H': return String(d.getHours());
      case 'hh': return p(d.getHours() % 12 || 12); case 'h': return String(d.getHours() % 12 || 12);
      case 'mm': return p(d.getMinutes()); case 'm': return String(d.getMinutes());
      case 'ss': return p(d.getSeconds()); case 's': return String(d.getSeconds());
      case 'tt': return d.getHours() < 12 ? 'AM' : 'PM';
      case 'K': case 'zzz': return off(); case 'zz': return off().slice(0, 3);
    }
    if (t[0] === 'f') return p(d.getMilliseconds(), 3).padEnd(t.length, '0').slice(0, t.length);
    if (t[0] === "'" || t[0] === '"') return t.slice(1, -1);
    return t.slice(1);
  });
}

function groupThousands(intPart) { return intPart.replace(/\B(?=(\d{3})+(?!\d))/g, ','); }

function formatNumber(n, f) {
  const m = /^([CDEFGNPXcdefgnpx])(\d*)$/.exec(f);
  if (m) {
    const prec = m[2] === '' ? null : Number(m[2]);
    switch (m[1].toUpperCase()) {
      case 'N': { const s = Math.abs(n).toFixed(prec ?? 2); const [i, d] = s.split('.'); return (n < 0 ? '-' : '') + groupThousands(i) + (d ? '.' + d : ''); }
      case 'F': return n.toFixed(prec ?? 2);
      case 'D': return (n < 0 ? '-' : '') + String(Math.abs(Math.trunc(n))).padStart(prec ?? 0, '0');
      case 'X': { const s = (n >>> 0).toString(16).padStart(prec ?? 0, '0'); return m[1] === 'X' ? s.toUpperCase() : s; }
      case 'P': return formatNumber(n * 100, 'N' + (prec ?? 2)) + ' %';
      case 'C': return (n < 0 ? '-$' : '$') + formatNumber(Math.abs(n), 'N' + (prec ?? 2));
      case 'E': return n.toExponential(prec ?? 6).replace(/e([+-])(\d+)$/, (_, s, e) => (m[1] === 'E' ? 'E' : 'e') + s + e.padStart(3, '0'));
      case 'G': return prec ? String(parseFloat(n.toPrecision(prec))) : numStr(n);
    }
  }
  // custom formats like 0.00, #,##0.0, 000
  const cm = /^([#0,]*)(?:\.([#0]+))?$/.exec(f);
  if (cm && f) {
    const dec = cm[2] || '', minDec = (dec.match(/0/g) || []).length;
    let s = Math.abs(n).toFixed(dec.length);
    let [i, d = ''] = s.split('.');
    while (d.length > minDec && d.endsWith('0')) d = d.slice(0, -1);
    const minInt = (cm[1].match(/0/g) || []).length;
    i = i.padStart(minInt, '0');
    if (minInt === 0 && i === '0' && cm[1]) i = '';
    if (cm[1].includes(',')) i = groupThousands(i);
    return (n < 0 ? '-' : '') + i + (d ? '.' + d : '');
  }
  return numStr(n);
}

function formatValue(v, f) {
  if (!f) return toStr(v);
  if (v instanceof Date) return fmtDate(v, f);
  if (typeof v === 'number') return formatNumber(v, f);
  return toStr(v);
}

function formatComposite(fmt, args) {
  return fmt.replace(/\{\{|\}\}|\{(\d+)(?:,(-?\d+))?(?::([^}]*))?\}/g, (m, idx, align, f) => {
    if (m === '{{') return '{';
    if (m === '}}') return '}';
    const i = Number(idx);
    if (i >= args.length) throw new PoshError('Error formatting a string: Index (zero based) must be greater than or equal to zero and less than the size of the argument list.');
    let s = formatValue(args[i], f);
    if (align) { const w = Number(align); s = w < 0 ? s.padEnd(-w) : s.padStart(w); }
    return s;
  });
}

const stripAnsi = s => s.replace(/\x1b\[[0-9;?]*[ -\/]*[@-~]|\x1b\][^\x07\x1b]*(\x07|\x1b\\)/g, '');

module.exports = {
  TYPE, PoshError, ErrorRecord, ScriptBlock, TypeRef, obj, hashtable, isHash, isObj, typeOf,
  toBool, toNum, tryNum, toStr, toDate, numStr, parseNumber, roundEven, cast, isType, normType,
  makeRegex, wildcardRe, hasWildcard, eq, compare, binaryOp, range, findKey, getMember, setMember,
  index, setIndex, callMethod, staticMember, timeSpan, fmtDate, formatComposite, formatValue, stripAnsi,
};
