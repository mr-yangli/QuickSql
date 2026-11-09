// v0.35 第二笔：这一支从 gitignored 的 obj/ 搬进 tools/ 并入库（欠账"量具进版本库"的那一步）。
// v0.38 第四笔：添"没有 git 也能跑"那一半。原样它一进本仓库就抛 fatal: not a git repository，
// 于是 §13.17 ① 那句"全仓库零 CRLF、零 BOM"从搬家那天起就没被复核过（欠账记 §14 那一格）。
// v0.38 第十一笔：git 那一半在 2026-10-04 的入库之后是直接跑不起来的（抛 status 128，一颗文件都没判），
// 因为本仓库现在是四棵子树共一个根，而 git 的三张口给三种路径——见下面 cwdPrefix() 那一段。修在此处，
// 判据本身一字未改：比 HEAD 的 BOM ∶ 比工作区改动件的 BOM/CRLF/TAB/行尾，两半都是原样；
// 只把 `ls-files` 那颗硬编码的 *.cs 改成跟着 --ext 走，与无 git 那半的射程对齐。
// 两种模式：
//   有 git —— 比 HEAD（tracked .cs 的 BOM）+ 比工作区改动过的 .cs（BOM/CRLF/TAB/行尾），照旧
//   无 git —— 直接遍历工作树自产文本，判据是绝对的（零 BOM ∶ 零 CRLF ∶ 零 TAB ∶ 有行尾），
//             这比"只看改动过的"更强，因为无 git 时没有"改动"可读，只有整棵树
// 两种模式共用同一条边界：.gitignore 写着不算交付物的，这里也不算（不靠硬编码的跳目录名单猜），
// 外加 §13.17 ① 的厂商字节例外 src/QuickSql.Providers.Xugu/Driver/。
// 用法：node tools/hygiene.mjs [--ext .cs] [--ext .md] …   不给 --ext 就只查 .cs
//       node tools/hygiene.mjs --worktree …   绕过 git 那一半，判整棵自产文本（刚 commit 完 git 模式是空集）
//       node tools/hygiene.mjs --fix …   把判据里"行尾"那一半改过来：去 BOM ∶ CRLF→LF ∶ 补末行换行；
//                                         TAB 不动（制表符可能活在字符串字面量里，改写它不是量具的权）
//       退出码：0 干净 ∶ 4 有违规 ∶ 3 找不到可扫的自产文本（那是量具自己扫空集，不许当绿灯）
import fs from 'node:fs';
import path from 'node:path';
import cp from 'node:child_process';

const HARD_SKIP = new Set(['bin', 'obj', '.vs', 'TestResults', 'drivers', 'node_modules', '.git', '.vscode', 'artifacts']);
const VENDOR = 'src/QuickSql.Providers.Xugu/Driver/'; // 厂商字节按 §13.17 ① 的例外保真，不判"没被改"为"不该改"

