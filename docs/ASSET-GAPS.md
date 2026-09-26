# Asset gaps

Owner ruling (2026-09-26): gaps may be filled with **AI-generated** assets (provenance recorded) or procedural/typographic fallbacks; no licensed third-party material.

## Known gaps

| Gap | Fallback now | Plan |
|---|---|---|
| No font files in the asset set | Unity bundled default font | Optional generated display face (D-005) |
| No audio files | Procedural synth | Generated music for 8 contexts + stingers (D-006) |
| No Reaver readiness poses | `STANCE-READY` frame | Generate |
| `greatsword`/`sword-shield` suites lack `CONVERSATION` | `PORTRAIT` frame | Generate |
| 51 of 63 relics have no painting | Typographic sigil | Generate in the style of the 12 paintings (D-011) |
| No logo/wordmark | Typographic | Generate (IP-safe) |
| No card art in the imported set (F1 CardView) | Grey art box in `Kit/Card` | Import or generate card paintings (F2) |
| Unity's default font has no `⬡` (Smithing Stone glyph, 04 §0) | Glyph renders as an empty box, so the kit uses `◆` in samples | Generated display face or an icon sprite |
| Title gate ash particles (W-02) | None; the gate prompt pulses | Procedural particles in the SKIN step |
| Title background lit/unlit rule (`profile.wins > 0`) | Always `bg.title-city-tower` | Needs the profile service (F4) and a lit/unlit pair |

## Unused

Files under the scanned roots that no ID rule matches (0). They are not shipped.

