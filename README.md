**Repo label: TEST** — see [CONTRIBUTING.md](CONTRIBUTING.md#repo-label-test) for the required process.

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

## License
MIT, see [LICENSE](LICENSE). ClassicUO is BSD 2-Clause and is redistributed unmodified with its notice.
