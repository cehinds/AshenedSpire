# WebGL build

The game runs in a browser from a Unity WebGL player (D-1250..D-1254). Unity is pinned to 6000.6.6f1.

## Build locally

Install 6000.6.6f1 in Unity Hub with the WebGL Build Support module, then:

```
Tools/unity.sh webgl -executeMethod Ashen.EditorTools.Cli.BuildWebGL -quit
```

Output: `Builds/WebGL` (gitignored), or the folder given with `-customBuildPath`. The method validates the content, sets the WebGL player settings in code (Brotli with the JavaScript decompression fallback, no threads, IndexedDB data cache) and builds the scenes in Build Settings.

## Serve locally

```
npx http-server Builds/WebGL -p 8080 --brotli --gzip
```

Open http://localhost:8080/. The decompression fallback means any static server works, even one that sends no `Content-Encoding` header (GitHub Pages is one). Do not open `index.html` from disk: the player fetches its files over HTTP.

## Publish to GitHub Pages

`.github/workflows/pages-webgl.yml` builds on every push to `dev` and deploys with `actions/deploy-pages`. Owner steps, once:

1. Install Unity 6000.6.6f1 with the WebGL module (Unity Hub) for local builds.
2. Repo Settings > Pages > Source = GitHub Actions.
3. Repo Settings > Secrets and variables > Actions: `UNITY_LICENSE`, `UNITY_EMAIL`, `UNITY_PASSWORD` (game-ci activation; without `UNITY_LICENSE` the jobs print a notice and skip).

The site is https://cehinds.github.io/AshenedSpire/. `ci.yml` also has a `build-webgl` job (same branch condition as `build-windows`) that uploads the build as an artifact.

## How the browser differs

- **Content**: a browser cannot read StreamingAssets with `System.IO`, so `AshenBoot` fetches `manifest.json` and every file it lists into a `MemoryContentSource` before building the UI (`WebContentLoader`). Windows and the editor are unchanged.
- **Saves**: `persistentDataPath` is IndexedDB behind an in-memory file system. `WebFileSystem` flushes it every 5 seconds, on pause and on losing focus. Saves are per browser and per origin; clearing site data deletes them.
- **No threads**: WebGL is single-threaded (`threadsSupport` off).

## Known gaps

- **Art in CI**: the converted images under `Unity/Assets/Art/Imported` are gitignored (D-018) and neither `build-windows` nor the WebGL jobs restore them, so a CI build has no images. A local build on a machine that ran the asset import has them. Decide where CI gets them (commit, LFS, or an import step from the web repo).
- **Not verified**: no Unity editor ran when this was written. The first build must confirm the UI Toolkit `CopyBufferRanges` WebGL renderer bug of 6000.6.0f1 is gone, that the `PlayerSettings.WebGL` calls in `Cli.cs` compile, that saves survive a reload, and the download size. See D-1254.
- **Packages**: opening the project in 6000.6.6f1 may update `Unity/Packages/packages-lock.json`; commit it.
