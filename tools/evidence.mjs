// v0.38 第三笔（裁决笔）：见证物的存放位与保留期由此定死，这一支是它的常驻量具。
//
// 来历：obj/evidence-v37.mjs（全文清点）与 obj/cite-v38.mjs（只看变更记录那一行的窄清点）都是
// 一次性工具，躺在 scratch 里，而 scratch 正是这一支要治的病——v0.37 那两笔数出来的 343 颗断口，
// 成因不是笔误，是"证据住在会被复用的目录里，而保留期比账短"。本版把它们收进 tools/，并添上
// 原两支没有的两件事：① 跨目录定位（docs/evidence ∶ obj/logs ∶ obj 三处都找，并比字节）；
// ② --archive 之前先按值扫口令，脏的一颗不许进非 scratch 的位置。
//
// 跑法：
//   node tools/evidence.mjs                 清点全文，读回 distinct ∶ exist ∶ gone 与逐族分布
//   node tools/evidence.mjs --line 8        同一判据只数第 8 行（变更记录那一行，窄清点）
//   node tools/evidence.mjs --archive       把"文中引到 ∧ 盘上有"的场复制进 docs/evidence/
//   node tools/evidence.mjs --archive --force
//                                           同名两颗而字节不同时以源覆盖（默认拒绝并计入 refused——
//                                           "归档件与源不同"要人先裁哪一侧是证人：scratch 里那份
//                                           可能被同名的复跑覆盖过，姿势三记 §13.17 那一格）
//   node tools/evidence.mjs --check v38     门禁形：v38 那一族任何一颗在 docs/evidence 读不到、或长期位那一份与 scratch 同名件字节不同，就退出码 4
//   node tools/evidence.mjs --scan          复查长期位的口令（只有归档件，文档本体归 §13.17 ② 那条判据管）
//                                           只报文件名 ∶ 来源 ∶ 样式名，绝不回显值；退出码 5 表示有漏网
//
// 判据与 §13.17「口令不落日志」同一条，取两种形：形状（等号右边不是打码位、URI 里带值）与原值
// （从根目录那几份 gitignored 连接文件里取出口令本身再去比字节）。原值那一半才是这一支的重点：
// 归档把日志从 scratch 搬到长期位置，一次漏网就不再是"本机看得到"，而是"跟着仓库走"。
// 输出只到文件名 ∶ 来源文件名 ∶ 样式名，绝不回显口令值。

import fs from 'node:fs';
import path from 'node:path';

const DOC = 'docs/quicksql-orm-design.md';
const OUT = 'docs/evidence';
const DIRS = [OUT, 'obj/logs', 'obj'];
const NAME_RE = /\bv\d\d-[A-Za-z0-9._-]+\.(?:log|txt)\b/g;

const argv = process.argv.slice(2);
const has = flag => argv.includes(flag);
const opt = (flag, d) => {
  const i = argv.indexOf(flag);
  return i >= 0 && argv[i + 1] ? argv[i + 1] : d;
};

// ---------- 引用提取：整篇或单行 ----------
const lines = fs.readFileSync(DOC, 'utf8').split(/\r?\n/);
const onlyLine = Number(opt('--line', 0));
const text = onlyLine ? (lines[onlyLine - 1] ?? '') : lines.join('\n');
const cited = [...new Set(text.match(NAME_RE) ?? [])].sort();

