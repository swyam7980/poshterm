'use strict';
// Turns pipeline output into text: tables for <=4 properties, lists otherwise,
// plus fixed views for well-known object types (files, processes, ...).
const path = require('path');
const { TYPE, toStr, isHash, isObj, fmtDate, numStr, stripAnsi, ScriptBlock, ErrorRecord, TypeRef } = require('./values');

const ESC = '\x1b[';
const HDR = ESC + '32;1m', RST = ESC + '0m';
const EXE_EXT = new Set(['.exe', '.cmd', '.bat', '.com', '.ps1', '.posh', '.msi', '.sh']);
const ARCHIVE_EXT = new Set(['.zip', '.tar', '.gz', '.tgz', '.7z', '.rar', '.xz', '.bz2']);

const shortDate = d => d instanceof Date ? `${fmtDate(d, 'M/d/yyyy').padStart(10)} ${fmtDate(d, 'h:mm tt').padStart(8)}` : '';
const mb = n => (n / 1048576).toFixed(2);

// Column views per object type: [label, getter, align]
const VIEWS = {
  Process: [['WS(M)', o => mb(o.WorkingSet || 0), 'r'], ['Id', o => o.Id, 'r'], ['SI', o => o.SessionId ?? '', 'r'], ['ProcessName', o => o.ProcessName]],
  GroupInfo: [['Count', o => o.Count, 'r'], ['Name', o => o.Name], ['Group', o => o.Group]],
  CommandInfo: [['CommandType', o => o.CommandType], ['Name', o => o.Name], ['Source', o => o.Source]],
  HistoryInfo: [['Id', o => o.Id, 'r'], ['Duration', o => o.Duration], ['CommandLine', o => o.CommandLine]],
  AliasInfo: [['CommandType', () => 'Alias'], ['Name', o => `${o.Name} -> ${o.Definition}`]],
  Variable: [['Name', o => o.Name], ['Value', o => o.Value]],
  MemberDefinition: [['Name', o => o.Name], ['MemberType', o => o.MemberType], ['Definition', o => o.Definition]],
  PathInfo: [['Path', o => o.Path]],
  FileHash: [['Algorithm', o => o.Algorithm], ['Hash', o => o.Hash], ['Path', o => o.Path]],
  DictionaryEntry: [['Name', o => o.Key], ['Value', o => o.Value]],
  EnvEntry: [['Name', o => o.Name], ['Value', o => o.Value]],
  DriveInfo: [['Name', o => o.Name], ['Used (GB)', o => o.Used, 'r'], ['Free (GB)', o => o.Free, 'r'], ['Root', o => o.Root]],
};
const LIST_TYPES = new Set(['TimeSpan', 'WebResponse', 'GenericMeasureInfo', 'Exception', 'OperatingSystem']);

function cell(v) {
  if (v === null || v === undefined) return '';
  if (Array.isArray(v)) return '{' + v.slice(0, 4).map(cell).join(', ') + (v.length > 4 ? '…' : '') + '}';
  if (v instanceof Date) return fmtDate(v, 'M/d/yyyy h:mm:ss tt');
  if (typeof v === 'number') return numStr(v);
  return toStr(v).replace(/\r?\n/g, ' ');
}

const visLen = s => stripAnsi(s).length;
function fit(s, w) {
  const plain = stripAnsi(s);
  if (plain.length <= w) return s;
  if (plain.length !== s.length) s = plain;
  return w <= 1 ? s.slice(0, w) : s.slice(0, w - 1) + '…';
}
const pad = (s, w, right) => { const n = w - visLen(s); return n <= 0 ? s : right ? ' '.repeat(n) + s : s + ' '.repeat(n); };

function renderTable(cols, rows, width, color, { hideHeaders = false } = {}) {
  const data = rows.map(r => cols.map(c => c.color ? c.color(r, cell(c.get(r))) : cell(c.get(r))));
  const widths = cols.map((c, i) => Math.max(c.label.length, ...data.map(d => visLen(d[i])), 1));
  const total = () => widths.reduce((a, b) => a + b, 0) + widths.length - 1;
  while (total() > width - 1) {
    const i = widths.indexOf(Math.max(...widths));
    if (widths[i] <= 6) break;
    widths[i]--;
  }
  const line = (cells, isHdr) => cells.map((s, i) => {
    const last = i === cells.length - 1;
    const right = cols[i].align === 'r';
    let t = fit(s, widths[i]);
    t = last && !right ? t : pad(t, widths[i], right);
    return isHdr && color ? HDR + t + RST : t;
  }).join(' ').replace(/\s+$/, '');
  const out = [''];
  if (!hideHeaders) {
    out.push(line(cols.map(c => c.label), true));
    out.push(line(cols.map((c, i) => '-'.repeat(Math.min(c.label.length, widths[i]))), true));
  }
  for (const d of data) out.push(line(d, false));
  out.push('');
  return out;
}

