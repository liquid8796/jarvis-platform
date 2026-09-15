// Records what the reference desktop's slash-command menu filter returns, by
// running the reference's own code: the Fuse.js build it ships and the verbatim
// `ix`/`sx`/`rx`/`lx`/`cx`/`ux`/`mx`/`px` region of its menu module.
//
// Refresh recipe (from this directory, with the desktop installed):
//
//   APP="/c/Program Files/WindowsApps/Claude_<version>_x64__pzs8sxrjxfjjc/app/resources/ion-dist/assets/v1"
//   cp "$APP/vendor-utils-"*.js ./vendor-utils.mjs
//   python - <<'PY'   # slice the filter region out of the menu chunk
//   s = open(r"<APP>\shared-10-<hash>.js", encoding="utf-8").read()
//   a = s.find('function ix(e){return"separator"===')
//   b = s.find('var hx=B(void 0)')
//   open("px-extract.mjs", "w", encoding="utf-8").write(s[a:b])
//   PY
//   node gen-slash-menu-filter.mjs > slash-menu-filter.json
//   rm vendor-utils.mjs px-extract.mjs
//
// The two sliced files are deliberately not checked in: they are the vendor's
// code, and the recording is what the parity test needs.
import { readFileSync } from "node:fs";
import { n as Fuse } from "./vendor-utils.mjs";

const source = readFileSync(new URL("./px-extract.mjs", import.meta.url), "utf-8");
// The region references Fuse as `uu`; everything else in it is self-contained.
// `ux` is the memoized index builder, exported alongside `px` so the raw Fuse
// scores can be recorded too: the ordering alone does not pin the scorer, since
// the sort's earlier tiers decide most pairs before the score bucket is read.
const { px, ux } = new Function("uu", `${source}; return { px, ux };`)(Fuse);

const cases = JSON.parse(readFileSync(new URL("./slash-menu-cases.json", import.meta.url), "utf-8"));

const results = {};
for (const query of cases.queries) {
  results[query] = px(cases.items, query).map((item) => item.label ?? "");
}

const scores = {};
for (const query of cases.scoredQueries) {
  scores[query] = ux(cases.items)
    .search(query.trim().toLowerCase())
    .map((result) => ({ label: result.item.item.label ?? "", score: result.score }));
}

process.stdout.write(`${JSON.stringify({ generatedWith: cases.generatedWith, results, scores }, null, 2)}\n`);
