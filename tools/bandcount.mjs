// v0.35 第二笔：这一支从 gitignored 的 obj/ 搬进 tools/ 并入库（欠账"量具进版本库"的那一步）。
// 规格文档里历史引文的 obj/ 路径是"当时位"；从本版起重跑路径以 tools/ 为准。
// 机械重数第 2 行那串"带内 / 越线"的场数：扫门禁日志（`vNN-*gate*.log`），
// 抽「实测 NN%」那一格，按 83%~89% 带内、≥90 越上界、≤82 越下界分类，并给逐场清单。
// 用法：node tools/bandcount.mjs [只扫某版本前缀，如 v29]
// 口径注意：本脚本不排除"每颗产物的第一跑"，也不判断日志属于交账还是回写 —— 它只报"文件里印了多少"。
// v0.38 第三笔跟着裁决改一处：见证物的长期位从今天起是 `docs/evidence/`，而这一支原来只读 `obj/logs`，
// 于是它会数不到本版之后落的场（n 停在 5 而实际在涨）。现在两处都扫并按文件名去重（同名以长期位为准）。
import fs from "node:fs";

const only = process.argv[2] ?? "";
const DIRS = ["docs/evidence", "obj/logs"];
const seen = new Set();
const files = [];
for (const dir of DIRS) {
  if (!fs.existsSync(dir)) continue;
  for (const f of fs.readdirSync(dir).sort((a, b) => {
    const va = Number(a.match(/^v(\d+)/)?.[1] ?? 0);
    const vb = Number(b.match(/^v(\d+)/)?.[1] ?? 0);
    return va - vb || a.localeCompare(b);
  })) {
    if (!/^v\d+.*gate.*\.log$/.test(f) || (only && !f.startsWith(only))) continue;
    if (seen.has(f)) continue;
    seen.add(f);
    files.push({ name: f, path: `${dir}/${f}` });
  }
}

const hist = new Map();
const perVersion = new Map();
const unparsed = [];
const rows = [];
for (const { name: f, path: p } of files) {
  const text = fs.readFileSync(p, "utf8");
  const lines = text.split(/\r?\n/);
  const head = lines.findIndex((l) => /\[[红绿]\] 物化吞吐 vs Dapper/.test(l));
  const meas = lines.slice(head + 1, head + 6).find((l) => /实测/.test(l)) ?? "";
  const m = meas.match(/实测\s*(\d+)%/);
  const version = (f.match(/^v(\d+)/) || [])[1] ?? "?";
  if (!m) {
    unparsed.push(`${f}  head=${head}  meas=${meas.slice(0, 60)}`);
    continue;
  }
  const pct = Number(m[1]);
  const cls = pct > 89 ? "上" : pct < 83 ? "下" : "内";
  hist.set(pct, (hist.get(pct) ?? 0) + 1);
  const v = perVersion.get(version) ?? { 内: 0, 上: 0, 下: 0, n: 0 };
  v[cls] += 1;
  v.n += 1;
  perVersion.set(version, v);
  rows.push(`${f.padEnd(26)} ${pct}% ${cls}`);
}
console.log(rows.join("\n"));
console.log(`\n可解析 ${rows.length} / 共 ${files.length} 份门禁日志`);
if (unparsed.length) console.log(`解析不出「实测 NN%」的：\n${unparsed.join("\n")}`);
console.log("\n逐百分比直方图（升序）：");
console.log(
  [...hist.entries()]
    .sort((a, b) => a[0] - b[0])
    .map(([p, n]) => `${p}%×${n}`)
    .join("  "),
);
const sum = (lo, hi) => [...hist.entries()].filter(([p]) => p >= lo && p <= hi).reduce((s, [, n]) => s + n, 0);
console.log(`\n带内（83~89）合计 ${sum(83, 89)}；越上界（≥90）${sum(90, 999)}；越下界（≤82）${sum(0, 82)}`);
console.log("带内逐段（与文档那串对照）：");
console.log(
  [...hist.entries()]
    .filter(([p]) => p >= 83 && p <= 89)
    .sort((a, b) => a[0] - b[0])
    .map(([p, n]) => n)
    .join("+") +
    " = " +
    [...hist.entries()].filter(([p]) => p >= 83 && p <= 89).reduce((s, [, n]) => s + n, 0),
);
console.log("\n逐版本：");
for (const [v, c] of perVersion) console.log(`  v${v.padEnd(2)} n=${c.n} 内=${c.内} 上=${c.上} 下=${c.下}`);