function renderList(objs, color, props) {
  const out = [''];
  for (const o of objs) {
    const keys = props || Object.keys(o);
    const w = Math.max(...keys.map(k => k.length), 0);
    for (const k of keys) {
      const label = k.padEnd(w) + ' : ';
      const v = o[k];
      const text = v !== null && typeof v === 'object' && !Array.isArray(v) && !(v instanceof Date) ? toStr(v) : cell(v);
      const lines = String(text).split('\n');
      out.push((color ? HDR + label + RST : label) + lines[0]);
      for (const l of lines.slice(1)) out.push(' '.repeat(label.length) + l);
    }
    out.push('');
  }
  return out;
}

function fileCols(color) {
  return [
    { label: 'Mode', get: o => o.Mode },
    { label: 'LastWriteTime', get: o => shortDate(o.LastWriteTime), align: 'r' },
    { label: 'Length', get: o => o.Length, align: 'r' },
    {
      label: 'Name', get: o => o.Name + (o.LinkTarget ? ' -> ' + o.LinkTarget : ''), color: color ? (o, s) => {
        if (o.PSIsContainer) return ESC + '1;34m' + s + RST;
        const ext = (o.Extension || '').toLowerCase();
        if (EXE_EXT.has(ext)) return ESC + '1;32m' + s + RST;
        if (ARCHIVE_EXT.has(ext)) return ESC + '1;31m' + s + RST;
        return s;
      } : null,
    },
  ];
}

function viewKey(v) {
  const t = v[TYPE];
  if (t === 'FileInfo' || t === 'DirectoryInfo') return 'FileSystem';
  return t || 'PSCustomObject';
}

function renderGroup(items, width, color, key) {
  if (key === 'FileSystem') {
    const out = [];
    let i = 0;
    while (i < items.length) {
      const dir = items[i].PSParentPath;
      let j = i;
      while (j < items.length && items[j].PSParentPath === dir) j++;
      out.push('', `    Directory: ${dir}`);
      out.push(...renderTable(fileCols(color), items.slice(i, j), width, color));
      i = j;
    }
    return out;
  }
  if (key === 'Hashtable') {
    const rows = items.flatMap(h => Object.entries(h).map(([Name, Value]) => ({ Name, Value })));
    return renderTable([{ label: 'Name', get: r => r.Name }, { label: 'Value', get: r => r.Value }], rows, width, color);
  }
  if (key === 'MatchInfo') {
    return items.map(m => {
      const line = color && m.Matches && m.Matches.length
        ? m.Line.replace(m.Pattern, s => ESC + '7m' + s + RST) : m.Line;
      return m.Path && m.Path !== 'InputStream' ? `${m.RelativePath || m.Path}:${m.LineNumber}:${line}` : line;
    });
  }
  if (VIEWS[key]) return renderTable(VIEWS[key].map(([label, get, align]) => ({ label, get, align })), items, width, color);
  if (LIST_TYPES.has(key)) return renderList(items, color);
  const props = Object.keys(items[0]);
  if (props.length === 0) return [];
  if (props.length > 4) return renderList(items, color, null);
  return renderTable(props.map(p => ({ label: p, get: o => o[p], align: typeof items[0][p] === 'number' ? 'r' : undefined })), items, width, color);
}

function scalarLines(v, color) {
  if (v === null || v === undefined) return [];
  if (typeof v === 'string') return v.split(/\r?\n/);
  if (v instanceof Date) return [fmtDate(v, 'dddd, MMMM d, yyyy h:mm:ss tt')];
  if (v instanceof ErrorRecord) { const m = (v.command ? v.command + ': ' : '') + v.Exception.Message; return [color ? ESC + '31;1m' + m + RST : m]; }
  if (v instanceof ScriptBlock) return v.text.split('\n');
  if (v instanceof TypeRef) return [v.Name];
  if (Array.isArray(v)) return v.flatMap(x => scalarLines(x, color));
  return [toStr(v)];
}

// "Table-able" objects: plain objects that aren't special scalar-like types
const isRecord = v => isObj(v) && !(v instanceof ScriptBlock) && !(v instanceof ErrorRecord) && !(v instanceof TypeRef) && v[TYPE] !== 'Guid';

function formatOutput(values, width = 120, color = true) {
  const lines = [];
  let i = 0;
  while (i < values.length) {
    const v = values[i];
    if (!isRecord(v)) { lines.push(...scalarLines(v, color)); i++; continue; }
    const key = viewKey(v);
    let j = i + 1;
    while (j < values.length && isRecord(values[j]) && viewKey(values[j]) === key) j++;
    lines.push(...renderGroup(values.slice(i, j), width, color, key));
    i = j;
  }
  // collapse runs of blank lines like PowerShell does between tables
  const out = lines.filter((l, k) => !(l === '' && lines[k - 1] === ''));
  return out.length ? out.join('\n') + '\n' : '';
}

module.exports = { formatOutput, renderTable, renderList, cell, fileCols };
