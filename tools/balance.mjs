// v0.38 第三笔：这一支从 scratch（obj/balance2-v38.mjs）搬进 tools/ 并入常驻量具那一本账。
// 搬的理由不是"整洁"，是这一格的裁决本身：一次性工具躺在会被复用的目录里，保留期就比账短——

// v0.37 ∶ v0.38 那三笔数出来的 343 颗断口里，量具与日志是同一批受害者。历史引文里的
// `obj/balance2-v38.mjs` ∶ `obj/balance-v37.mjs` 一律读作"当时位"，从本版起重跑以 tools/ 为准。
//
// 逐行配平：全角括号 ∶ 书名号 ∶ 方头括号各要成对，星号要成双；反引号里的代码串先摘掉再数，
// 因为 glob（`src/**`、`*.cs` 那种）会被旧那支读成奇数星号而整片误报（那颗误报记 §14 变更记录 v0.38 第二笔）。
// v0.38 第四笔：摘代码串那一步从"一颗反引号翻一次开关"换成按 CommonMark 的形数串——一串 k 颗开，
// 遇到下一串同样 k 颗才闭，串里长度不同的反引号算正文。换的理由是一颗真反引号：行 2081 那格引了
// C# 的 arity 写法 `System.Nullable`1[...]`，它活在 ``…`` 双反引号串里，旧判据把它数成"奇数颗"，
// 于是全篇多报一颗假配平事故（三颗围栏那一类假报已在第三笔摘掉，这是同一族的第二族假报）。
// 新判据不再数颗数，改问"这一行末尾有没有还开着没收的串"——那才是真的断。
// 用法：node tools/balance.mjs [lo] [hi] —— 不给参数就扫全文；只报标出的行号与差额。
import fs from 'node:fs';

const lines = fs.readFileSync('docs/quicksql-orm-design.md', 'utf8').split(/\r?\n/);
const lo = +(process.argv[2] ?? 1);
const hi = +(process.argv[3] ?? lines.length);

// 返回 { text（代码串里的字符换成空格）, open（行末还开着的串长，0 = 收干净了）}
const strip = s => {
  let out = '';
  let i = 0;
  let open = 0;
  while (i < s.length) {
    if (s[i] === '`') {
      let k = 0;
      while (s[i + k] === '`') k += 1;
      if (open === 0) open = k;
      else if (open === k) open = 0;
      i += k;
      continue;
    }
    out += open ? ' ' : s[i];
    i += 1;
  }
  return { text: out, open };
};

const count = (s, c) => s.split(c).length - 1;
let flagged = 0;

for (let n = lo; n <= hi; n++) {
  const raw = lines[n - 1] ?? '';
  if (!raw.trim()) continue;
  const flat = raw.replace(/\\\|/g, '@');
  const fence = /^\s*`{3}/.test(raw); // 围栏行：那三颗是标记不是串，整行不判反引号
  const { text: s, open } = strip(flat);
  const bad = [];
  for (const [a, b] of [['（', '）'], ['「', '」'], ['【', '】']]) {
    const ca = count(s, a), cb = count(s, b);
    if (ca !== cb) bad.push(a + b + ' ' + ca + '/' + cb);
  }
  if (!fence && open) bad.push('反引号 未闭合 k=' + open);
  const stars = count(s, '*');
  if (stars % 2) bad.push('* 奇=' + stars);
  if (bad.length) { flagged++; console.log(n + ' IMBAL ' + bad.join('  ') + '  len=' + raw.length); }
}
console.log('--- 扫描 ' + lo + '..' + hi + '，标记 ' + flagged + ' 行');
