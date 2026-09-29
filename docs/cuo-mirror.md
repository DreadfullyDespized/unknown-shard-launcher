# Pinned ClassicUO mirror (unknown-shard#194)

The launcher never downloads ClassicUO from GitHub. The upstream `ClassicUO-main-release` assets are re-uploaded in place, so the same URL returns different bytes over time; on 2026-09-29, for example, the win-x64 asset was updated at 01:25 CT. Instead, the shard host mirrors **one** official zip, content-addressed, and the signed manifest pins it.

No ClassicUO binaries are committed to any git repo.

## Manifest entry (schema 1; this field already exists in `PatchManifest.Cuo`)
```json
"cuo": {
  "file": "objects/<sha256 of the zip>",
  "sha256": "<sha256 of the zip>",
  "size": 19629510,
  "version": "ClassicUO-main-release@<commit>",
  "authenticode_subject": "SignPath Foundation"
}
```
- `file`, `sha256` and `size`: the validator enforces `file == objects/<sha256>`. The launcher enforces the exact size while streaming and checks the SHA-256 before extracting.
- `version`: informational. The publisher must pick a build from main **2026-09-08 or later**, because loose `Gumps\` support is required.
- `authenticode_subject`: must equal the signer CN of `ClassicUO.exe`, **and** be in the launcher's compiled-in `Authenticode.PinnedSubjects` (currently only `SignPath Foundation`). A compromised manifest therefore cannot name a different signer.

## Launcher behaviour (`CuoInstaller`)
1. **Download:** the zip is fetched and verified only if `cuo\.cuo-install.json` names a different zip SHA-256. A second run downloads nothing.
2. **Extract** into `cuo.tmp-<guid>`, refusing any of the following:
   - absolute or drive paths, `..`, `\`-traversal, `:` (ADS);
   - unusual characters;
   - symlink entries;
   - more than 2000 entries;
   - more than 1 GiB expanded (counted while inflating; header sizes are not trusted).
3. **Check the exe:** `ClassicUO.exe` must exist at the zip root. `WinVerifyTrust` (generic verify v2, no UI) must pass, and the signer CN must equal the pinned subject.
4. **Write** `UnknownShard-THIRD-PARTY-NOTICES.txt` (the license notices, see below) and the hash index `.cuo-install.json`.
5. **Swap:** move `cuo` to `cuo.old-*`, then `cuo.tmp-*` to `cuo`, then delete the old copy. If the move fails (for example, CUO is running), the old install stays.
6. **Before every Play:** re-hash every indexed file and re-run the Authenticode check. An altered `ClassicUO.exe` is refused and Play stays disabled.

A refused ClassicUO update never blocks art or gumps updates, and it never removes the working client.

Revocation is not checked online (`WTD_REVOKE_NONE` with cache-only URL retrieval), so offline players can still start. The SHA-256 pin in the signed manifest is the primary control.

## Mirroring procedure (host side; to be wired into #190/#191)
1. On a Windows host, download `ClassicUO-win-x64-release.zip` from https://github.com/ClassicUO/ClassicUO/releases/tag/ClassicUO-main-release.
2. Expand it and check the signature: `Get-AuthenticodeSignature .\ClassicUO.exe`. The status must be `Valid` and the signer subject must contain `CN=SignPath Foundation`.
3. Run `Get-FileHash -Algorithm SHA256` on the zip, then copy the zip to `C:\unknown-shard-web\patch\objects\<sha256>`. Never overwrite an existing object.
4. Put the `cuo` block into the manifest source.
   - Follow-up: ManifestTool (unknown-shard #200) does not yet read a `cuo` block from `manifest.src.json`. That is a small addition in the shard repo; the core schema and validator already handle it.

Observed 2026-09-29 (a reference point only; the asset may change again):
- Asset `ClassicUO-win-x64-release.zip`: 19,629,510 B, SHA-256 `cb556a076ba7dfa6a200255e36f57a236813801cdec5e106226117dcde536473`.
- The zip holds 22 flat entries, including `ClassicUO.exe` (213,288 B) and **no license files**.
- `ClassicUO.exe` signer: `CN=SignPath Foundation, O=SignPath Foundation, L=Lewes, ST=Delaware, C=US`. Issuer: `GlobalSign GCC R45 CodeSigning CA 2020`.
- ClassicUO main HEAD was `ee79d7eb` (2026-09-29 01:18 CT), which is later than the 2026-09-08 minimum.

## License finding
- **ClassicUO is BSD 2-Clause** (https://github.com/ClassicUO/ClassicUO/blob/main/LICENSE.md). That license allows redistributing binaries, with or without modification. The condition: *"Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution."*
  - Mirroring the unmodified signed zip is therefore permitted, **provided we ship the notice**.
  - The upstream zip contains no license file, so the launcher writes the notices file into `cuo\`.
  - The same text is in this repo (`notices/`) and should also go on the download page (#197/#198).
- **Bundled components** carry their own licenses:
  - FNA: Ms-PL.
  - FNA3D, FAudio, Theorafile, SDL3, zlib: zlib License.
  - System.* DLLs: MIT.
  - MP3Sharp: **LGPL-3.0**.

  We redistribute them unmodified, with notices and upstream source links. Not verified, and flagged for Dread: whether MP3Sharp is statically merged into `cuo.dll`, and what that means for LGPL relinking obligations. That is an upstream ClassicUO packaging question; we mirror the zip byte-for-byte. This is not legal advice.
