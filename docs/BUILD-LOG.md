# Build log

Newest entries go at the top. Each entry records the story, what landed, the tests, what wasn't verified, and the next step. After a context summary, re-read this file and `DECISIONS.md` before continuing.

## Phase F0 Foundation (in progress)

### Preflight (2026-09-26)

| Check | Result |
|---|---|
| Unity editor | `D:\Unity\6000.6.0f1\Editor\Unity.exe` ✓ |
| Unity licence | Unity Personal (ULF + Assigned), unlimited ✓ |
| Unity MCP tools | **Not available.** Using the batchmode fallback (D-023) |
| UPM cache | Global `cacheRoot` points at a missing `H:` drive; `UPM_CACHE_ROOT` is overridden in `Tools/unity.sh` (D-024) |
| Blender | 4.0 ✓ |
| Python | 3.11.9 in Git Bash (3.13 first on the Windows PATH); Pillow **not** installed |
| Node | 22.14.0 ✓ |
| .NET SDK | 8.0.303, 8.0.408, 9.0.305 ✓ |
| Edge | ✓ |
| gh | logged in as cehinds ✓ |
| Packages (bundled with the editor) | URP 17.6.0 · Input System 1.20.0 · Addressables 2.11.2 · Newtonsoft 3.2.2 · Test Framework 1.8.0 · Vector Graphics module (built-in) |

Branches: lane `feature/foundation/main`; current story branch `feature/foundation/us-0.9`.
