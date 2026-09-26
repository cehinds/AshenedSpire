# Ashen Spire (Unity rebuild)

A dark-fantasy, seeded, run-based tactical deckbuilder RPG, rebuilt in Unity 6000.6.0f1 as a fully data-driven game.

> **Made with generative AI.** Ashen Spire was created with generative AI. Its code, art, music, sound, text and design were produced with AI tools under human direction. The game contains no licensed third-party assets. Every generated asset's provenance is recorded in `Provenance/`.

- Design package: [`docs/design/`](docs/design/00-README.md). Start with the index; the build prompt is `09-UNITY-MEGA-PROMPT.md`.
- Build log: [`docs/BUILD-LOG.md`](docs/BUILD-LOG.md) · Decisions: [`docs/DECISIONS.md`](docs/DECISIONS.md)
- Version: see [`VERSION`](VERSION). The scheme is `E.F.S.P` (`<epic release>.<feature>.<user story>.<patch>`); see [`docs/design/10`](docs/design/10-BRANCHING-VERSIONING-CI.md).

## Layout

| Path | What |
|---|---|
| `Unity/` | The Unity project. Content JSON is in `Assets/StreamingAssets/Content` |
| `Tools/` | Content export and transform from the shipped web game, the parity oracle, the asset pipeline, `version.mjs` and `unity.sh` |
| `Tests/Domain.Tests` | Engine-free .NET tests of the Domain and Application assemblies |
| `Provenance/` | Credits and the generation manifests for all AI-made content |

## Common commands

```bash
node Tools/version.mjs current
Tools/unity.sh editmode -runTests -testPlatform EditMode -testResults Logs/editmode.xml
dotnet test Tests/Domain.Tests/Domain.Tests.csproj
```
