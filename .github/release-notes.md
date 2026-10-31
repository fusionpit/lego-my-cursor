LeGo my cursor! {{VERSION}} fixes Legion Go touchpad reports before Windows uses them, so the cursor stops shooting to the bottom-right corner when you lift your finger.

Download `LeGoMyCursor.Setup.exe`, then follow the [install instructions]({{README_URL}}). The installer is unsigned: SmartScreen may show **More info → Run anyway**, followed by UAC. It signs the driver catalog locally with a certificate generated on your device; uninstall removes it.

The single EXE contains the unsigned driver package and uses the .NET Framework 4.8 supplied with Windows 11; no runtime download is required.

This release was built by the repository's CI workflow from the tagged source. It includes `LeGoMyCursor.Setup.exe.sha256` and a GitHub build-provenance attestation; see the README for verification commands. Legion Go 1 remains untested; volunteer reports are welcome.

Before publishing this draft, replace the following with the candidate's actual results:

- Installer launched from a real browser download; SmartScreen/UAC result: **pending**
- Install result on a Legion Go, with the fix reported active in setup: **pending**
- Smart App Control result on a device with it enabled: **pending**
- Changes for this version: **review before publishing**

Smart App Control reputation can differ between machines and change over time; check the exact final EXE, and do not publish a blocked candidate. Copying the EXE over SSH does not validate browser-download trust prompts.
