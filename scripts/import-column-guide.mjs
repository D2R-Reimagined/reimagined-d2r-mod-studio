// Converts the eezstreet/d2rdoc data guide into the compact column-guide.json embedded in ModStudio.Core.
//
//   git clone --depth 1 https://github.com/eezstreet/d2rdoc <checkout>
//   node scripts/import-column-guide.mjs <checkout>
//
// Only field names, alt names, types, descriptions and small reference tables are kept. Reference links
// ($!file#field!$) and HTML are flattened to plain text so the tooltip renderer needs no markup support.
import { readdirSync, readFileSync, writeFileSync } from "node:fs";
import { execSync } from "node:child_process";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const checkout = process.argv[2];
if (!checkout) { console.error("Usage: node scripts/import-column-guide.mjs <d2rdoc checkout>"); process.exit(1); }
const filesDir = join(checkout, "data", "files");
const output = join(dirname(fileURLToPath(import.meta.url)), "..", "src", "ModStudio.Core", "Assets", "column-guide.json");
const maxTableRows = 400;

const raw = {};
for (const name of readdirSync(filesDir).filter(f => f.endsWith(".js"))) {
    const text = readFileSync(join(filesDir, name), "utf8");
    const start = text.indexOf("= {"); const end = text.lastIndexOf("}");
    if (start < 0 || end < 0) continue;
    const key = /files\["([^"]+)"\]/.exec(text)?.[1] ?? name.replace(/\.js$/, "");
    raw[key] = JSON.parse(text.slice(start + 2, end + 1));
}
const byLower = new Map(Object.keys(raw).map(k => [k.toLowerCase(), k]));
const resolve = key => raw[key] ?? raw[byLower.get(key.toLowerCase())];

const entities = { "&lt;": "<", "&gt;": ">", "&amp;": "&", "&quot;": "\"", "&#39;": "'", "&nbsp;": " " };
function plain(value, self) {
    if (value == null) return "";
    let s = String(value);
    s = s.replace(/\$!([^!#]*)#([^!]+)!\$/g, (_, file, field) => file && file.toLowerCase() !== (self ?? "").toLowerCase() ? `${field} (${file})` : field);
    s = s.replace(/\$!([^!]+)!\$/g, (_, file) => `${file}.txt`);
    s = s.replace(/<br\s*\/?>/gi, "\n").replace(/<\/(p|div|li|tr)>/gi, "\n").replace(/<[^>]+>/g, "");
    s = s.replace(/&[a-z#0-9]+;/gi, m => entities[m.toLowerCase()] ?? m);
    return s.replace(/[ \t]+\n/g, "\n").replace(/\n{3,}/g, "\n\n").trim();
}
const cell = (c, self) => plain(typeof c === "object" && c !== null ? c.text : c, self);

function convertField(field, self) {
    const type = field.type ?? {};
    const out = { name: field.name, description: plain(field.description, self) };
    if (field.altNames?.length) out.altNames = field.altNames;
    let typeText = type.type ?? "";
    if (type.type === "reference" && type.file) { typeText = `reference → ${type.field ?? ""} (${type.file})`; out.refFile = type.file; if (type.field) out.refField = type.field; }
    if (type.type === "parse" && type.description) out.format = plain(type.description, self);
    if (typeText) out.type = typeText;
    if (Array.isArray(field.table) && field.table.length > 1) {
        out.table = field.table.slice(0, maxTableRows + 1).map(row => row.map(c => cell(c, self)));
        if (field.table.length > maxTableRows + 1) out.tableTruncated = field.table.length - 1;
    }
    if (Array.isArray(field.bittable) && field.bittable.length) out.bits = field.bittable.map(c => cell(c, self));
    return out;
}

const files = {};
for (const [key, file] of Object.entries(raw)) {
    if (file.noHtml || file.guideOnly && !file.codeDependency) continue;
    const fields = (file.fields ?? []).filter(f => f.type?.type !== "comment").map(f => convertField(f, key));
    for (const appended of file.appendFiles ?? []) {
        const other = resolve(appended); if (!other) continue;
        for (const f of other.fields ?? []) if (f.type?.type !== "comment" && !fields.some(x => x.name === f.name)) fields.push(convertField(f, key));
    }
    if (fields.length === 0) continue;
    // The site's page file names keep the .js file's casing, so the original key is kept for links.
    files[key.toLowerCase()] = { key, title: file.title, overview: plain(file.overview, key), fields };
}

let commit = "";
try { commit = execSync("git rev-parse HEAD", { cwd: checkout }).toString().trim(); } catch {}
const guide = { source: "https://eezstreet.github.io/d2rdoc/", repository: "https://github.com/eezstreet/d2rdoc", commit, generated: new Date().toISOString().slice(0, 10), files };
writeFileSync(output, JSON.stringify(guide));
const fieldCount = Object.values(files).reduce((n, f) => n + f.fields.length, 0);
console.log(`Wrote ${output}: ${Object.keys(files).length} files, ${fieldCount} fields, ${(readFileSync(output).length / 1024).toFixed(0)} KiB`);
