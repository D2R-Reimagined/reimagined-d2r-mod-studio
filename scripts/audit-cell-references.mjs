// Offline audit and navigation registry from the exact bundled guide.
// Run: node scripts/audit-cell-references.mjs
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const guide = JSON.parse(readFileSync(join(root, 'src/ModStudio.Core/Assets/column-guide.json'), 'utf8'));
const overrides = new Map();
function add(tables, fields, kind, targets, note = '') {
    for (const table of tables.split('|')) for (const field of fields.split('|'))
        overrides.set(`${table}.${field}`, { kind, targets: targets.split('|'), note });
}
const items = 'armor.code|weapons.code|misc.code';
add('gamble', 'code', 'item-code', items, 'Exact shared-source item code; preserve all matches.');
add('uniqueitems', 'code', 'item-code', items);
add('setitems', 'item', 'item-code', items);
add('charstats|monequip', 'item#', 'item-code', items);
add('belts', 'defaultItemCodeCol#', 'item-code', items);
add('books', 'ScrollSpellCode|BookSpellCode', 'item-code', 'misc.code');
add('armor|weapons|misc', 'normcode|ubercode|ultracode|TMogType|NightmareUpgrade|HellUpgrade', 'item-code', items,
    'Respect source-table upgrade rules and xxx sentinel; confirm any target restriction per field before rollout.');
add('misc', 'BetterGem', 'item-code', 'misc.code');
add('armor|weapons|misc', 'auto prefix', 'group-key', 'automagic.group', 'Multiple affixes share a group; show a chooser.');
add('armor|weapons|misc', 'component', 'prose-key', 'composit.Token', 'Guide prose calls this a code; Composit names the column Token. Verify value encoding before enabling.');
add('gems', 'transform', 'prose-key', 'colors.Code');
add('levels', 'Depend|Vis#', 'numeric-key', 'levels.Id', 'Zero means no reference.');
add('levels', 'Warp#', 'numeric-key', 'lvlwarp.Id', '-1 means none; duplicate IDs need direction/context.');
add('levels', 'LevelType', 'numeric-key', 'lvltypes.Id', 'Guide also describes row order; verify ID versus slot behavior.');
add('lvlmaze', 'Level', 'numeric-key', 'levels.Id');
add('lvlprest', 'LevelId', 'numeric-key', 'levels.Id', 'Zero means partial preset, not level zero.');
add('levels', 'SubType|SubWaypoint|SubShrine', 'group-key', 'lvlsub.Type', 'Level substitution group, not a unique row; apply field-specific sentinel/gating rules.');
add('lvlsub', 'Type', 'reverse-group', 'levels.SubType', 'Group membership; not a unique outgoing pointer.');
add('actinfo', 'wanderingnpcstart', 'row-index', 'wanderingmon.[slot]', 'Beginning of a range controlled by wanderingnpcrange.');
add('armor|weapons|misc', 'belt', 'row-index', 'belts.[slot]');
add('armor|weapons|misc', 'missiletype', 'row-index', 'missiles.[slot]', 'Numeric index is not the Missile name.');
add('armor|weapons|misc', 'gemoffset', 'row-index', 'gems.[slot]', 'Range start, depends on gemapplytype.');
add('hireling', 'Class|Seller', 'row-index', 'monstats.[slot]');
add('hireling', 'Mode#', 'row-index', 'monmode.[slot]', 'Guide explicitly requires numeric ID, not mode name.');
add('levels', 'SoundEnv', 'row-index', 'soundenviron.[slot]');
add('levels', 'ObjGrp#', 'row-index', 'objgroup.[slot]', 'Zero is ignored.');
add('objgroup', 'ID#', 'row-index', 'objects.[slot]', 'Guide contains malformed %!objects!% markup; retain this correction.');
add('pettype', 'mclass#', 'row-index', 'monstats.[slot]');
add('superuniques', 'Mod#', 'row-index', 'monumod.[slot]');
add('shrines', 'LevelMin', 'row-index', 'levels.[slot]', 'Guide says index; verify whether actual data uses Id.');
add('monstats2', 'HitClass', 'row-index', 'hitclass.[slot]', 'Target inferred from field meaning; confirm before enabling.');
add('objects', 'ShrineFunction', 'numeric-key', 'shrines.Code', 'Function code lookup; multiple shrine rows may share it.');
add('monpet', 'monster', 'union-key', 'monstats.Id|superuniques.Superunique');
add('monpreset', 'Place', 'union-key', 'superuniques.Superunique|monstats.Id|monplace.code');
add('monstats', 'Sk#mode', 'union-key', 'monmode.code|monseq.sequence', 'Sequence contains a contiguous group of rows.');
add('states', 'gfxclass', 'conditional-index', 'monstats.[slot]|playerclass.[slot]', 'Target depends on gfxtype: 1 monster, 2 player class.');
add('automagic|magicprefix|magicsuffix|qualityitems', 'mod#code', 'property-code', 'properties.code|propertygroups.code');
add('cubemain', 'mod #', 'property-code', 'properties.code|propertygroups.code');
add('gems', 'Mod#Code', 'property-code', 'properties.code|propertygroups.code');
add('monprop|uniqueitems|setitems', 'prop#', 'property-code', 'properties.code|propertygroups.code');
add('propertygroups', 'prop#', 'property-code', 'properties.code|propertygroups.code', 'Group-to-group reference is self-referential; detect cycles.');
add('runes', 'T1Code#', 'property-code', 'properties.code|propertygroups.code');
add('setitems', 'aprop#', 'property-code', 'properties.code|propertygroups.code');
add('sets', 'PCode#|FCode#', 'property-code', 'properties.code|propertygroups.code');
for (const [tables, fields] of [
    ['automagic|magicprefix|magicsuffix|qualityitems', 'mod#param'], ['cubemain', 'mod # param'], ['gems', 'Mod#Param'],
    ['runes', 'T1Param#'], ['uniqueitems|setitems|monprop', 'par#'], ['setitems', 'apar#'], ['sets', 'PParam#|FParam#'], ['monpet', 'consumepar#']
]) add(tables, fields, 'conditional-parameter', 'skills.skill|montype.type|states.state',
    'Interpret paired property/stat and its function first. Numeric literals are often values, not row IDs.');
