# FewTags for VRCX

This Windows patcher overlays the FewTags-enabled frontend onto an existing VRCX installation. It is a standalone `.exe`, not a VRCX setup installer or a separate VRCX build. Choose the folder containing `VRCX.exe`; the default is `C:\Program Files\VRCX`.

## Patcher features

- Browse to a VRCX install folder, apply/update the integration, or restore the newest backup.
- Download and validate both `FewTagsv2.json` and legacy `FewTags.json` each time you apply. The databases are injected into the frontend index, avoiding runtime requests from VRCX's local `file://` page.
- Show tags only when a profile's exact user ID matches an active record. A v2 record takes precedence over legacy, even if the v2 entry is inactive.
- Render the supported FewTags rich text and animation prefixes, including `.RAIN.`, `.SCROLL.`, `.PULSE.`, and `.GLITCH.`.
- Back up only the changed frontend files with a manifest, and roll back a partial update if copying fails.
- Check that the patcher's VRCX frontend is not older than the latest VRCX release or the installed VRCX package version.
- When this project is published to its own GitHub repository, check for patcher releases at startup, ask before updating, verify the EXE's SHA-256 checksum, then replace and restart the patcher.

VRCX is installed under Program Files on many PCs. Run the patcher as administrator if Windows denies access to that folder. Close VRCX before updating; the app will ask it to close and stop if it remains open.

## Build the EXE locally

Requirements: Windows, Node.js 24 or later, npm 11 or later, and the .NET 7 SDK.

1. Check out the VRCX source at the release tag shown in `app/VRCXFewTagsPatcher.csproj` into `VRCX-source`.
2. Apply `integration/fewtags-vrcx.patch` to that source checkout.
3. Run `npm install --no-package-lock` and `npm run prod` from `VRCX-source`.
4. Create `payload/VRCX-html.zip` with the built `html` folder at the ZIP root.
5. Publish the patcher:

   ```powershell
   dotnet publish app/VRCXFewTagsPatcher.csproj -c Release -r win-x64 --self-contained true `
     -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
     -p:PatcherRepository=OWNER/REPOSITORY -o app/publish
   ```

The EXE is written to `app/publish/VRCX-FewTags-Patcher.exe`. For a local build before choosing a release repository, omit `-p:PatcherRepository=...`; self-updates stay disabled until it is set.

## Automatic releases

The workflow in `.github/workflows/release.yml` checks every four hours for a new VRCX release and also runs when the patcher or integration changes. It verifies the integration patch applies cleanly, builds the latest compatible VRCX frontend, embeds that frontend in a single-file patcher EXE, writes a SHA-256 file, and publishes a GitHub release only after the build succeeds. Each generated patcher uses its own repository's releases for opt-in self-updates. `workflow_dispatch` can force a rebuild.

The reference patcher builds and replaces a complete VRCNext installation. This VRCX patcher keeps the original approach of overlaying only the frontend files into the selected existing installation.

## Restore

Open the patcher, choose the same VRCX folder, and select **Restore backup**. It restores only the files from the most recent `FewTagsVRCXBackup-*` manifest.

FewTags is community-maintained data and may be inaccurate or opinion-based. The integration displays the source tags without verifying their claims.
