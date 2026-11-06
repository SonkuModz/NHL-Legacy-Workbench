# Reference map for Workbench 2.0

Where to look before writing or changing code for the PS3 recomp target.

Reviewed at these commits:

- Modding Studio: `d5576cc` (14 Oct 2026), https://github.com/puckhead73/nhl-modding-studio
- PS3 recomp: `5f05e64` (1 Nov 2026), https://github.com/csrpfivem/PS3NHLLegacyRecomp

Studio's local copy in `references/my old legacy tool/References/nhl-modding-studio-sourcecode` is older than GitHub. Use GitHub.

## PS3 recomp: how the game and mods work

| Topic | Path in the recomp repo |
|---|---|
| Install folder layout | `nhl/launcher/installer.h` |
| How mods are combined (load order, hard links) | `compose_hdd0` in `nhl/launcher/installer.cpp` |
| What a mod pack can look like | `nhl/launcher/mod_import.cpp` |
| Roster packs (`SYS-DATA`, save naming) | `build_roster_save` and `apply_roster` in `nhl/launcher/installer.cpp` |
| Headless launcher flags (`--import-mod`, `--compose`, `--apply-roster`) | `nhl/launcher/main.cpp` |
| PS3 EB archive reader | `tools/eb_extract.py` |
| PS3 `chunkzip` decoder | `tools/chunkzip.py` |
| RefPack decoder | `tools/refpack.py` |
| RPSGL texture extractor | `tools/rpsgl_extract.py` |
| Development history and known bugs | `docs/notes/NEXT_STEPS.md`, `docs/notes/LOCAL_PATCHES.md` |

## Modding Studio: formats and evidence

| Topic | Path in the Studio repo |
|---|---|
| Legacy PS3 test results | `docs/qa/overnight-2026-10-03/nhllegacy.md` |
| Legacy PS3 asset work | `docs/validation/legacy-ps3-assets-2026-10-11.md` |
| What works on Legacy PS3, by feature | `docs/audits/data/surface-capabilities.csv` (filter on `ps3,nhllegacy`) |
| Legacy PS3 database schema | `docs/audits/data/schema/ps3-nhllegacy.json` |
| Database format | `docs/formats/tdb-v6-format-spec.md`, `docs/formats/tdb-v6-format.md` |
| PS3 save and roster container | `docs/formats/ps3-save-container.md` |
| Safe database editing rules | `docs/EDIT-MODEL.md` |
| Archive codecs (`chunkzip`, RefPack) | `crates/tdb-archive-codec/src/lib.rs` |
| EB archive rewriting | `crates/tdb-archive-big/src/stage.rs` |
| EB format notes (written for PS4, so check against PS3) | `eb-big-format.md` |
| PS3 textures | `crates/tdb-rx2/src/texlib_ps3.rs`, `docs/research/overnight-2026-09-18/18-rw4ps3-texlib-layout.md` |
| PS3 meshes (EDGE) | `crates/tdb-rx2/src/ps3_mesh.rs`, `research/rpsgl2-review/README.md` |
| PS3 arena boards | `docs/research/overnight-2026-09-28/10-ps3-board-raster-writer-contract.md` |
| Arena and venue data | `docs/research/overnight-2026-09-21-pm/09-arena-venue-parameter-inventory.md`, `19-gen7-arena-venue-asset-join.md` in the same folder |
| Scorebug and overlays | `docs/research/overnight-2026-09-21-pm/16-scorebug-overlay-inventory.md`, `docs/research/overnight-2026-09-26/23-scorebug-team-assets.md` |
| Arena horns and chants | `docs/research/overnight-2026-09-22-pm/10-gen7-arena-horn-chant-bank-join.md` |
| ABK and other audio | `crates/tdb-snd/src/abk.rs` |
| Team and asset identity data | `docs/audits/data/assets/nhllegacy/` (Xbox 360 only, so check against PS3) |

## Rules for using these

- **Studio's "Legacy Recompiled" means the Xbox-based port.** Its PS3 target and Mod Manager were tested around RPCS3, not this recomp.
- **Studio's PS3 results are file-level.** Its Legacy PS3 report says edited gameplay was not tested, so anything we take from it still needs an in-game test.
- **Don't copy `anchors/` into this project or a release.** That folder holds real EA databases and saves. Use it only as local test data.
- **Check licences before copying code.** The recomp has an MIT `LICENSE` file. Studio's README declares MIT, but this commit has no `LICENSE` file. Learning from the format docs is fine; settle this with the author before copying Studio code.
