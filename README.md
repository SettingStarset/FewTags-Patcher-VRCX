# FewTags for VRCX

This Windows patcher overlays the FewTags-enabled frontend onto an existing VRCX installation. It is a standalone `.exe`, not a VRCX setup installer or a separate VRCX build. Choose the folder containing `VRCX.exe`; the default is `C:\Program Files\VRCX`.

## Patcher features

- Browse to a VRCX install folder, apply/update the integration, or restore the newest backup.
- Download and validate both `FewTagsv2.json` and legacy `FewTags.json` each time you apply. The databases are injected into the frontend index, avoiding runtime requests from VRCX's local `file://` page.
- Show tags only when a profile's exact user ID matches an active record. A v2 record takes precedence over legacy, even if the v2 entry is inactive.
- Render the supported FewTags rich text and animation prefixes, including `.RAIN.`, `.SCROLL.`, `.PULSE.`, and `.GLITCH.`.
- Back up only the changed frontend files with a manifest, and roll back a partial update if copying fails.
- Match the installed VRCX executable and library against the exact supported release fingerprints before changing files. The current release supports nightly `2026-10-18T13.40-d8619fb` and stable `v2026.09.16`; unknown builds are refused safely.
- When this project is published to its own GitHub repository, check for patcher releases at startup, ask before updating, verify the EXE's SHA-256 checksum, then replace and restart the patcher.

VRCX is installed under Program Files on many PCs. Run the patcher as administrator if Windows denies access to that folder. Close VRCX before updating; the app will ask it to close and stop if it remains open.

## Build the EXE locally

Requirements: Windows, Node.js 24 or later, npm 11 or later, and the .NET 7 SDK.

1. Check out each supported VRCX source release into separate `VRCX-nightly-source` and `VRCX-stable-source` folders.
2. Apply `integration/fewtags-vrcx.patch`, then run `npm ci` and `npm run prod` in each checkout.
3. Create `payload/nightly/VRCX-html.zip` and `payload/stable/VRCX-html.zip` with each built `html` folder at the ZIP root.
4. Publish the patcher:

   ```powershell
   dotnet publish app/VRCXFewTagsPatcher.csproj -c Release -r win-x64 --self-contained true `
     -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
     -p:VrcxNightlyReleaseTag=2026-10-18T13.40-d8619fb `
     -p:VrcxStableReleaseTag=v2026.09.16 `
     -p:VrcxNightlyExeSha256=... -p:VrcxNightlyDllSha256=... `
     -p:VrcxStableExeSha256=... -p:VrcxStableDllSha256=... `
     -p:PatcherRepository=SettingStarset/FewTags-Patcher-VRCX -o app/publish
   ```

The EXE is written to `app/publish/VRCX-FewTags-Patcher.exe`. For a local build before choosing a release repository, omit `-p:PatcherRepository=...`; self-updates stay disabled until it is set.

## Automatic releases

The workflow in `.github/workflows/release.yml` checks every four hours for the latest nightly and stable releases and also runs when the patcher or integration changes. It verifies the integration patch applies cleanly to both sources, builds both frontends, embeds them in a single-file patcher EXE, writes a SHA-256 file, and publishes a GitHub release only after both builds succeed. The patcher matches the installed VRCX native binaries to the corresponding embedded frontend. Each generated patcher uses its own repository's releases for opt-in self-updates. `workflow_dispatch` can force a rebuild.

The reference patcher builds and replaces a complete VRCNext installation. This VRCX patcher keeps the original approach of overlaying only the frontend files into the selected existing installation.

## Restore

Open the patcher, choose the same VRCX folder, and select **Restore backup**. It recognizes both the current per-file manifest backups and earlier full-frontend backups.

FewTags is community-maintained data and may be inaccurate or opinion-based. The integration displays the source tags without verifying their claims.
