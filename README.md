# Unknown Shard Launcher

This is the open-source (MIT) auto-updater and launcher for the **Unknown Shard** Ultima Online free shard.

It downloads the shard's client-art updates and verifies them. It then launches a pinned, official [ClassicUO](https://github.com/ClassicUO/ClassicUO) build.

- This repo contains **no secrets and no shard art**. Art is fetched at runtime from `https://unknownshard.ddns.net/patch/`.
- Every update manifest is signed with ECDSA P-256. The launcher only accepts manifests that verify against the public key embedded in the exe.
- Every file is checked for size and SHA-256 before it is used.
- Writes are additive only:
  - the shard's own folder, `%LOCALAPPDATA%\UnknownShard\`;
  - new `Gumps\*.gump` files in your UO folder, tracked in a ledger.

  Stock UO files are never modified.

## ⚠️ The launcher is **unsigned**
We do not pay for a code-signing certificate, so Windows SmartScreen and some antivirus products may warn on first run.

### First-run steps (placeholder, finalized in unknown-shard#197)
1. Download `UnknownShardLauncher.exe` and compare its SHA-256 with the value published on the download page.
2. When SmartScreen appears, click **More info → Run anyway**.
3. Pick your Ultima Online data folder when asked.
4. Wait for the progress bar to finish, then click **Play**.

VirusTotal link and build-from-source instructions: *TBD* (unknown-shard#197/#198).

## Layout
- `src/UnknownShard.Patching/`: the shared manifest core. It holds the schema, canonical JSON, the path allowlist, ECDSA P-256 verification and the serial guard. It is byte-identical with `tools/ManifestTool/src/UnknownShard.Patching/` in the shard repo, which is the code that signs manifests.
- `src/UnknownShard.Launcher.Core/`: the updater pipeline: fetch, verify, diff, stage, verify, promote. It also holds the Gumps ledger, the launch command and the embedded public key (`keys/patch-signing-public.pem`, key id `us-2026a`, SHA-256 `ca2b00c134ed7a0a2f6e99811c233ef0141895eba05670e89c338d0fb54773a4`).
- `src/UnknownShardLauncher/`: the WinForms UI (net8.0-windows, asInvoker): a status line, a progress bar and a Play button.
- `tests/UnknownShard.Launcher.Tests/`: xunit tests that run on Linux or Windows.

## Build from source
```
dotnet test UnknownShardLauncher.sln -c Release
dotnet publish src/UnknownShardLauncher -c Release -r win-x64 --self-contained false -o out
```
Linux builds work because `EnableWindowsTargeting=true` is set. The app itself runs on Windows only.

## License
MIT, see [LICENSE](LICENSE). ClassicUO is BSD 2-Clause and is redistributed unmodified with its notice.
