'use strict';
// posh self-check: node posh/test.js   (exits non-zero on the first failure)
const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { Interp, unwrap } = require('./interp');
const { formatOutput } = require('./format');
const { highlight, parseKeys } = require('./editor');

let written = '';
const host = { color: false, write: s => { written += s; }, writeErr: s => { written += s; }, columns: () => 120, readLine: async () => 'typed' };
const interp = new Interp(host);

async function ev(src) {
  const body = interp.parse(src);
  const out = [];
  await interp.execBlock(body.end, interp.global, out);
  return unwrap(out);
}
async function text(src) { written = ''; await interp.runTop(src); return written; }

const cases = [
  ['1 + 2 * 3', 7],
  ['(1 + 2) * 3', 9],
  ['10 / 4', 2.5],
  ['7 % 3', 1],
  ['"a" + 1', 'a1'],
  ['"ab" * 3', 'ababab'],
  ['$x = 5; $x += 2; $x', 7],
  ['$s = "hi"; "$s there $(1+1)"', 'hi there 2'],
  ["'no $expand'", 'no $expand'],
  ['"tab`there"', 'tab\there'],
  ['@(1,2,3).Count', 3],
  ['(1..5).Count', 5],
  ['1..3', [1, 2, 3]],
  ['5..3', [5, 4, 3]],
  ["'a'..'c'", ['a', 'b', 'c']],
  ['$a = 1,2,3; $a[1]', 2],
  ['$a = 1,2,3; $a[-1]', 3],
  ['$h = @{ Name = "x"; Size = 3 }; $h.Name', 'x'],
  ['$h = @{ a = 1 }; $h["A"]', 1],
  ['$h = @{}; $h.b = 2; $h.Count', 1],
  ['$o = [pscustomobject]@{ A = 1; B = 2 }; $o.B', 2],
  ['"abc" -eq "ABC"', true],
  ['"abc" -ceq "ABC"', false],
  ['5 -gt 3', true],
  ['"10" -gt 9', false],
  ['1,2,3,4 -gt 2', [3, 4]],
  ['"hello.txt" -like "*.txt"', true],
  ['"abc123" -match "\\d+"; $Matches[0]', [true, '123']],
  ['"a-b-c" -replace "-", "+"', 'a+b+c'],
  ['"John Smith" -replace "(\\w+) (\\w+)", \'$2, $1\'', 'Smith, John'],
  ['"a,b,c" -split ","', ['a', 'b', 'c']],
  ['(1,2,3) -join "-"', '1-2-3'],
  ['-join ("a","b")', 'ab'],
  ['1,2,3 -contains 2', true],
  ['2 -in 1,2,3', true],
  ['$true -and $false', false],
  ['-not $false', true],
  ['!$true', false],
  ['"{0} is {1:N2}" -f "pi", 3.14159', 'pi is 3.14'],
  ['"{0,5}|" -f 42', '   42|'],
  ['[int]"42" + 1', 43],
  ['[int]2.5', 2],
  ['[int]3.5', 4],
  ['[string]42', '42'],
  ['5 -is [int]', true],
  ['"5" -as [int]', 5],
  ['[math]::Round(2.567, 2)', 2.57],
  ['[math]::Max(3, 9)', 9],
  ['[string]::IsNullOrEmpty("")', true],
  ['"Hello".ToUpper()', 'HELLO'],
  ['"  pad ".Trim()', 'pad'],
  ['"a.b.c".Split(".")', ['a', 'b', 'c']],
  ['"hello".Substring(1, 3)', 'ell'],
  ['"hello".Length', 5],
  ['"hello".Replace("l", "L")', 'heLLo'],
  ['if (1 -eq 1) { "y" } else { "n" }', 'y'],
  ['if ($false) { 1 } elseif ($true) { 2 } else { 3 }', 2],
  ['$r = foreach ($i in 1..3) { $i * 2 }; $r', [2, 4, 6]],
  ['$t = 0; for ($i = 0; $i -lt 5; $i++) { $t += $i }; $t', 10],
  ['$i = 0; while ($i -lt 3) { $i++ }; $i', 3],
  ['$i = 0; do { $i++ } until ($i -ge 4); $i', 4],
  ['foreach ($i in 1..10) { if ($i -eq 3) { break }; $i }', [1, 2]],
  ['foreach ($i in 1..4) { if ($i % 2) { continue }; $i }', [2, 4]],
  ['switch (2) { 1 { "one" } 2 { "two" } default { "other" } }', 'two'],
  ['switch ("abc") { "x" { 1 } default { "d" } }', 'd'],
  ['switch -wildcard ("file.txt") { "*.txt" { "text" } }', 'text'],
  ['switch -regex ("v12") { "^v(\\d+)" { $Matches[1] } }', '12'],
  ['switch (1,2,3) { { $_ -gt 1 } { $_ } }', [2, 3]],
  ['function Add($a, $b = 10) { $a + $b }; Add 1', 11],
  ['function Add($a, $b) { $a + $b }; Add -b 2 -a 3', 5],
  ['function F { param([switch]$Loud, $Name) if ($Loud) { $Name.ToUpper() } else { $Name } }; F -Loud bob', 'BOB'],
  ['function G { "a"; return "b"; "c" }; G', ['a', 'b']],
  ['function Dbl { process { $_ * 2 } }; 1,2,3 | Dbl', [2, 4, 6]],
  ['function Sum { $t = 0; foreach ($x in $input) { $t += $x }; $t }; 1,2,3 | Sum', 6],
  ['function P { param([Parameter(ValueFromPipeline)]$V) process { "<$V>" } }; "a","b" | P', ['<a>', '<b>']],
  ['function Cnt { $args.Count }; Cnt a b c', 3],
  ['$sb = { param($x) $x + 1 }; & $sb 41', 42],
  ['$sb = { $args[0] * 2 }; $sb.Invoke(21)', 42],
  ['try { throw "boom" } catch { "caught: $_" }', 'caught: boom'],
  ['try { 1/0 } catch { $_.Exception.Message }', 'Attempted to divide by zero.'],
  ['$f = 0; try { "x" } finally { $f = 1 }; $f', ['x', 1]],
  ['1..5 | Where-Object { $_ -gt 3 }', [4, 5]],
  ['1..3 | ForEach-Object { $_ * 10 }', [10, 20, 30]],
  ['1..3 | % { $_ + 1 }', [2, 3, 4]],
  ['$items = @([pscustomobject]@{N="b";V=2}, [pscustomobject]@{N="a";V=1}); ($items | Sort-Object N)[0].V', 1],
  ['3,1,2 | Sort-Object', [1, 2, 3]],
  ['3,1,2 | Sort-Object -Descending', [3, 2, 1]],
  ['1..10 | Select-Object -First 3', [1, 2, 3]],
  ['1..10 | Select-Object -Last 2', [9, 10]],
  ['1,1,2,3,3 | Select-Object -Unique', [1, 2, 3]],
  ['(1..10 | Measure-Object -Sum).Sum', 55],
  ['(1..4 | Measure-Object -Average).Average', 2.5],
  ['("a","b","a" | Group-Object | Where-Object Name -eq "a").Count', 2],
  ['$p = [pscustomobject]@{ Name = "x"; Size = 5 }; ($p | Select-Object Name).Size', null],
  ['$p = [pscustomobject]@{ Name = "x"; Size = 5 }; $p | Select-Object -ExpandProperty Size', 5],
  ['@{a=1;b=2} | ConvertTo-Json -Compress', '{"a":1,"b":2}'],
  ['("{""x"":[1,2]}" | ConvertFrom-Json).x', [1, 2]],
  ['("a,b`n1,2" | ConvertFrom-Csv).b', '2'],
  ['$x = 1; $x++; $x', 2],
  ['$list = @(); $list += 1; $list += 2; $list.Count', 2],
  ['$a = @(1); $a.Count', 1],
  ['@(1,2,3).Where({ $_ -ge 2 })', [2, 3]],
  ['@(1,2).ForEach({ $_ * 3 })', [3, 6]],
  ['Write-Output 1 2 3', [1, 2, 3]],
  ['$null -eq $null', true],
  ['"" -eq $null', false],
  ['$e = $env:POSH_TEST_X; $env:POSH_TEST_X = "v"; $env:POSH_TEST_X', 'v'],
  ['(Get-Date -Year 2020 -Month 2 -Day 3).ToString("yyyy-MM-dd")', '2020-02-03'],
  ['Get-Date -Date "2021-03-04T05:06:07" -Format "dd/MM/yyyy HH:mm"', '04/03/2021 05:06'],
  ['$h = @{ b = 2; a = 1 }; ($h.GetEnumerator() | Sort-Object Value | Select-Object -First 1).Key', 'a'],
  ['$x = if ($true) { "yes" } else { "no" }; $x', 'yes'],
  ['"x" * 0', ''],
  ['[char]65', 'A'],
  ['0x10 + 1kb', 1040],
  ['"$(Get-Item .)" -ne ""', true],
  ['$sum = 0; 1..4 | ForEach-Object { $sum += $_ }; $sum', 10],
  ['Invoke-Expression "2 + 3"', 5],
  ['(Measure-Command { 1 }).TotalMilliseconds -ge 0', true],
];