// ---------- 口令：原值只在本进程内存里比对，不落任何输出 ----------
const secretFiles = fs.readdirSync('.').filter(f => /^quicksql-test.*connection.*\.local\.txt$/.test(f));
const secrets = [];
for (const f of secretFiles) {
  const t = fs.readFileSync(f, 'latin1');
  for (const m of t.matchAll(/(?:Password|pwd)\s*=\s*([^;\r\n"'`]+)/gi)) {
    const v = m[1].trim();
    if (v && v !== '***') secrets.push({ from: f, needle: Buffer.from(v, 'latin1') });
  }
}

// 归档位里的日志可能正被写它的那支脚本握着（run-and-log 把子进程输出重定向进同一个目录），
// Windows 上读它会 EBUSY。这不是脏、也不是干净，是第三种下落，必须报出来而不是把整场崩掉——
// 本笔第一版 --scan 就崩在这上面（现场 `docs/evidence/v38-evidence-4.log`，末行 `EXIT=1`）。
function readForScan(p) {
  try {
    return { buf: fs.readFileSync(p) };
  } catch (e) {
    if (e && (e.code === 'EBUSY' || e.code === 'EPERM' || e.code === 'EMFILE')) return { locked: true };
    throw e;
  }
}

function dirtyOf(p) {
  const r = readForScan(p);
  if (r.locked) return { locked: true, shapes: [], vals: [] };
  const t = r.buf.toString('latin1');
  const shapes = [];
  if (/(Password=)(?!\*\*\*)[^;\s"']/.test(t)) shapes.push('assign');
  if (/:\/\/[^/\s:@]*:(?!\*\*\*)[^@\s]+@/.test(t)) shapes.push('uri');
  const vals = [...new Set(secrets.filter(s => r.buf.includes(s.needle)).map(s => s.from))];
  return { shapes, vals };
}

// ---------- 定位：三处目录，字节为准 ----------
function locate(name) {
  const hits = [];
  for (const d of DIRS) {
    const p = path.join(d, name);
    if (fs.existsSync(p) && fs.statSync(p).isFile()) hits.push({ dir: d, p, size: fs.statSync(p).size });
  }
  return hits;
}

// 同名落在两处而字节不同 = 归档件不是被引的那一件，必须报出来而不是算它存在
function divergence(hits) {
  if (hits.length < 2) return null;
  const a = readForScan(hits[0].p);
  if (a.locked) return `${hits[0].dir} 被占用，比不了字节`;
  for (let i = 1; i < hits.length; i++) {
    const b = readForScan(hits[i].p);
    if (b.locked) return `${hits[i].dir} 被占用，比不了字节`;
    if (!a.buf.equals(b.buf)) return `${hits[0].dir} ∶ ${hits[i].dir} 字节不同`;
  }
  return null;
}

const rows = cited.map(name => {
  const hits = locate(name);
  return { name, hits, dir: hits.length ? hits[0].dir : null, div: divergence(hits) };
});
const exist = rows.filter(r => r.hits.length);
const gone = rows.filter(r => !r.hits.length);
const fams = [...new Set(cited.map(n => n.slice(0, 3)))].sort();

console.log(`scope=${onlyLine ? 'line ' + onlyLine : 'whole doc'} distinct=${cited.length} exist=${exist.length} gone=${gone.length} secrets_loaded=${secrets.length}`);
for (const f of fams) {
  const inFam = rows.filter(r => r.name.startsWith(f));
  const e = inFam.filter(r => r.hits.length);
  console.log(`  ${f}: cited=${String(inFam.length).padStart(3)} exist=${String(e.length).padStart(3)} gone=${String(inFam.length - e.length).padStart(3)}  in ${OUT}=${e.filter(r => r.dir === OUT).length}`);
}
const byDir = {};
for (const r of exist) byDir[r.dir] = (byDir[r.dir] ?? 0) + 1;
console.log('  resolved under: ' + (Object.entries(byDir).map(([k, v]) => `${k}=${v}`).join(' ') || '(none)'));
const div = rows.filter(r => r.div);
for (const r of div) console.log(`  DIVERGENT ${r.name} :: ${r.div}`);
if (!div.length) console.log(`  同名跨目录而字节不同的：0 颗`);

// ---------- --archive：文中引到 ∧ 盘上有 → 复制进长期位，脏的一律拒收 ----------
if (has('--archive')) {
  if (!fs.existsSync(OUT)) fs.mkdirSync(OUT, { recursive: true });
  let copied = 0, already = 0, refused = 0, replaced = 0, bytes = 0;
  const refusedList = [];
  for (const r of rows) {
    const src = r.hits.find(h => h.dir !== OUT);
    if (!src) { if (r.hits.length) already++; continue; }
    const d = dirtyOf(src.p);
    if (d.locked) {
      refused++;
      refusedList.push(`${r.name} :: 源正被占用（${src.dir}），本颗不归档`);
      continue;
    }
    if (d.shapes.length || d.vals.length) {
      refused++;
      refusedList.push(`${r.name} :: ${[...d.shapes, ...d.vals.map(v => 'value@' + v)].join(' ')}`);
      continue;
    }
    const dst = path.join(OUT, r.name);
    if (fs.existsSync(dst)) {
      const a = readForScan(dst);
      const b = readForScan(src.p);
      if (a.locked || b.locked) { refused++; refusedList.push(`${r.name} :: 比字节时任一侧被占用`); continue; }
      if (a.buf.equals(b.buf)) { already++; continue; }
      if (has('--force')) { fs.copyFileSync(src.p, dst); replaced++; bytes += src.size; continue; }
      refused++;
      refusedList.push(`${r.name} :: 已归档但与源字节不同，需人工裁（源在 scratch 被复写过；确认要以源为准时加 --force）`);
      continue;
    }
    fs.copyFileSync(src.p, dst);
    copied++;
    bytes += src.size;
  }
  console.log(`archive: copied=${copied} already=${already} replaced=${replaced} refused=${refused} new_bytes=${bytes} dir_total=${fs.readdirSync(OUT).length}${has('--force') ? ' （--force 在场：字节不同的以源覆盖）' : ''}`);
  for (const l of refusedList) console.log('  REFUSED ' + l);
}

// ---------- --scan：长期位的口令复查（归档之后再跑一次，而不是只信归档那一道闸） ----------
// 射程只有归档件，不含本文档：文档那一侧按 §13.17 ② 自己那条判据管（"命中只出现在那一行"），
// 而那条判据不许把"讲口令该打码"的散文算成漏网——原句写的就是"断言某字面 0 命中的判据不得含该字面"。
if (has('--scan')) {
  const targets = fs.readdirSync(OUT).map(f => path.join(OUT, f));
  let leaks = 0, locked = 0;
  for (const p of targets) {
    const d = dirtyOf(p);
    if (d.locked) { locked++; console.log(`LOCKED ${p} :: 正被写它的那一支握着，这一场读不到`); continue; }
    if (d.shapes.length || d.vals.length) {
      leaks++;
      console.log(`LEAK ${p} :: ${[...d.shapes, ...d.vals.map(v => 'value@' + v)].join(' ')}`);
    }
  }
  console.log(`scan: files=${targets.length} leaks=${leaks} locked=${locked}`);
  process.exit(leaks ? 5 : 0);
}

// ---------- --check：指定族的每一颗全名引用都要能在长期位打开，而且长期位那一份得就是它 ----------
if (has('--check')) {
  const prefixes = argv.filter(a => /^v\d\d$/.test(a));
  let missing = 0, diverged = 0, unverified = 0;
  for (const r of rows) {
    if (!prefixes.some(p => r.name.startsWith(p))) continue;
    if (!r.hits.some(h => h.dir === OUT)) {
      missing++;
      console.log(`MISSING ${r.name} :: ${r.hits.length ? '在盘但没归档（' + r.dir + '）' : '盘上根本没有'}`);
      continue;
    }
    // 门禁形以前只问"长期位有没有这颗名字"，于是一场新落构建把上一笔归档的同名证人盖掉时，
    // 它照样退出 0（本仓库 v0.38 第四笔当场踩到，那颗 `v38-build-1.log` 由清点报成 DIVERGENT 而门禁放行）。
    // 从今天起 DIVERGENT 也是门禁的一种红；比不了字节（被握着）仍按第三种下落算，不升格成违规也不静默放行。
    if (r.div) {
      if (r.div.includes('被占用')) { unverified++; console.log(`UNVERIFIED ${r.name} :: ${r.div}`); }
      else { diverged++; console.log(`DIVERGENT-IN-GATE ${r.name} :: ${r.div}（长期位那一份不是文中引的那一件）`); }
    }
  }
  console.log(`check [${prefixes.join(', ')}]: 未归档 ${missing} 颗 ∶ 长期位与文中所引不同 ${diverged} 颗 ∶ 比不了字节 ${unverified} 颗`);
  process.exit(missing || diverged ? 4 : 0);
}
