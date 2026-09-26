#!/usr/bin/env node
// Bootstrap JSON schemas for StreamingAssets/Content (docs/design/08 §4).
// Run ONCE (or with --only <file>) to seed schemas from the imported data; afterwards the committed
// schemas are the source of truth and the validator checks the data against them.
//
//   node Tools/infer-schemas.mjs --write [--only catalog/cards.json]
//
// Subset: type (union allowed), properties, required, items, enum, additionalProperties,
// plus x-keyed (id-keyed catalog: every property value matches x-row), x-ref (from Tools/schema-refs.json).
import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const CONTENT = join(ROOT, 'Unity', 'Assets', 'StreamingAssets', 'Content');
const SCHEMAS = join(CONTENT, 'schemas');
const REFS = JSON.parse(readFileSync(join(ROOT, 'Tools', 'schema-refs.json'), 'utf8'));
const WRITE = process.argv.includes('--write');
const onlyIdx = process.argv.indexOf('--only');
const ONLY = onlyIdx > 0 ? process.argv[onlyIdx + 1] : null;
const ENUM_MAX = 12, ENUM_MIN_SAMPLES = 6, MAX_DEPTH = 8;

const typeOf = (v) => v === null ? 'null' : Array.isArray(v) ? 'array' : Number.isInteger(v) ? 'integer' : typeof v;
function merge(types) { const s = new Set(types); if (s.has('integer') && s.has('number')) s.delete('integer'); return [...s].sort(); }

function infer(samples, depth, path) {
  const types = merge(samples.map(typeOf));
  const schema = { type: types.length === 1 ? types[0] : types };
  if (depth > MAX_DEPTH) return schema;
  if (types.includes('object')) {
    const objs = samples.filter(s => typeOf(s) === 'object');
    const keys = [...new Set(objs.flatMap(o => Object.keys(o)))].sort();
    // Maps with data-shaped keys (ids) are inferred as additionalProperties rather than fixed properties.
    const mapLike = keys.length > 24 || REFS.mapPaths.includes(path);
    if (mapLike) schema.additionalProperties = infer(objs.flatMap(o => Object.values(o)), depth + 1, `${path}.*`);
    else {
      schema.properties = {};
      for (const k of keys) schema.properties[k] = infer(objs.filter(o => k in o).map(o => o[k]), depth + 1, `${path}.${k}`);
      const req = keys.filter(k => objs.every(o => k in o));
      if (req.length) schema.required = req;
    }
  }
  if (types.includes('array')) {
    const items = samples.filter(Array.isArray).flat();
    schema.items = items.length ? infer(items, depth + 1, `${path}[]`) : {};
  }
  if (types.length === 1 && types[0] === 'string') {
    const distinct = [...new Set(samples)];
    if (samples.length >= ENUM_MIN_SAMPLES && distinct.length <= ENUM_MAX && !REFS.freeText.some(f => path.endsWith(f))) schema.enum = distinct.sort();
  }
  const ref = REFS.refs.find(r => new RegExp(r.path).test(path));
  if (ref) { schema['x-ref'] = ref.table; delete schema.enum; }
  return schema;
}

const manifest = JSON.parse(readFileSync(join(CONTENT, 'manifest.json'), 'utf8'));
const keyed = new Set(JSON.parse(readFileSync(join(ROOT, 'Tools', 'transform.config.json'), 'utf8')).tables.filter(t => !t.list).map(t => t.out));
let written = 0;
for (const file of Object.keys(manifest.files)) {
  if (ONLY && file !== ONLY) continue;
  if (file === 'strings/en.json') continue; // strings are validated by key convention, not by schema
  const data = JSON.parse(readFileSync(join(CONTENT, file), 'utf8'));
  const name = file.replace(/\//g, '.').replace(/\.json$/, '');
  let schema;
  if (keyed.has(file)) {
    schema = { $id: file, 'x-keyed': true, type: 'object', additionalProperties: infer(Object.values(data), 1, name) };
  } else {
    schema = { $id: file, ...infer([data], 0, name) };
  }
  const out = join(SCHEMAS, `${name}.schema.json`);
  if (WRITE) {
    if (existsSync(out) && !ONLY) continue; // never overwrite a committed schema unless targeted
    mkdirSync(SCHEMAS, { recursive: true });
    writeFileSync(out, `${JSON.stringify(schema, null, 2)}\n`);
    written++;
  }
}
console.log(`infer-schemas: ${WRITE ? `wrote ${written}` : 'dry run (pass --write)'}`);