const errorCases = [
  ['Get-NoSuchThing', /not recognized/],
  ['throw "custom"', /custom/],
  ['$null.Foo()', /null-valued/],
  ['Get-ChildItem -NoSuchParam', /parameter cannot be found/],
  ['function F { param([ValidateSet("a","b")]$X) $X }; F c', /does not belong to the set/],
];

(async () => {
  let pass = 0;
  for (const [src, want] of cases) {
    let got;
    try { got = await ev(src); }
    catch (e) { console.error(`FAIL (threw) ${src}\n  ${e.stack || e.message}`); process.exit(1); }
    try { assert.deepStrictEqual(got, want); pass++; }
    catch { console.error(`FAIL ${src}\n  want ${JSON.stringify(want)}\n  got  ${JSON.stringify(got)}`); process.exit(1); }
  }
  for (const [src, re] of errorCases) {
    const out = await text(src);
    if (!re.test(out)) { console.error(`FAIL error case ${src}\n  got: ${out}`); process.exit(1); }
    pass++;
  }

  // file-system cmdlets in a temp dir
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'posh-test-'));
  const prev = process.cwd();
  process.chdir(dir);
  try {
    await ev('Set-Content -Path a.txt -Value "one","two","three"');
    assert.deepStrictEqual(await ev('Get-Content a.txt'), ['one', 'two', 'three']);
    await ev('Add-Content a.txt "four"');
    assert.strictEqual(await ev('(Get-Content a.txt).Count'), 4);
    assert.strictEqual(await ev('(Get-Content a.txt -Tail 1)'), 'four');
    await ev('New-Item -ItemType Directory sub | Out-Null; Copy-Item a.txt sub');
    assert.strictEqual(await ev('Test-Path sub/a.txt'), true);
    assert.deepStrictEqual(await ev('(Get-ChildItem -Recurse -File).Name'), ['a.txt', 'a.txt']);
    assert.strictEqual(await ev('(Select-String -Path a.txt -Pattern "^t").Count'), 2);
    await ev('Move-Item sub/a.txt sub/b.txt; Rename-Item sub/b.txt c.txt');
    assert.strictEqual(await ev('Test-Path sub/c.txt'), true);
    await ev('Remove-Item sub -Recurse');
    assert.strictEqual(await ev('Test-Path sub'), false);
    await ev('"redirected" > out.txt; "more" >> out.txt');
    assert.deepStrictEqual(await ev('Get-Content out.txt'), ['redirected', 'more']);
    await ev('[pscustomobject]@{A=1;B="x"},[pscustomobject]@{A=2;B="y"} | Export-Csv data.csv');
    assert.deepStrictEqual(await ev('(Import-Csv data.csv).B'), ['x', 'y']);
    assert.strictEqual(await ev('Split-Path (Join-Path "a" "b.txt") -Leaf'), 'b.txt');
    assert.strictEqual(await ev('(Get-FileHash a.txt -Algorithm MD5).Hash.Length'), 32);
    fs.writeFileSync('s.posh', 'param($Name = "world", [switch]$Shout)\n$m = "hello $Name"\nif ($Shout) { $m.ToUpper() } else { $m }\n');
    assert.strictEqual(await ev('./s.posh -Name you'), 'hello you');
    assert.strictEqual(await ev('./s.posh -Shout'), 'HELLO WORLD');
    // externals
    const node = JSON.stringify(process.execPath);
    assert.strictEqual(await ev(`& ${node} -e "console.log(6*7)"`), '42');
    await ev(`& ${node} -e "process.exit(3)"`);
    assert.strictEqual(await ev('$LASTEXITCODE'), 3);
    assert.strictEqual(await ev(`"piped" | & ${node} -e "process.stdin.pipe(process.stdout)"`), 'piped');
    pass += 18;
  } finally { process.chdir(prev); fs.rmSync(dir, { recursive: true, force: true }); }

  // formatting
  const t = formatOutput([{ Name: 'a', Size: 1 }, { Name: 'bb', Size: 22 }], 80, false);
  assert.ok(/Name Size\n---- ----\na       1\nbb     22/.test(t), t);
  const l = formatOutput([{ A: 1, B: 2, C: 3, D: 4, E: 5 }], 80, false);
  assert.ok(/A : 1\nB : 2/.test(l), l);
  assert.ok(/Name +Value/.test(await text('@{ k = "v" }')));
  pass += 3;

  // incomplete-input detection drives multi-line editing
  assert.strictEqual(interp.isIncomplete('if ($x) {'), true);
  assert.strictEqual(interp.isIncomplete('"open string'), true);
  assert.strictEqual(interp.isIncomplete('Get-ChildItem |'), true);
  assert.strictEqual(interp.isIncomplete('Get-ChildItem'), false);
  assert.strictEqual(interp.isIncomplete('1 +)'), false);
  pass += 5;

  // highlighter keeps text intact
  const hl = highlight('Get-ChildItem -Path "x" | % { $_.Name } # hi', n => n === 'Get-ChildItem' || n === '%');
  assert.strictEqual(hl.replace(/\x1b\[[0-9;]*m/g, ''), 'Get-ChildItem -Path "x" | % { $_.Name } # hi');
  assert.ok(hl.includes('\x1b[93mGet-ChildItem'));
  pass += 2;

  // key decoding: Meta+Enter (new line) and Meta+Backspace (delete word) arrive as ESC CR / ESC DEL
  assert.deepStrictEqual([...parseKeys('\x1b\r\x1b\x7f\x1bb\x1b[1;5D')], ['alt-enter', 'alt-backspace', 'alt-b', 'ctrl-left']);
  pass += 1;

  console.log(`posh self-check: ${pass} checks passed`);
})().catch(e => { console.error(e); process.exit(1); });
