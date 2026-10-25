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
4. `WEB_REPO_TOKEN` (a fine-grained personal access token with read access to Contents of `cehinds/ashenspire`), only while that repo is private. The jobs use `secrets.WEB_REPO_TOKEN || github.token`; the default token cannot read another repository, so a private web repo fails at the checkout step without it. The art repo `cehinds/AshenSpire-art` is public and needs nothing.

The site is https://cehinds.github.io/AshenedSpire/. `ci.yml` also has a `build-webgl` job (same branch condition as `build-windows`) that uploads the build as an artifact.

## Art in CI

`.github/actions/fetch-art` (used by `build-windows`, `build-webgl` and `pages-webgl.yml`) runs `Tools/ci/fetch-art.sh`: sparse checkout of `cehinds/ashenspire` at the commit in `Tools/ci/art-pin.json`, the web's `tools/fetch-art.mjs` downloads the pinned `hd-assets-v<N>` high and common packs from cehinds/AshenSpire-art (cached in `.art-web/.art-cache` by tag), `npm ci` in `Tools`, then `Tools/scan-assets.mjs --release ... --import` converts them (about 1500 images, 230 MB, a minute or two) and `Tools/ci/check-art.mjs` fails the job if any imported id is missing. `Cli.BuildWebGL` and `Cli.BuildWindows` then run `BuildUiArt` so the `UiArtCatalog` lists the textures. To run it locally: clone the web at the pinned commit, then `bash Tools/ci/fetch-art.sh <web clone>`. When the web pin or art release moves, update `Tools/ci/art-pin.json` (the script stops if the tag disagrees with the web's `art-release.json`).

## How the browser differs

- **Content**: a browser cannot read StreamingAssets with `System.IO`, so `AshenBoot` fetches `manifest.json` and every file it lists into a `MemoryContentSource` before building the UI (`WebContentLoader`). Windows and the editor are unchanged.
- **Saves**: `persistentDataPath` is IndexedDB behind an in-memory file system. `WebFileSystem` flushes it every 5 seconds, on pause and on losing focus. Saves are per browser and per origin; clearing site data deletes them.
- **No threads**: WebGL is single-threaded (`threadsSupport` off).

## Known gaps

- **Art in CI**: the converted images under `Unity/Assets/Art/Imported` are gitignored (D-018), so every Unity CI job restores them first with `.github/actions/fetch-art` (D-1300, D-1301); see below. The first run is the proof: it was tested step by step locally but never on a runner.
- **Not verified**: no Unity editor ran when this was written. The first build must confirm the UI Toolkit `CopyBufferRanges` WebGL renderer bug of 6000.6.0f1 is gone, that the `PlayerSettings.WebGL` calls in `Cli.cs` compile, that saves survive a reload, and the download size. See D-1254.
- **Packages**: opening the project in 6000.6.6f1 may update `Unity/Packages/packages-lock.json`; commit it.
