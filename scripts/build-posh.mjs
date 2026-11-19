// Builds posh.exe: the posh shell bundled into one standalone Windows executable, via Node's
// built-in single-executable application (SEA) support. No loose .js files ship in the result.
//   bundle (esbuild) -> minify -> Node SEA blob -> inject into a copy of node.exe (postject)
// Run with Windows node:  node scripts/build-posh.mjs [outDir]      (default outDir: dist/posh)
// Needs Node 20+ (22 recommended): the posh.exe produced is a copy of the node.exe that runs this script.
import { execFileSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const out = resolve(process.argv[2] || join(root, 'dist', 'posh'));
const work = join(root, 'build', 'posh');
const isWin = process.platform === 'win32';
mkdirSync(out, { recursive: true });

// Dependencies (esbuild, postject) live in build/posh/node_modules. scripts/build.ps1 installs them;
// to do it by hand:  npm install --prefix build/posh esbuild@0.21.5 postject@1.0.0-alpha.6
if (!existsSync(join(work, 'node_modules', 'esbuild')) || !existsSync(join(work, 'node_modules', 'postject'))) {
  console.error(`Missing build tools in ${work}.
Run: npm install --prefix "${work}" esbuild@0.21.5 postject@1.0.0-alpha.6`);
  process.exit(1);
}

console.log('> bundling and minifying');
const esbuild = await import(pathToFileURL(join(work, 'node_modules', 'esbuild', 'lib', 'main.js')));
await esbuild.build({
  entryPoints: [join(root, 'posh', 'posh.js')], bundle: true, platform: 'node', target: 'node22', format: 'cjs',
  minify: true, legalComments: 'none', outfile: join(work, 'bundle.js'),
});

console.log('> creating single-executable blob');
writeFileSync(join(work, 'sea-config.json'), JSON.stringify({ main: 'bundle.js', output: 'sea-prep.blob', disableExperimentalSEAWarning: true, useCodeCache: false }));
execFileSync(process.execPath, ['--experimental-sea-config', 'sea-config.json'], { stdio: 'inherit', cwd: work });

console.log('> embedding the blob into a copy of node.exe');
const exe = join(out, isWin ? 'posh.exe' : 'posh');
copyFileSync(process.execPath, exe);
const postject = join(work, 'node_modules', 'postject', 'dist', 'cli.js');
execFileSync(process.execPath, [postject, exe, 'NODE_SEA_BLOB', join(work, 'sea-prep.blob'), '--sentinel-fuse', 'NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2'], { stdio: 'inherit', cwd: work });

console.log(`> done: ${exe} (${(statSync(exe).size / 1048576).toFixed(0)} MB)`);