// 无 git 时"自产文本"没有 `git ls-files` 可问，就照仓库根那份 .gitignore 划界：
// 它写着什么，什么就不算交付物。支持的形只有三种（这一份文件里在场的就这三种）：
// 目录名（结尾带 /）、整颗名字或路径、`*.后缀`。读不出的形一律忽略并报出来，不猜。
function readIgnore(root) {
  const p = path.join(root, '.gitignore');
  if (!fs.existsSync(p)) return { dirs: new Set(), names: new Set(), suffix: new Set(), unparsed: [] };
  const dirs = new Set(), names = new Set(), suffix = new Set(), unparsed = [];
  for (const raw of fs.readFileSync(p, 'utf8').split(/\r?\n/)) {
    const l = raw.trim();
    if (!l || l.startsWith('#')) continue;
    if (l.endsWith('/')) dirs.add(l.slice(0, -1));
    else if (l.startsWith('!')) unparsed.push(l);
    else if (/^\*\./.test(l)) suffix.add(l.slice(1));
    else if (/^[\w.*\/\\-]+$/.test(l)) names.add(l.replace(/^\.\//, ''));
    else unparsed.push(l);
  }
  return { dirs, names, suffix, unparsed };
}

const bom = b => b[0] === 0xef && b[1] === 0xbb && b[2] === 0xbf;
const args = process.argv.slice(2);
const exts = args.reduce((a, x, i) => (x === '--ext' && args[i + 1] ? a.concat(args[i + 1]) : a), []);
const wantExts = exts.length ? exts : ['.cs'];
const fix = args.includes('--fix');
// 刚 commit 完跑 git 模式必然扫到空集（它判的是"工作区改动过的"），而 §13.17 ① 那句"全仓库零 CRLF ∶ 零 BOM"
// 要的是整棵树的绝对读数——那正是无 git 年代这一支交回的形。`--worktree` 就是把这一半强制回来，
// 不改判据、只换射程；两颗名字都保留，交账时读得出这一场量的是哪一片。
const forceWorktree = args.includes('--worktree');
// 从 HEAD 取一颗件的字节要留给读 BOM 的头三颗，`execFileSync` 却要把整份吐完才还手，
// 默认 1 MiB 不够台账那一份用——这是探针量出来的，不是猜的。
const HEAD_MAX = 256 * 1024 * 1024;
const ign = readIgnore('.');

// 目录名规则：带斜杠的按从根算起的路径前缀，不带斜杠的按任何一层的名字。
function ignoredDir(rel, name) {
  if (HARD_SKIP.has(name)) return true;
  for (const d of ign.dirs) {
    if (d.includes('/')) { if (rel.length && (d === rel || d.startsWith(rel + '/') || rel.startsWith(d + '/'))) return true; }
    else if (d === name) return true;
  }
  return false;
}

function ignoredFile(rel) {
  const base = rel.slice(rel.lastIndexOf('/') + 1);
  if (ign.names.has(rel) || ign.names.has(base)) return true;
  for (const s of ign.suffix) if (rel.endsWith(s)) return true;
  return rel.startsWith(VENDOR);
}

function isGit() {
  try {
    cp.execFileSync('git', ['rev-parse', '--is-inside-work-tree'], { stdio: ['ignore', 'pipe', 'ignore'] });
    return true;
  } catch {
    return false;
  }
}

// git 那三张口给的是三种路径，而 2026-10-04 入库之后本仓库是"四棵子树共一个根"（`sql/` 只是其中一棵），
// 于是同一支脚本里并排着三种形：`ls-files` 给"相对当前目录"的，`status --porcelain` 给"相对仓库根"的
// （还会把别家子树的改动一起给），而 `git show HEAD:<路径>` 认的也是相对仓库根的。
// 本支的判据路径（`src/…` 与 VENDOR 那一格）一直是以运行目录为根的，所以统一折成"相对当前目录"：
// HEAD 那侧写成 `HEAD:./<相对路径>`（git 认这一形，`./` 就是当前目录），status 那侧减掉 `--show-prefix`。
function cwdPrefix() {
  try { return cp.execFileSync('git', ['rev-parse', '--show-prefix'], { encoding: 'utf8' }).trim(); }
  catch { return ''; }
}

function check(buf, f) {
  const isBom = bom(buf);
  const text = (isBom ? buf.subarray(3) : buf).toString('utf8');
  const issues = [];
  if (isBom) issues.push('has BOM');
  if (!text.length) return { file: f, issues, empty: true }; // 没有内容的件没有"行"可言：不判行尾，另计数报出，不静默
  if (!text.endsWith('\n')) issues.push('no trailing newline');
  const crlf = (text.match(/\r\n/g) || []).length;
  if (crlf) issues.push(`CRLF x${crlf}`);
  const cr = (text.match(/\r(?!\n)/g) || []).length; // 孤立 CR 是另一种老 Mac 行尾，§13.17 ① 要的是 LF
  if (cr) issues.push(`CR x${cr}`);
  const tabs = (text.match(/\t/g) || []).length;
  if (tabs) issues.push(`TAB x${tabs}`);
  return { file: f, issues };
}

// --fix 的射程只有 BOM 与行尾两样：字节级改写，不碰 TAB（制表符可能活在字符串字面量里），
// 也不碰带 0x00 的件（那是 UTF-16 一类的编码，按字节找 CR 会把它搅烂 → 原样退回，只报不改）。
function normalize(buf) {
  let b = bom(buf) ? buf.subarray(3) : buf;
  if (b.includes(0)) return buf;
  if (b.includes(0x0d)) {
    const out = Buffer.allocUnsafe(b.length);
    let n = 0;
    for (let i = 0; i < b.length; i++) {
      if (b[i] !== 0x0d) { out[n++] = b[i]; continue; }
      if (b[i + 1] === 0x0a) continue; // CRLF：丢掉 CR，紧随的 LF 原位留着就是 LF 行尾
      out[n++] = 0x0a; // 孤立 CR：换成 LF
    }
    b = out.subarray(0, n);
  }
  if (b.length && b[b.length - 1] !== 0x0a) b = Buffer.concat([b, Buffer.from('\n', 'utf8')]);
  return Buffer.from(b);
}

function walk(dir, out, rel) {
  const r = rel === undefined ? (dir === '.' ? '' : dir.replace(/^\.\//, '')) : rel;
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const childRel = r ? r + '/' + e.name : e.name;
    if (e.isDirectory()) { if (!ignoredDir(childRel, e.name)) walk(path.join(dir, e.name), out, childRel); }
    else if (wantExts.some(x => e.name.endsWith(x)) && !ignoredFile(childRel)) out.push(childRel);
  }
  return out;
}

const git = isGit();
let files = [];
let mode = '';
let headBom = null;

console.log(`boundary: .gitignore dirs=${ign.dirs.size} names=${ign.names.size} suffix=${ign.suffix.size} unparsed=${ign.unparsed.length} + vendor ${VENDOR}`);
for (const u of ign.unparsed) console.log(`UNPARSED-RULE ${u}（这一条形没被读，落在这条形上的文件会被当成自产文本）`);

if (git && !forceWorktree) {
  mode = 'git';
  const prefix = cwdPrefix();
  const tracked = cp.execFileSync('git', ['ls-files', ...wantExts.map(x => '*' + x)], { encoding: 'utf8' }).split('\n').filter(Boolean);
  // HEAD 里查不到的单报一颗：新落盘还没 commit 的件在 index 里、`ls-files` 认它，而 `git show HEAD:` 不认。
  // 读不到一律报名并带错误码，不许静默折成"不在 HEAD"：`execFileSync` 的默认射程只有 1 MiB，
  // 而台账那一份 .md 在 HEAD 里就比它大——探针实测过，不抬这个数就会把在册的件误报成缺席。
  let notInHead = 0;
  headBom = tracked.filter(f => {
    let raw;
    try {
      raw = cp.execFileSync('git', ['show', 'HEAD:./' + f], { stdio: ['ignore', 'pipe', 'ignore'], maxBuffer: HEAD_MAX });
    } catch (e) {
      notInHead++;
      console.log(`HEAD-MISS ${f}（${(e && e.code) || e}）这一颗 HEAD 那侧没读到，不算干净也不算违规`);
      return false;
    }
    return bom(raw);
  }).length;
  console.log(`HEAD ${wantExts.join('/')} ${tracked.length} tracked ∶ ${headBom} with BOM ∶ ${notInHead} not in HEAD ∶ prefix=${prefix || '(仓库根本身)'}`);
  const out = cp.execFileSync('git', ['status', '--porcelain'], { encoding: 'utf8' });
  const set = new Set();
  const add = p => { if (wantExts.some(x => p.endsWith(x)) && !ignoredFile(p)) set.add(p); };
  for (const l of out.split('\n').filter(Boolean)) {
    let p = l.slice(3).trim().replace(/\\/g, '/');
    if (p.includes(' -> ')) p = p.split(' -> ').pop(); // 改名行给的是「旧 -> 新」，工作树上在场的是新名
    if (prefix) {
      if (!p.startsWith(prefix)) continue; // 别家子树的改动不进本支射程
      p = p.slice(prefix.length);
    }
    if (l.startsWith('??') && fs.existsSync(p) && fs.statSync(p).isDirectory()) { walk(p, []).forEach(f => add(f)); continue; }
    add(p);
  }
  files = [...set];
} else {
  mode = git ? 'worktree（在场 git 未用——`--worktree` 强制整棵树的绝对读数）'
             : 'worktree（无 git，判据换成整棵自产文本的绝对读数）';
  files = walk('.', []).sort();
}

let bad = 0;
let empty = 0;
let locked = 0;
let absent = 0;
let fixed = 0;
let left = 0;
for (const f of files) {
  let buf;
  try {
    buf = fs.readFileSync(f);
  } catch (e) {
    if (e && (e.code === 'EBUSY' || e.code === 'EPERM' || e.code === 'EMFILE')) { locked++; console.log(`LOCKED ${f}（读不到，不算干净也不算违规）`); continue; }
    // git 报它改动过而工作树上没有：删除那一条，或改名行里 git 打的是带引号的路径。不算干净也不算违规，只报名。
    if (e && e.code === 'ENOENT') { absent++; console.log(`ABSENT ${f}（git 说它变了，工作树里却没有这一颗）`); continue; }
    throw e;
  }
  const r = check(buf, f);
  if (r.empty) { empty++; console.log(`EMPTY ${f}（零字节件：没有行可言，不判行尾，只报个名）`); }
  if (r.issues.length) { bad++; console.log(`BAD ${f} :: ${r.issues.join(' | ')}`); }
  if (fix && r.issues.length) {
    const nb = normalize(buf);
    if (nb.equals(buf)) { console.log(`SKIP-FIX ${f}（那一类不在 --fix 的射程内，只报不改）`); continue; }
    fs.writeFileSync(f, nb);
    fixed++;
    const after = check(nb, f);
    if (after.issues.length) left++;
    console.log(`FIXED ${f} :: ${r.issues.join(' | ')} → ${after.issues.length ? after.issues.join(' | ') : 'clean'}`);
  }
}
console.log(`mode=${mode}${fix ? '  [--fix 已改写]' : ''}`);
console.log(`checked ${files.length} file(s) [${wantExts.join(', ')}], ${bad} problem file(s), ${fixed} fixed, ${empty} empty file(s), ${locked} locked file(s), ${absent} absent file(s)`);
if (fix && bad) {
  let recheck = 0;
  for (const f of files) {
    try { if (check(fs.readFileSync(f), f).issues.length) recheck++; }
    catch { /* 读不到的一律不算进复核，交给上一场的 LOCKED */ }
  }
  if (recheck !== left) console.log(`复核不一致：改写当场读到的余数 ${left} ∶ 重开一遍读到的 ${recheck}（以重开那颗为准）`);
  left = recheck;
  console.log(`复核：改写后再走同一判据，仍有 ${left} 颗违规（--fix 只治 BOM 与行尾，TAB 一类留在地上）`);
}
if (!files.length) { console.log('NOTHING-SCANNED：扫到空集，这一场的绿灯不算数'); process.exit(3); }
// 退出码问的是"这一支此刻还看得见违规吗"，不是"改写之前有没有"——
// 否则"已经改干净"与"压根没得改"两形都退出 4，交账时读不出区别（这颗是在 v38-hygiene-2 那一场上改的判法）。
console.log(`verdict: ${fix ? '改写后余 ' + left + ' 颗' : bad + ' 颗违规'}`);
process.exit((fix ? left : bad) ? 4 : 0);
