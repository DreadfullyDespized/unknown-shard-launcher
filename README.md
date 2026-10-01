# Shard Launcher

This is an open-source (MIT) auto-updater and launcher for an Ultima Online free shard. It is generic: it holds **no server-specific values**. The server address, the patch URL and the trusted signing key are supplied at build time (see [Configuring a build](#configuring-a-build)).

It downloads the shard's client-art updates and verifies them. It then launches a pinned, official [ClassicUO](https://github.com/ClassicUO/ClassicUO) build.

- This repo contains **no secrets, no shard art and no server configuration**. Art is fetched at runtime from the patch base URL that was compiled in.
- Every update manifest is signed with ECDSA P-256. The launcher only accepts manifests that verify against a public key embedded in the exe at build time.
- Every file is checked for size and SHA-256 before it is used.
- Writes are additive only:
  - the launcher's own folder, `%LOCALAPPDATA%\<LauncherDataDirName>\`;
  - new `Gumps\*.gump` files in your UO folder, tracked in a ledger (`.shardlauncher-owned.json`).

  Stock UO files are never modified.

## Configuring a build
The server-specific values come from an MSBuild props file that is **not committed**. `Directory.Build.props` imports it from one of two places:
- the path given with `-p:LauncherBuildConfig=/abs/path/launcher.build.props`, which is how the private server repo supplies it in CI; or
- `launcher.build.props` in the repo root (gitignored, like `keys/`).

Start from [`launcher.build.example.props`](launcher.build.example.props), which holds only placeholders:

| Property / item | Meaning |
|---|---|
| `LauncherDisplayName` | Window title and product name |
| `LauncherDataDirName` | Folder under `%LOCALAPPDATA%` (no path separators) |
| `LauncherServerHost`, `LauncherServerPort` | Default game server passed to ClassicUO as `-ip` / `-port` |
| `LauncherPatchBaseUrl` | `https://` base of the patch server; must end with `/` |
| `LauncherTrustedKey` item (`KeyId` metadata) | ECDSA P-256 **public** key PEM (`<PUBLIC_KEY_PEM>`). `KeyId` must equal the manifest's `signing_key_id`. To rotate keys, add a second item. |
| `LauncherAssemblyName` (optional) | Exe name (default `ShardLauncher`) |

At build time the values are compiled into a generated `BuildSettings.g.cs` under `obj/`, and the keys become embedded resources. The keys cannot be supplied at runtime **on purpose**: a key loaded from a user-editable file would let anyone swap it and defeat signature checking. The build refuses a file that contains a private key.

- A build **without** a config still compiles and passes all tests, because the tests generate throwaway keys. At startup the launcher shows a clear configuration error listing what is missing and does not update or launch.
- To make a release build fail instead, add `-p:RequireLauncherConfig=true`.

**Runtime override (server address only):** `%LOCALAPPDATA%\<LauncherDataDirName>\server.json` may contain `{"host": "...", "port": 1234}`. Any other setting (patch URL, keys), or an invalid value, is rejected with an error. It is never ignored silently.

## ⚠️ The launcher is **unsigned**
We do not pay for a code-signing certificate, so Windows SmartScreen and some antivirus products may warn on first run.

### First-run steps
1. Download the launcher exe and compare its SHA-256 with the value published on your shard's download page.
2. When SmartScreen appears, click **More info → Run anyway**.
3. Pick your Ultima Online data folder when asked.
4. Wait for the progress bar to finish, then click **Play**.

## Layout
- `src/ShardLauncher.Patching/`: the shared manifest core. It holds the schema, canonical JSON, the path allowlist, ECDSA P-256 verification and the serial guard. Keep it in sync with the copy used by the manifest signing tool.
- `src/ShardLauncher.Core/`: the updater pipeline: fetch, verify, diff, stage, verify, promote. It also holds the Gumps ledger, the launch command, `LauncherConfig` (build-time settings and the runtime server override) and `TrustedKeys` (embedded public keys).
- `src/ShardLauncher/`: the WinForms UI (net8.0-windows, asInvoker): a status line, a progress bar and a Play button.
- `tests/ShardLauncher.Tests/`: xunit tests that run on Linux or Windows. They use placeholder `*.example.invalid` hosts and keys generated at runtime.

## Build from source
```
dotnet test ShardLauncher.sln -c Release
dotnet publish src/ShardLauncher -c Release -r win-x64 --self-contained false -o out \
  -p:LauncherBuildConfig=/abs/path/launcher.build.props -p:RequireLauncherConfig=true
```
Linux builds work because `EnableWindowsTargeting=true` is set. The app itself runs on Windows only.

## License
MIT, see [LICENSE](LICENSE). ClassicUO is BSD 2-Clause and is redistributed unmodified with its notice.
