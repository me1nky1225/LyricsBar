// Markdown sanity check for the upload archive: balanced code fences, table column
// counts, empty headings, and that every relative link / image target exists.
const fs = require('fs');
const path = require('path');

const root = process.argv[2];
if (!root) { console.error('usage: node _mdcheck.js <dir>'); process.exit(2); }

function mdFiles(dir, out = []) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (e.name === '.git' || e.name === 'node_modules') continue;
    const p = path.join(dir, e.name);
    if (e.isDirectory()) mdFiles(p, out);
    else if (e.name.toLowerCase().endsWith('.md')) out.push(p);
  }
  return out;
}

let problems = 0;
const files = mdFiles(root).sort();
for (const file of files) {
  const rel = path.relative(root, file).replace(/\\/g, '/');
  const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);
  let fence = 0;
  let tableCols = null;
  let tableStart = 0;
  lines.forEach((line, i) => {
    const n = i + 1;
    if (/^\s*```/.test(line)) { fence++; return; }
    if (fence % 2 === 1) return; // inside a code block
    if (/^#{1,6}\s*$/.test(line)) { console.log(`${rel}:${n}: empty heading`); problems++; }
    const isRow = /^\s*\|.*\|\s*$/.test(line);
    if (isRow) {
      const cols = line.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').length;
      if (tableCols === null) { tableCols = cols; tableStart = n; }
      else if (cols !== tableCols) {
        console.log(`${rel}:${n}: table row has ${cols} cols, header (line ${tableStart}) has ${tableCols}`);
        problems++;
      }
    } else { tableCols = null; }
  });
  if (fence % 2 !== 0) { console.log(`${rel}: unbalanced code fence (${fence} markers)`); problems++; }
  const dir = path.dirname(file);
  for (const m of fs.readFileSync(file, 'utf8').matchAll(/!?\[[^\]]*\]\(([^)\s]+)\)/g)) {
    let target = m[1].split('#')[0];
    if (!target || /^[a-z]+:/i.test(target) || target.startsWith('mailto:')) continue;
    target = decodeURIComponent(target);
    const abs = path.resolve(dir, target);
    if (!fs.existsSync(abs)) { console.log(`${rel}: broken link -> ${m[1]}`); problems++; }
  }
}
console.log(`checked ${files.length} markdown files, ${problems} problem(s)`);
process.exit(problems === 0 ? 0 : 1);
