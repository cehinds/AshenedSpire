// The tally of what a Core 2 fight oracle's filter leaves out (WP-C0, docs/design/11-CORE2-PORT-PLAN.md). Every fight oracle drives a bot and records a step only
// when it stays inside the filter; every candidate the filter turns away is counted here under the reason it gave, and `--why` prints the tally. The target of
// WP-C0 is an empty tally for every feature a default Core 2 run reaches: what is left in it names what the port refuses by name.
//
//   const REJ = whyTally('damage');
//   if (!playable) return REJ.note('card: a gate');      // false, and counted
//   return REJ.why('throws: ...');                         // { ok: false, why }, and counted
//   REJ.finish();                                          // prints with --why; returns the tally for the index
export function whyTally(name) {
  const rejected = {};
  const note = (reason) => {
    const key = String(reason).replace(/[0-9]+/g, 'N').slice(0, 110);
    rejected[key] = (rejected[key] || 0) + 1;
    return false;
  };
  return {
    note,
    why: (reason) => { note(reason); return { ok: false, why: reason }; },
    rejected,
    total: () => Object.values(rejected).reduce((sum, n) => sum + n, 0),
    /** The tally, sorted; printed when the run was asked `--why`. */
    finish() {
      const sorted = Object.fromEntries(Object.entries(rejected).sort(([a], [b]) => (a < b ? -1 : 1)));
      if (process.argv.includes('--why')) console.log(`${name}: ${Object.values(rejected).reduce((sum, n) => sum + n, 0)} candidates turned away by the filter`, JSON.stringify(sorted, null, 1));
      return sorted;
    },
  };
}
