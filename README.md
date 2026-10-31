# LeGo My Cursor!

> [!CAUTION]
> I can NOT guarantee that this will be compatible with any anti-cheat engine. Everything was designed to stay out of anything anti-cheat would usually care about: It's user-mode and not kernel-mode; we use a self-signed package rather than disabling signature enforcement or enabling test signing; the driver does not touch any process, ever; and we do not generate any input. 
> 
> I have tested the driver in both Fortnite (NOT BANNED as of 20261031) and Valorant (NOT BANNED as of 20261031). 

NOTE: AI was used for just about everything BUT this readme file.

There are two issues that you can experience with the Go's touchpad:

1. When you lift your finger off the pad, the cursor shoots off to another location: this could be straight down, straight right, or to the bottom-corner of the screen. This is caused by the firmware writing a special value to the X and or Y coordinates that is supposed to mean "not being touched" but there's also another bit called "Tip Switch" that is supposed to be 0 when the pad is not being touched but it's still set to 1. That is, the firmware sets the X/Y coords to 0xFFFF while Tip Switch is 1. Tip Switch is what matters to Windows, so it reads 0xFFFF as where it "should" go. **This is what we fix**.
2. When you're moving the cursor, it will appear to jump to another position for a few milliseconds before returning to where you'd expect it to be. This is caused by the firmware not writing both bytes of the X or Y position at one time: it first writes the low byte, then it writes the high byte. Sometimes between these writes the driver reads the position and moves it to this "new" position, until the next time it reads the correct position about 4 milliseconds later and it snaps back. That is, the firmware's writes to the 2 bytes of each coordinate are not atomic.

