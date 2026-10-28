# NX789J Installer

A Windows 11–style installer for the ZTE nubia RedMagic 10 Pro (**NX789J**). It replaces the
loose collection of shell scripts with one guided app that installs an OS, checks every step, and
keeps the phone's firmware and unlock untouched.

> **Experimental.** Flashing erases what's on the phone. The bootloader must already be unlocked.
> Only the NX789J is supported; other models are refused.

## What it installs

| Package | Status |
|---|---|
| **Evolution X** 16 (KernelSU-Next + SUSFS kernel, optional TWRP) | packaged, draft release |
| **TWRP** recovery on its own | packaged, draft release |
| **LineageOS** 23.2, with or without Google apps | being built; shown as "Coming soon" |
| **Debian 13 + KDE Plasma** | not packaged yet |

Draft releases are not visible to the app or the public until they're published.

## Toolbox

- **Play Integrity** — check a keybox against Google's revocation list, install one (no reboot),
  and see the status of the modules on the phone. **The app never ships keyboxes; you provide your own.**
- **Full backup** — read every partition over EDL to the PC, with checksums (read-only).
- **Recovery** — restart into recovery/EDL/bootloader; fix a phone that only starts into fastboot
  (clears the bootloader's "unbootable" mark in both copies of the partition table); put the
  kernel back (rewrites only boot + init_boot from an OS package, keeps data).
- **Windows 11 VM** — set up the crosvm-based Windows guest on a Debian install.

## Safety

- **Firmware is never written.** The bootloaders, TrustZone, modem, DSPs, and the partitions that
  hold the bootloader unlock and radio calibration are on a hard deny-list in the engine
  (`PartitionPolicy`), checked again right before every write, whatever a package asks for.
- **The unlock is verified from the bootloader itself** (the `devinfo` partition) in EDL before
  anything is written — not from Android, which custom ROMs spoof to "locked".
- Installs **back up the partitions they replace** to the PC first, then **read them back and
  compare checksums** after writing.
- Nothing is written below 30% battery, or with more than one phone connected.

## Layout

```
src/NxInstaller.Core   engine: device detection, EDL, packages, install, backup, Play Integrity, safety
src/NxInstaller.Cli    nxcli — the engine on the command line (testing and scripting)
tests/                 unit tests for the safety-critical logic
```

A WinUI 3 app (the wizard in the screenshot) sits on top of the same engine.

## Tools it needs

- **adb / fastboot** — downloaded from Google automatically.
- **Qualcomm EDL tools + the NX789J firehose** — these are proprietary and are **not** shipped;
  the app finds them in the ZTE toolbox you already have from unlocking the bootloader.
- **OS images** — downloaded from GitHub Releases (large ones split into parts and rejoined), or
  from a local folder / USB drive.

## Command line

```
nxcli probe                 show the connected phone
nxcli checks                run the pre-install checks
nxcli keybox <file>         check a keybox against Google's revocation list
nxcli pif-status            show the Play Integrity stack on the phone
nxcli pif-swap <file>       check then install a keybox (no reboot)
nxcli edl-info              EDL lock state + partition count (read-only)
nxcli backup [dir]          full partition backup over EDL (read-only)
nxcli install <id> [--dry-run] [--twrp] [--wipe] [--source dir]
nxcli repair-slot [--dry-run]   clear the "unbootable" mark on the active slot (phone in EDL)
nxcli kernel <id> [--dry-run]   rewrite only boot + init_boot from a package
nxcli pack <spec.json> <dir>    build a package: zstd, split under 2 GiB, manifest.json
```

## Building

```
dotnet test tests/NxInstaller.Core.Tests
dotnet publish src/NxInstaller.App -c Release -r win-x64 -p:Platform=x64 -o artifacts/NxInstaller-win-x64
```

The release build is one trimmed, compressed exe (about 58 MB). Package specs for `nxcli pack`
are in `packaging/`.

## License

GPL-3.0-or-later (see [LICENSE](LICENSE)). The Qualcomm EDL tools, firehose loader, Google
platform-tools, OS images and keyboxes the app works with are not part of this repository and
keep their own terms.
