# Repository migration and Windows Streamer updater controls

These changes are for the next compatibility update. The published V3.0.0 asset is unchanged.

## Move to Qpro-Companion

Publish this compatibility update under the existing repository address and install it before renaming the repository. New updater code requests **Fwooffy/Qpro-Companion**, accepts GitHub's exact metadata redirect for repository ID **1386190011**, and falls back to **Fwooffy/Qpro-Enhanced-FT-Wireless** only when the new endpoint returns 404. Stable and prerelease channels retain their own endpoint and selection rules. Asset name, version, size, SHA-256 and package validation still apply.

Keep the same repository when renaming it. Do not create a replacement repository at either address: the numeric metadata redirect is pinned to the existing project. No Git remote or repository rename is performed by Companion.

Previously released apps cannot receive this behavior until they update. If the repository is renamed first and an old updater cannot follow the API redirect, download and extract the compatibility release manually for that first upgrade. Personal models, options and connections use the existing persistent data locations.

## PC Streamer update hold

**First-time setup > Windows Streamer updates** provides Check, Pause and Restore. Changes require administrator approval and an idle updater/installer. Status checks do not change the installation.

The installed Advanced Installer updater rejects its normal Disable policy when its INI has NoDisableAutoCheck. Companion therefore uses a narrowly scoped outbound Windows Defender Firewall rule for the verified installed Updater.exe. It preserves the INI, application files and unrelated rules. Restore removes only the current user's Companion-owned hold, with status explaining any remaining hold from another Windows user.

This holds the PC updater's network access. It cannot cancel a cached/manual installer or remove version requirements between the Quest app and PC Streamer. It does not change Quest app updates. Restore the hold before removing Companion.

## Hand compatibility

The exact PC driver SHA-256 **9fa2b70005d4e0aea02a891ae48a5caa0314d0f30e1190001d7ea979ec10d5c1** is admitted after executable code, data, function layout and vendor-signature comparison with the reviewed beta profile. Admission remains an exact fingerprint match. Live ABI, optical input and cleanup checks still apply.

This fixes the reported combination with Quest app **1.34.22.0** at the Windows driver precheck. It does not admit Quest app **1.34.23.0** or **1.34.25.0**; those require separate headset-layout validation. Error text now identifies whether the missing profile is on Windows, Quest or both, with the next useful diagnostic.

## Verification boundaries

Repository migration has fixture coverage and a successful anonymous live metadata check before the rename. Pause/Restore is exercised with fake firewall commands, including query failures and interrupted recovery. Native policy refusal was reproduced using a disposable updater copy and private test identity. The actual installed updater has only been inspected; no real firewall hold, headset hook, installation or repository rename was performed. Live hand tracking on the newly admitted PC driver remains unverified.
