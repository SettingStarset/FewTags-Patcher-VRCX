# FewTags for VRCX

<p align="center">
  <strong>A small Windows patcher that adds FewTags profile badges to your existing VRCX installation.</strong><br>
  Stable and nightly support · Automatic FewTags data refresh · Backup and restore
</p>

<p align="center">
  <a href="https://github.com/SettingStarset/FewTags-Patcher-VRCX/releases/latest">Download the latest patcher</a>
  &nbsp;·&nbsp;
  <a href="https://github.com/SettingStarset/FewTags-Patcher-VRCX/actions/workflows/release.yml">Release builds</a>
</p>

---

## What it does

The patcher updates the frontend files in the VRCX folder you select. It does not install VRCX, replace its executable, or create a separate VRCX build.

| Feature | Details |
| --- | --- |
| **Stable and nightly** | Includes a frontend for the latest release from [vrcx-team/VRCX](https://github.com/vrcx-team/VRCX) and [Natsumi-sama/VRCX](https://github.com/Natsumi-sama/VRCX). |
| **Exact build matching** | Checks the installed `VRCX.exe` and `VRCX.dll` before patching. Unsupported builds are left unchanged. |
| **Fresh FewTags data** | Downloads and validates the current FewTags databases each time you apply the integration. |
| **Profile badges** | Shows tags only for matching user IDs, with the supported rich text and animation styles. |
| **Backup and restore** | Saves changed frontend files before applying and can restore the most recent recognized backup. |
| **Patcher updates** | Checks for a newer patcher at startup, asks before installing, and verifies its SHA-256 checksum. |

## Install or update

1. Download **VRCX-FewTags-Patcher.exe** from the [latest release](https://github.com/SettingStarset/FewTags-Patcher-VRCX/releases/latest).
2. Run the patcher. It will suggest your VRCX folder; use **Browse…** if needed.
3. Confirm the app recognizes your stable or nightly build.
4. Select **Apply / Update**. If VRCX is open, close it and apply again.
5. Start VRCX again to see FewTags on matching profiles.

The default install folder is `C:\Program Files\VRCX`. If Windows denies access, run the patcher as administrator. The patcher only supports the exact VRCX builds bundled into that release; update the patcher after VRCX updates.

## Restore the original frontend

Close VRCX, open the patcher, select the same install folder, then choose **Restore backup**. The restore option recognizes both current per-file backups and earlier full-frontend backups.

## How releases stay current

GitHub Actions checks both VRCX repositories every four hours and also runs when the patcher or integration source changes. It builds the FewTags frontend against each latest release, verifies both builds, then publishes a new patcher with the matching executable and library fingerprints. A failed or incompatible build is not released.

Each patcher release includes the EXE and a `.sha256` checksum file. Patcher self-updates are opt-in: the app asks before downloading and replacing itself.

## Build from source

The release workflow is the recommended way to produce a patcher because it selects the upstream releases and computes their exact binary fingerprints for you. To build manually, use Windows with Node.js 24+, npm 11+, and the .NET 7 SDK:

1. Check out the desired stable and nightly VRCX source tags into `VRCX-stable-source` and `VRCX-nightly-source`.
2. In each source folder, apply `integration/fewtags-vrcx.patch`, run `npm ci`, then run `npm run prod`.
3. Zip each generated `build/html` folder so `html` is at the root of the archive. Save them as `payload/stable/VRCX-html.zip` and `payload/nightly/VRCX-html.zip`.
4. Publish the Windows executable with the matching release tags and SHA-256 hashes:

   ```powershell
   $nightlyTag = "<nightly-release-tag>"
   $stableTag = "<stable-release-tag>"
   $nightlyExeSha256 = "<nightly-exe-sha256>"
   $nightlyDllSha256 = "<nightly-dll-sha256>"
   $stableExeSha256 = "<stable-exe-sha256>"
   $stableDllSha256 = "<stable-dll-sha256>"

   dotnet publish app/VRCXFewTagsPatcher.csproj -c Release -r win-x64 --self-contained true `
     -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
     -p:VrcxNightlyReleaseTag=$nightlyTag -p:VrcxStableReleaseTag=$stableTag `
     -p:VrcxNightlyExeSha256=$nightlyExeSha256 -p:VrcxNightlyDllSha256=$nightlyDllSha256 `
     -p:VrcxStableExeSha256=$stableExeSha256 -p:VrcxStableDllSha256=$stableDllSha256 `
     -p:PatcherRepository=SettingStarset/FewTags-Patcher-VRCX -o app/publish
   ```

The output is `app/publish/VRCX-FewTags-Patcher.exe`. Self-updates use this repository's GitHub releases.

## Data note

FewTags is community-maintained data and may be inaccurate or opinion-based. The integration displays the source tags without independently verifying their claims.
