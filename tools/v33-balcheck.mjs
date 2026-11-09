// v0.35 第二笔：这一支从 gitignored 的 obj/ 搬进 tools/ 并入库（欠账"量具进版本库"的那一步）。
// v0.38 第四笔：添"没有 git 也能跑"那一半。原样第一句就是 `git show HEAD:docs/...`，
// 在不是仓库的工作目录里当场抛（欠账记 §14 那一格：两支读 HEAD 的量具从搬家那天起没跑过）。
// 两种模式：
//   有 git —— HEAD ∶ 工作区 逐颗计数比 delta（判据"改动前后差值必须成对"，一字未改照旧）
//   无 git —— 基线快照 `tools/balcheck-baseline.json` ∶ 工作区：上一笔交账时把当时的计数写进去，
//              本笔再数一次，delta 就是"这一笔动了什么"。快照与 HEAD 的区别要说白：HEAD 是
//              不可变的提交，快照是上一笔自己写的读数——所以它只能证"从上次交账到现在没配错"，
//              不能证"从头到尾没配错"，这一格不许把它读成后者。
// 用法：node tools/v33-balcheck.mjs                比 delta（有快照就读快照，没有就报缺）
//       node tools/v33-balcheck.mjs --snapshot     把当前计数写成基线（交账最后一步做）
//       node tools/v33-balcheck.mjs --doc <路径>   换被量的文档
// 退出码：0 delta 全偶 ∶ 4 有奇数 delta ∶ 3 没有可比的对象（空比不许当绿灯）
import fs from 'node:fs';
import cp from 'node:child_process';

const chars = [['（', '（'], ['）', '）'], ['「', '「'], ['」', '」'], ['【', '【'], ['】', '】'], ['BT', '`'], ['PIPE', '|'], ['**', '*']];
const DOCi = process.argv.indexOf('--doc');
const DOC = DOCi > 0 ? process.argv[DOCi + 1] : 'docs/quicksql-orm-design.md';
const BASE = 'tools/balcheck-baseline.json';

const count = (t, ch) => t.split(ch).length - 1;
const vector = t => Object.fromEntries(chars.map(([, ch]) => [ch, count(t, ch)]));

function isGit() {
  try {
    cp.execFileSync('git', ['rev-parse', '--is-inside-work-tree'], { stdio: ['ignore', 'pipe', 'ignore'] });
    return true;
  } catch {
    return false;
  }
}

const now = fs.readFileSync(DOC, 'utf8');
const nowV = vector(now);

if (process.argv.includes('--snapshot')) {
  const body = JSON.stringify({ doc: DOC, taken: new Date().toISOString().slice(0, 10), mode: isGit() ? 'git' : 'worktree', counts: nowV }, null, 2) + '\n';
  fs.writeFileSync(BASE, body, 'utf8');
  console.log(`snapshot -> ${BASE} (${Buffer.byteLength(body, 'utf8')} B)`);
  console.log(JSON.stringify(nowV));
  process.exit(0);
}

let refV = null;
let refName = '';
if (isGit()) {
  try {
    refV = vector(cp.execFileSync('git', ['show', 'HEAD:' + DOC], { encoding: 'utf8', maxBuffer: 1 << 28 }));
    refName = 'HEAD';
  } catch { /* 仓库里没有这一颗（新文件），退回快照 */ }
}
if (!refV && fs.existsSync(BASE)) {
  refV = JSON.parse(fs.readFileSync(BASE, 'utf8')).counts;
  refName = 'baseline(' + JSON.parse(fs.readFileSync(BASE, 'utf8')).taken + ')';
}
if (!refV) {
  console.log('mode: 没有可比对象（既无 HEAD 也无 ' + BASE + '）——先跑 --snapshot 立基线，再把绝对数写台账');
  for (const [name, ch] of chars) console.log(name.padEnd(5), 'NOW', String(nowV[ch]).padStart(8));
  process.exit(3);
}

let odd = 0;
console.log('mode: ' + refName + ' vs worktree');
// 判据分两族，别看成一族：成对的那三族问"新增的左与新增的右是否同数"（各加一颗 → Δ左=Δ右=1，配平）；
// 自己就是分隔符的那三颗（反引号 ∶ 竖线 ∶ 星号）没有"右"可比，只能问"新增的颗数是不是偶"（开一次必关一次）。
// 旧形把两族都按"Δ 为偶"判，于是任何一次"加一对括号"的正常改动都会双双击穿——那是判错的形，不是文档的账。
const pairs = [['（', '）'], ['「', '」'], ['【', '】']];
const singles = ['`', '|', '*'];
const D = ch => nowV[ch] - refV[ch];
for (const [a, b] of pairs) {
  const da = D(a), db = D(b);
  if (da !== db) { odd++; console.log(`ODD ${a}${b} 新增 ${da} vs ${db}（不对称：这一笔动过的文本自己没配平）`); }
  console.log((a + b).padEnd(4), String(refV[a]).padStart(8), '->', String(nowV[a]).padStart(8), ' d=' + da, '   ', String(refV[b]).padStart(8), '->', String(nowV[b]).padStart(8), ' d=' + db, da === db ? '' : '  MISMATCH');
}
for (const ch of singles) {
  const d = D(ch);
  if (d % 2) { odd++; console.log(`ODD ${ch === '`' ? '反引号' : ch === '|' ? '竖线' : '星号'} 新增 ${d}（奇数：开了没关，或关了没开）`); }
  console.log((ch === '`' ? 'BT' : ch === '|' ? 'PIPE' : 'STAR').padEnd(4), String(refV[ch]).padStart(8), '->', String(nowV[ch]).padStart(8), ' d=' + d, d % 2 ? '  ODD' : '');
}
console.log(odd ? `判定：${odd} 处不配平（这一笔动过的文本自己没配平）` : '判定：新增部分全配平（成对那三族左右同数，分隔符那三颗各成双）');
process.exit(odd ? 4 : 0);
