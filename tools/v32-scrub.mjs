// v0.35 第二笔：这一支从 gitignored 的 obj/ 搬进 tools/ 并入库（欠账"量具进版本库"的那一步）。
// v0.32 卫生工具：把日志里漏出来的明文口令换成 ***，并回报动了哪几份。
// v0.38 第四笔：三处改动，每一处都是 §14 那一格记着的欠账——
//   ① 前缀与目录参数化。原样把扫描集硬钉在 `obj/logs` 的 `v32-*` 上，而 v0.33 起每批各换前缀，
//      于是它连续几笔扫的是空目录，"含明文口令 0 份"是空集上的真话（欠账原句：scrub 的前缀参数化）。
//      今天默认扫 `obj/logs` ∶ `docs/evidence` 两处、前缀认 `vNN-` 一族。
//   ② 判据从"只看形状"扩成"形状 + 原值"：从仓库根那几份 gitignored 连接文件里取出口令本身再去比，
//      这是 v0.37 那一次按值复查才抓到两种形状的同一颗尺；输出只到文件名与样式名，绝不回显值。
//   ③ `--fix` 从此拒绝长期位：改写归档件就是伪造证人，而台账引的是它的字节与行号。
//      源头的正确走法是：在源那一侧打码后重落一场，再 `node tools/evidence.mjs --archive --force` 覆盖归档件。
// 跑法：node tools/v32-scrub.mjs                       只报告，不改（默认两目录全族）
//       node tools/v32-scrub.mjs --dir docs/evidence   只扫某目录（可重复给）
//       node tools/v32-scrub.mjs --prefix v38          只看某一批
//       node tools/v32-scrub.mjs --fix                 改写（只动 scratch，长期位一律拒绝）
// 判据同 §13.17「口令不落日志」那一句：赋值那一形的值必须是打码位、URI 里也一样。
import fs from 'node:fs';
import path from 'node:path';

const argv = process.argv.slice(2);
const fix = argv.includes('--fix');
const optAll = flag => argv.reduce((a, x, i) => (x === flag && argv[i + 1] ? a.concat(argv[i + 1]) : a), []);
const dirs = optAll('--dir').length ? optAll('--dir') : ['obj/logs', 'docs/evidence'];
const prefix = optAll('--prefix')[0] || /^v\d\d-/;
const matchName = f => (typeof prefix === 'string' ? f.startsWith(prefix) : prefix.test(f)) && /\.(log|txt)$/.test(f);

const leaky = t => /(Password=)(?!\*\*\*)[^;\s"']/.test(t) || /:\/\/[^/\s:@]*:(?!\*\*\*)[^@\s]+@/.test(t);
const scrub = t => t.replace(/(Password=)(?!\*\*\*)[^;\s"']+/gi, "$1***").replace(/(\/\/[^/\s:@]*:)(?!\*\*\*)[^@\s]+@/g, "$1***@");

// 原值只在本进程内存里比对，不落任何输出
const secretFiles = fs.readdirSync('.').filter(f => /^quicksql-test.*connection.*\.local\.txt$/.test(f));
const secrets = [];
for (const f of secretFiles) {
  const t = fs.readFileSync(f, 'latin1');
  for (const m of t.matchAll(/(?:Password|pwd)\s*=\s*([^;\r\n"'`]+)/gi)) {
    const v = m[1].trim();
    if (v && v !== '***') secrets.push({ from: f, needle: Buffer.from(v, 'latin1') });
  }
}

const LONG_TERM = 'docs/evidence';
let scanned = 0, dirty = 0, fixed = 0, refused = 0, locked = 0;
for (const dir of dirs) {
  if (!fs.existsSync(dir)) { console.log(`  （目录不在场，跳过：${dir}）`); continue; }
  for (const f of fs.readdirSync(dir).filter(matchName)) {
    const p = path.join(dir, f);
    let buf;
    try { buf = fs.readFileSync(p); }
    catch (e) { if (e && (e.code === 'EBUSY' || e.code === 'EPERM' || e.code === 'EMFILE')) { locked++; console.log(`  LOCKED ${p}`); continue; } throw e; }
    const text = buf.toString('latin1');
    const byShape = leaky(text);
    const byValue = secrets.filter(s => buf.includes(s.needle)).map(s => s.from);
    if (!byShape && !byValue.length) { scanned++; continue; }
    scanned++; dirty++;
    console.log(`  DIRTY ${p} :: ${[byShape ? 'shape' : null, ...byValue.map(v => 'value@' + v)].filter(Boolean).join(' ')}`);
    if (!byShape) continue; // 只有原值撞字（如厂商口令等于默认模式名）时不许自动改写，那要人判
    if (dir === LONG_TERM) {
      refused++;
      console.log(`    REFUSED：长期位的件不改写（改写归档件 = 伪造证人），走法见本支头注 ③`);
      continue;
    }
    if (fix) { fs.writeFileSync(p, scrub(text), 'latin1'); fixed++; }
  }
}
console.log(`扫描 ${dirs.join(' ∶ ')} 前缀 ${typeof prefix === 'string' ? prefix : 'vNN-'}：${scanned} 份，脏 ${dirty} 份，改写 ${fixed} 份，长期位拒绝改写 ${refused} 份，被占用读不到 ${locked} 份${fix ? '' : '（未改，加 --fix 才改）'}`);
if (!scanned) { console.log('NOTHING-SCANNED：空集上的 0 不算绿灯'); process.exit(3); }
process.exit(dirty && !(fix && refused === 0) ? 5 : 0);
