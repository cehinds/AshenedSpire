// Node ESM loader hook for the Core 2 enemy oracle (WP-11). The web keeps rollIntents, weightedMovePick, presentIntent and buildIntent private to
// src/engine/combat.js; the oracle must drive the SHIPPED functions, not a copy of them. This hook appends one export line to the source of that
// module as it is loaded (the checkout on disk is never touched, the pinned-sha guard still holds): nothing else changes.
//
//   import { register } from 'node:module';
//   register('./oracle-core2-hook.mjs', import.meta.url);   // before the web modules are imported
const EXPORTS = {
  '/src/engine/combat.js': '\nexport { weightedMovePick as __weightedMovePick, rollIntents as __rollIntents, presentIntent as __presentIntent, buildIntent as __buildIntent, startDelayedMove as __startDelayedMove, executeMovePayload as __executeMovePayload, enemyPhase as __enemyPhase };\n',
};

export async function load(url, context, nextLoad) {
  const loaded = await nextLoad(url, context);
  const key = Object.keys(EXPORTS).find((suffix) => url.endsWith(suffix));
  if (!key) return loaded;
  return { ...loaded, source: `${String(loaded.source)}${EXPORTS[key]}` };
}
