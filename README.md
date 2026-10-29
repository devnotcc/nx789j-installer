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
| **Debian 13 + KDE Plasma** (on top of an installed Evolution X / LineageOS) | packaged, draft release |

Draft releases are not visible to the app or the public until they're published.

### How the Linux install works

Debian runs next to Android on the same kernel: Android's native side keeps loading the
drivers, firmware and vendor services, and Debian (systemd in its own namespaces) takes the
screen. The package carries a second kernel (no KernelSU, with the namespaces systemd needs) and
an init_boot with a small boot helper and an **install agent**:

1. The installer copies the Linux image and its launchers to `/data/local/tmp/nx-install` over
   adb, plus `firstboot.conf` (your Linux password as a SHA-512 crypt hash, never the password),
   then a manifest (name, size, SHA-256 of every file) and `READY`.
2. It writes the kernel (boot + init_boot of the active slot) over EDL, like any other package.
3. On the next boot the agent takes the upload out of the adb user's reach, checks every file
   against the manifest, accepts only a fixed list of names, refuses symlinks and hard links,
   never replaces an installed Linux unless asked, moves the files into `/data/nx-linux` as root,
   grows the image to the chosen size, and writes `STATUS` (`ok` / `error <reason>`).
4. The installer reads `STATUS`, sets the boot mode the same way, and restarts into Linux.
   Debian sets the password, grows its filesystem and makes its SSH host keys on the first start.

Android's apps and data stay. While the Linux kernel is installed Android runs without
KernelSU (root modules stop working); the Recovery tool puts the OS package's kernel back.

Know what you trade while the Linux kernel is installed:

- **Android runs with SELinux permissive** (Linux needs it), so apps are much less contained.
- **Any PC you have authorized for adb can get root** on the next boot, by uploading its own
  launchers to the install agent (it checks that an upload is complete and intact, not who made
  it). Revoke adb authorizations you don't need.
- **No Bluetooth** in Debian yet (needs a kernel module that isn't shipped).

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
            Linux: [--size GB] [--replace] [--stay-android] [--password-file f]
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
