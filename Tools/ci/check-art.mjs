#!/usr/bin/env node
// Fails when an image the asset registry says is imported is not on disk (D-1300). Run after the CI art import.
//   node Tools/ci/check-art.mjs [--min <n>]
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const REGISTRY = join(ROOT, 'Unity', 'Assets', 'StreamingAssets', 'Content', 'assets', 'registry.json');
const minArg = process.argv.indexOf('--min');
const MIN = minArg > 0 ? Number(process.argv[minArg + 1]) : 1000;

const registry = JSON.parse(readFileSync(REGISTRY, 'utf8'));
const wanted = Object.entries(registry).filter(([, e]) => e.unityPath);
const missing = wanted.filter(([, e]) => !existsSync(join(ROOT, 'Unity', e.unityPath)));
console.log(`art: ${wanted.length - missing.length} of ${wanted.length} imported ids are on disk`);
if (missing.length) {
  console.error(`::error::art import incomplete: ${missing.length} images missing, e.g. ${missing.slice(0, 8).map(([id]) => id).join(', ')}`);
  process.exit(1);
}
if (wanted.length < MIN) {
  console.error(`::error::the registry lists only ${wanted.length} imported ids (expected at least ${MIN}); the registry is not the committed one`);
  process.exit(1);
}