add('cubemain', 'input #|output', 'expression', 'itemtypes.Code|armor.code|weapons.code|misc.code|uniqueitems.index|setitems.index',
    'Parse modifiers and aliases. Bundled format says ItemType and sets.index: verify these apparent guide mistakes against authored data/runtime.');
add('treasureclassex', 'Item#', 'expression', 'treasureclassex.Treasure Class|armor.code|weapons.code|misc.code|uniqueitems.index|setitems.index',
    'Parse comma modifiers, special tokens, generated classes and recursive references.');
add('monstats2', 'COMPv', 'expression', 'compcode.code', 'Split component alternatives and preserve token positions.');
add('skills', 'sumumod', 'expression', 'monumod.[slot]', 'BBE expression whose result is a monster modifier index; literal-only shortcut requires validation.');
add('lvlprest|lvlsub', 'Dt1Mask', 'contextual-bitmask', 'lvltypes.File #', 'Bitmask picks columns in a context-selected level type; not a direct cell-value row key.');
add('hireling', 'NameFirst', 'localization', 'strings.Key', 'Endpoints of a range of localized names; include NameLast when present in project schema.');
add('lowqualityitems', 'Name', 'localization', 'strings.Key');
add('levels', 'LevelName|LevelWarp|LevelEntry', 'localization', 'strings.Key', 'Documented as display strings in prose; resolve against authored string catalogs.');
add('itemuicategories', 'Name', 'localization', 'strings.Key', 'Also the category identity.');