The first issue can be fixed in two ways: either set Tip Switch to 0 if X or Y is 0xFFFF, or track the last known good coordinates the firmware set and replace a 0xFFFF with the last-known value. Microsoft’s Windows Precision Touchpad (PTP) HID protocol (which the touchpad's drivers claim it conforms to) says the second method would be the correct one, while the first is a lot easier and faster.

The second issue can be fixed only if we know what the next report, that is expected to come 4 milliseconds from now, contains. That means we'd have to delay everything by 4ms.

## What we do

LeGo My Cursor offers both fixes for the first issue. The recommended setting, Simple, just clears the Tip Switch bit on a 0xFFFF. The other setting, Compliant, tracks the last recorded position and replaces the X/Y values with those. We don't fix the second issue (yet) as an occasional small jump for a few milliseconds is better than delaying everything for 4ms. If you’d take the trade, open an issue.

## How do we do it?

[Windows has some shit](https://learn.microsoft.com/en-us/windows-hardware/drivers/wdf/overview-of-the-umdf) that [allows other drivers to change stuff](https://learn.microsoft.com/en-us/windows-hardware/drivers/wdf/installing-a-umdf-filter-driver). LeGo My Cursor is a **UMDF 2 upper device filter** that lets us patch the times the firmware reports an out-of-bound move so it stays where it is supposed to.

## What's the catch?

We need to install a driver on your system and Windows really doesn't want you to do that with some random code. Something in the chain needs to be **signed**, which usually takes \$\$\$ and a business and a whole lot of paperwork.

The easier/cheaper thing is for you to generate a certificate on your own device, trust it, then sign the software with your own keys. That's what we're doing here.
We build an unsigned driver DLL and include an unsigned catalog in the installer. Your PC will then generate a brand new 2048-bit RSA self-signed certificate named "CN=LeGoMyCursor local signer (key destroyed)" that can only perform code signing and is valid until 2099. We use that certificate and Windows `SignerSignEx2` to sign the catalog. As soon as we sign the catalog we get rid of the private key so nothing else can be signed with it. We then add the public certificate to the Local Machine `Root` and `TrustedPublisher` stores so Windows has no reason to not trust it. When you uninstall, reinstall, clean, or update the driver, we remove the certificate from the stores. Clean (`LeGoMyCursor.Setup.exe /clean`) removes every driver package, then every certificate with that name. If you reinstall or update the driver, we create new certs.

This repo has both the driver code and an installer/config program to handle all the necessary steps for you. All of the code used in the releases is available in the repo, and all releases are built here in the open. 

### Verifying the download [🤖 written]

Every release is built by the [CI workflow](.github/workflows/ci.yml) from the tagged source. Download `LeGoMyCursor.Setup.exe` and `LeGoMyCursor.Setup.exe.sha256` from the same release, then compare the hashes in PowerShell:

```powershell
(Get-FileHash .\LeGoMyCursor.Setup.exe -Algorithm SHA256).Hash
Get-Content .\LeGoMyCursor.Setup.exe.sha256
```
or
```powershell
(Get-FileHash .\LeGoMyCursor.Setup.exe -Algorithm SHA256).Hash -eq (Get-Content .\LeGoMyCursor.Setup.exe.sha256).Split(' ')[0]
```

To check GitHub's build-provenance attestation with the [GitHub CLI](https://cli.github.com/):

```powershell
gh attestation verify .\LeGoMyCursor.Setup.exe --repo fusionpit/lego-my-cursor
```

## Installing

This driver is only compatible with the Lenovo Legion Go 2 running Windows 11 with access to an Admin account. Tested on Windows 11 26H2 (build 26300.9711).

Download the latest release from [the github releases page](https://github.com/fusionpit/lego-my-cursor/releases). See [the verification section above](#verifying-the-download--written)

You may be presented with a Windows SmartScreen prompt when running the installer as it is not signed ([see above](#whats-the-catch)). If you do, select More Info -> Run anyway.

Pick your mode, [described above](#what-we-do). Select Install to begin the process of creating a certificate, signing the catalog, adding the public certificate to stores, installing the catalog and driver, restarting the touchpad, and verifying the custom stuff has been added. Sometimes, you'll be asked to restart after. Usually not.

### Windows is blocking something!

You may find Smart App Control blocking installation of the driver itself. We don't sign the driver dll (the signed catalog covers it), but it wouldn't help if we did as SAC only cares about specific certificates from Microsoft.  
You really only have three options when this happens: try again later and hope it works, disable SAC, or [build the driver locally](#building-the-driver--installer-locally).

#### Disable Smart App Control
NOTE: depending on your version of Windows, you MAY NOT BE ABLE TO TURN SAC BACK ON IF YOU TURN IT OFF. 

  - Start -> type "Windows Security" into the search -> Enter
  - App & browser control -- click the tile not the "Turn on" button
  - Smart App Control -> Smart App Control settings
  - Select Off, confirm the UAC prompt

Once disabled, run the setup program again.

## Building the driver + installer locally

If you don't trust the release, you can build and install the driver locally. You can still run into SAC blocking it, but you can just re-build until it works.  
The local toolchain will be several gigs in size.  

To install locally, you will need:
| Dependency | Version | Needed for | How to get it |
|---|---|---|---|
| [Build Tools for Visual Studio](https://visualstudio.microsoft.com/downloads/) (or Visual Studio) | 2022 | Everything | Visual Studio Installer |
| ↳ MSVC C++ x64/x86 build tools | Latest | Driver | Visual Studio Installer component |
| ↳ Windows 11 SDK | Latest | Driver | Visual Studio Installer component |
| ↳ .NET desktop development (MSBuild) | — | Installer | Visual Studio Installer workload |
| ↳ .NET Framework 4.8 targeting pack | 4.8 | Installer | Visual Studio Installer component |
| Windows Driver Kit (NuGet package `Microsoft.Windows.WDK.x64`) | 10.0.26100.6584 (exact) | Driver, catalog | `nuget.exe install` (see below) |
| [nuget.exe](https://www.nuget.org/downloads) | Any recent | Installing the WDK package | Direct download |

WDK can be installed with
```powershell
nuget.exe install Microsoft.Windows.WDK.x64 -Version 10.0.26100.6584 -OutputDirectory driver\.wdk -DependencyVersion Ignore
```

You can then build with a version number:
```powershell
.\build-wpf.cmd 1.2.3
```

You will end up with an installer in `build\release\LeGoMyCursor.Setup.exe`. Run that to sign/install the catalog and unsigned driver dll. If you still run into a Smart App Control block, run the same `build-wpf` command with the same version number again. This will generate a driver dll with a new hash, which may work when installed. If not, hopefully one of the next 100 will.
