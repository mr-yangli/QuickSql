// v0.35 第二笔：这一支从 gitignored 的 obj/ 搬进 tools/ 并入库（欠账"量具进版本库"的那一步）。
// 规格文档里历史引文的 obj/ 路径是"当时位"；从本版起重跑路径以 tools/ 为准。
// v0.38 第四笔：添 `--tables` 那一形。旧形一段只能认一颗"期望格数"，而 §12.8 那一区间 1640..1700
// 里站着两张表（1640~1642 那一张三列，1676~1689 那一张四列）——按 4 判就说后一张 14 行全劈错格，
// 按 5 判就说前一张 3 行错。那颗呆定的默认值（want=4）从表体长出新那一列起就是过期的，这正是 §14
// 那一格记着的"cellscan 的默认期望格数已过期"。新形不猜期望：每张表自己的分隔行就是它的期望；
// 数不出分隔行的表报 NO-SEP 并单独计数，绝不把"没判"读成"判过了且干净"。
import fs from 'node:fs';

// 逐行数「表格会被劈成几格」，用于 §12.8 那三张表与 §14 的表体回写之后复核。
// 用法：node tools/v32-cellscan.mjs [from] [to] [期望格数]   旧形：整段按一颗 want 判
//       node tools/v32-cellscan.mjs --tables                 新形：全篇按每张表自己的分隔行判
// 退出码：0 没有错格 ∶ 4 有错格（判不了的表另报一颗数，不升格成违规）∶ 3 一张表都没找到（空比不算绿灯）
// 两形共用同一颗单位：`| a | b |` 数出来是 3（两格加行首那一颗竖线），分隔行 `|---|---|` 也数成 3。
const lines = fs.readFileSync('docs/quicksql-orm-design.md', 'utf8').split('\n');
const from = Number(process.argv[2] || 1640);
const to = Number(process.argv[3] || 1700);
const want = Number(process.argv[4] || 4);

// ── 新形 --tables：期望由每张表自己的分隔行给 ───────────────────────────────
// 上面那三颗 from/to/want 在新形里一概不用（它们是旧形的坐标）；新形扫全篇。
// 代码串里那两个 [ ] 记法是刻意绕开反斜杠：[\x5c] 就是一颗反斜杠，[|] 就是一颗竖线，
// 读起来比 \\\| 少一层歧义（这一支曾被转义竖线绊过一次，别再来一次）。
if (process.argv.includes('--tables')) {
  const cellCount = l => l.replace(/[\x5c][|]/g, ' ').split('|').length - 1;
  const isSep = l => /^\s*[|](?:\s*:?-{2,}:?\s*[|])+\s*$/.test(l);
  const isRow = l => /^\s*[|]/.test(l);
  let tables = 0, noSep = 0, odd = 0, fences = 0, rows = 0;
  for (let i = 0; i < lines.length; i++) {
    if (/^\s*`{3}/.test(lines[i])) { fences++; continue; } // 围栏开关：围栏里的假表格不是文档的表
    if (!isRow(lines[i])) continue;
    let j = i;
    while (j < lines.length && isRow(lines[j]) && !/^\s*`{3}/.test(lines[j])) j++;
    const block = lines.slice(i, j);
    tables++;
    const sep = block.findIndex(isSep);
    if (sep < 0) {
      noSep++;
      console.log(`NO-SEP 起于行 ${i + 1}（${block.length} 行）：这张表没有分隔行，判不出期望格数——不算它干净`);
    } else {
      const w = cellCount(block[sep]);
      block.forEach((l, k) => {
        if (k === sep) return;
        rows++;
        const n = cellCount(l);
        if (n !== w) { odd++; console.log(`ODD 行 ${i + k + 1} 格数 ${n}（本表分隔行判 ${w}，起于行 ${i + 1}）：${l.slice(0, 60)}`); }
      });
    }
    i = j - 1;
  }
  console.log(`--tables：全篇 ${tables} 张表（判了 ${rows} 行），错格 ${odd} 行，判不了的表 ${noSep} 张，围栏行 ${fences} 颗不参与`);
  if (noSep) console.log(`NOT-JUDGED：上面那 ${noSep} 块没有分隔行，这一支没看见它们——绿灯只覆盖判了的那 ${rows} 行（本规格那两张 §14 台账格是故意不带分隔行的：添一颗 |---| 就多占一行，而全篇到处在按行号引它。要判那两块仍用旧形，自己给 want）`);
  if (!tables) { console.log('NOTHING-SCANNED：一张表都没找到，这一场的绿灯不算数'); process.exit(3); }
  process.exit(odd ? 4 : 0);
}
// ── 旧形：区间 + 一颗呆定的 want ────────────────────────────────────────────
let odd = 0;
let touched = 0;
for (let i = from - 1; i < Math.min(to, lines.length); i += 1) {
  const l = lines[i];
  if (!/^[|]/.test(l)) continue;
  touched += 1;
  const n = l.replace(/\\\|/g, '\u0000').split('|').length - 1;
  if (n !== want) { odd += 1; console.log(`ODD 行 ${i + 1} 格数 ${n}（期望 ${want}）：${l.slice(0, 70)}`); }
}
console.log(`${odd === 0 ? 'CELLS-OK' : 'ODD=' + odd}（区间 ${from}..${to}，格数按 ${want} 判）`);
if (!touched) { console.log('NOTHING-SCANNED：这一区间没有一行表格，0 错格那句不算绿灯'); process.exit(3); }
process.exit(odd ? 4 : 0);