function mentions(text) {
    const result = new Set();
    for (const match of text.matchAll(/\(([^()]+)\)|\b([a-z][a-z0-9]+)\.txt\b|%!([^!]+)!%/gi)) {
        const table = (match[1] ?? match[2] ?? match[3]).toLowerCase();
        if (guide.files[table] || table === 'enums') result.add(table);
    }
    return [...result].sort();
}
const records = [];
for (const [table, doc] of Object.entries(guide.files)) for (const field of doc.fields) {
    const text = [field.description, field.format].filter(Boolean).join(' ');
    const proseTargets = mentions(text);
    const tableTargets = mentions(JSON.stringify([field.table ?? [], field.bits ?? []]));
    let decision = overrides.get(`${table}.${field.name}`);
    if (!decision && field.refFile) decision = { kind: field.refFile.toLowerCase() === 'enums' ? 'documentation-enum' : 'structured-key',
        targets: [`${field.refFile.toLowerCase()}.${field.refField ?? '[unspecified]'}`], note: 'Explicit guide type; confirm value encoding, sentinels and duplicates before enabling.' };
    if (!decision && (field.type === 'string' || /string key|key value.*name|localized string/i.test(text)))
        decision = { kind: 'localization', targets: ['strings.Key'], note: 'Search project localization catalogs, preserving duplicate keys and locale context.' };
    if (!decision && /BBE|Calc Fields/.test(field.format ?? ''))
        decision = { kind: 'calculation', targets: [], note: 'Parse named skill/stat/table operands within the expression; do not treat the entire formula as an ID.' };
    if (!decision && /\(enums\)/i.test(text)) decision = { kind: 'documentation-enum', targets: ['enums'], note: 'Function/enum documentation, not an editable record reference.' };
    if (!decision && proseTargets.length) decision = { kind: 'related-documentation', targets: proseTargets,
        note: 'Context, inverse dependency, asset or function mention; no proven direct value-to-row rule. See evidence before proposing navigation.' };
    if (!decision && tableTargets.length) decision = { kind: 'function-documentation', targets: tableTargets,
        note: 'Cross-links occur inside the guide lookup/bit table; navigate documentation or interpret function parameters, not arbitrary records.' };
    if (!decision && /\.dc6|\.dcc|\.ds1|\.dt1|\.wav|graphics.*file|animation.*file/i.test(text))
        decision = { kind: 'asset', targets: [], note: 'Asset path/index navigation belongs to asset previews, not table joins.' };
    decision ??= { kind: 'no-documented-row-link', targets: [], note: 'No cell-value record target identified in the bundled field metadata/prose; retained in JSON for coverage.' };
    const aliases = [...new Set([...(field.name.includes('#') ? [] : [field.name]), ...(field.altNames ?? []).filter(n => !n.includes('#'))])];
    records.push({ table, field: field.name, columns: aliases.length ? aliases : [field.name], ...decision,
        explicitReference: field.refFile ? { table: field.refFile, column: field.refField } : null,
        guideUrl: `${guide.source}files/${doc.key}.html#${encodeURIComponent(field.name)}`,
        description: field.description, format: field.format ?? null, mentionedTables: proseTargets, lookupTableMentions: tableTargets });
}
// Fail on stale curated names so future guide imports cannot silently drop planned links.
for (const key of overrides.keys()) if (!records.some(r => `${r.table}.${r.field}` === key)) throw new Error(`Unknown curated guide field: ${key}`);
const kinds = Object.fromEntries([...new Set(records.map(r => r.kind))].sort().map(kind => [kind, records.filter(r => r.kind === kind).length]));
const enabledKinds = new Set(['structured-key', 'item-code', 'property-code', 'prose-key', 'group-key', 'union-key', 'numeric-key', 'row-index', 'conditional-index', 'conditional-parameter', 'localization', 'expression', 'calculation', 'reverse-group']);
// These require additional game-side encoding/context evidence before a row is safe to choose.
const deferred = new Set(['armor.component', 'weapons.component', 'misc.component', 'armor.gemoffset', 'weapons.gemoffset', 'misc.gemoffset', 'shrines.LevelMin', 'monstats2.HitClass']);
const runtime = records.filter(r => enabledKinds.has(r.kind) && !deferred.has(`${r.table}.${r.field}`))
    .map(({ table, field, columns, kind, targets }) => ({ table, field, columns, kind, targets }));
writeFileSync(join(root, 'src/ModStudio.Core/Assets/cell-reference-rules.json'), JSON.stringify(runtime) + '\n');
const out = join(root, 'docs'); mkdirSync(out, { recursive: true });
writeFileSync(join(out, 'cell-reference-inventory.json'), JSON.stringify({ guideCommit: guide.commit, guideGenerated: guide.generated,
    source: 'src/ModStudio.Core/Assets/column-guide.json', tables: Object.keys(guide.files).length, fields: records.length,
    explicitReferences: records.filter(r => r.explicitReference).length, kinds, records }, null, 2) + '\n');
const escape = s => String(s).replaceAll('|', '\\|').replaceAll('\n', ' ');
const lines = ['# Bundled guide cell-reference inventory', '', `Guide commit: \`${guide.commit}\`; generated ${guide.generated}.`, '',
    `Audited ${Object.keys(guide.files).length} tables and ${records.length} documented field families, including ${records.filter(r => r.explicitReference).length} explicit references.`, '',
    `Generated by \`node scripts/audit-cell-references.mjs\`. ${runtime.length} field families are in the navigation registry; see cell-reference-plan.md for interpretation and exclusions.`, '',
    'The companion JSON retains every field, full description/format, expanded aliases and classification. A `#` with no explicit aliases remains a pattern to expand against the project schema; it is not one literal column.', '',
    'Structured keys are guide declarations. Curated prose mappings are implementation proposals; notes call out encoding and guide ambiguities. Related/function documentation is deliberately excluded from automatic record linking.', '',
    '| Classification | Field families |', '| --- | ---: |', ...Object.entries(kinds).map(([k,n]) => `| ${k} | ${n} |`), ''];
for (const [table, doc] of Object.entries(guide.files)) {
    const all = records.filter(r => r.table === table);
    const planned = all.filter(r => r.kind !== 'no-documented-row-link');
    lines.push(`## ${doc.key}`, '', `${all.length} field families reviewed; ${planned.length} entries below.`, '');
    if (!planned.length) { lines.push('No record-reference candidate identified in the bundled fields.', ''); continue; }
    lines.push('| Source columns / family | Kind | Targets | Implementation note |', '| --- | --- | --- | --- |');
    for (const r of planned) lines.push(`| ${escape(r.columns.join(', '))} | ${r.kind} | ${escape(r.targets.join('; ') || 'Expression operands / asset context')} | ${escape(r.note)} |`);
    lines.push('');
}
writeFileSync(join(out, 'cell-reference-inventory.md'), lines.join('\n') + '\n');
console.log(JSON.stringify({ tables: Object.keys(guide.files).length, fields: records.length, kinds }, null, 2));
