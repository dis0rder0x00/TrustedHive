# TrustedHive - (CVE-2026-81963 Suspected)

Local privilege escalation via ungated CBS COM method - medium-IL to SYSTEM arbitrary file write on Windows 11

## Table of Contents

- [Disclosure Timeline](#disclosure-timeline)
- [The Vulnerability](#the-vulnerability)
  - [Affected versions](#affected-versions)
  - [How my approach works](#how-my-approach-works)
- [MSRC Response](#msrc-response)
- [Suspected CVE](#suspected-cve)
- [Usage & Repository Structure](#usage--repository-structure)
  - [PoC_Fun/](#poc_fun)
  - [PoC_Boring/](#poc_boring)
- [See Also](#see-also)
- [UI Impressions](#ui-impressions)

## Disclosure Timeline

| Date                  | Event                                                                          |
| --------------------- | ------------------------------------------------------------------------------ |
| **July 22, 2026**     | Vulnerability reported to MSRC                                                 |
| **September 8, 2026** | Microsoft patches the Windows Update servicing stack (September Patch Tuesday) |
| **October 29, 2026**  | MSRC responds (see below)                                                      |

## The Vulnerability

The Component Based Servicing (CBS) session COM object runs inside `TrustedInstaller.exe` as **SYSTEM** and is activatable by any Interactive User. Nearly every method on the session interface calls `EnsureCallerIsAdministrator` before proceeding, besides **`CreateExternalTransformerExecutor`**, which returned a low-level CSI store transformer executor with no authorization check.

That executor's `Initialize()` runs as SYSTEM with no impersonation. A medium-integrity caller can drive TrustedInstaller to create a registry hive log file at an attacker-controlled path by exploiting NTFS junctions and object-manager symbolic links, none of which require any special privileges to create.

### Affected versions

- **Windows 11** (23H2, 24H2, 25H2, 26H1) and **Windows Server 2025** (likely all builds prior to the September 8, 2026 cumulative update)
- I originally tested this on:
	- Windows 11 Pro - 24H2 (servicing stack 26100.8872)
	- Windows Server 2025 - 24H2 (servicing stack 26100.32230)
	- Windows 11 Pro - 26H1 (OS Build 28120.2546)
- **Patched** in the September 2026 Patch Tuesday servicing stack update

### How my approach works

1. **Build a reference hive** with a permissive DACL (Everyone: Full Control)
2. **Set up junctions and symlinks** redirecting the hive's log-file path to the target location
3. **Activate the CBS session** and call the ungated `CreateExternalTransformerExecutor`
4. **TrustedInstaller follows the redirected path** and creates the log file, which is owned by SYSTEM, at the attacker-chosen location
5. **Overwrite the file contents** as the file inherits the hive's permissive DACL, so the unprivileged process can write arbitrary content

**Result:** Arbitrary file write to any existing directory as SYSTEM, from a normal user account. No administrator rights, no special privileges, no user interaction required.

## MSRC Response

MSRC assessed my report as **Low severity / Defense in Depth**, stating that the "assessed proofs of concept did not demonstrate file writes to unauthorized locations." The case was closed as below the threshold for immediate servicing and no extra CVE was issued, no bounty awarded, and MSRC will not track the issue further, which makes sense if this is a duplicate.

The PoC writes attacker-controlled content to `C:\Windows\System32` from a medium-IL, non-administrator process and it did so reliably on every unpatched build it was tested on.
The most likely explanation for MSRC's assessment is timing as the report was submitted in July, the servicing stack fix shipped in September, and the response by MSRC arrived in October, by which point the PoC would indeed fail with `E_ACCESSDENIED` on any up-to-date system.

I did get added to the [special mentions page of MSRC](https://msrc.microsoft.com/special-mention) so there is that at least :)

## Suspected CVE

I suspect the vulnerability reported here is covered by **[CVE-2026-81963](https://msrc.microsoft.com/update-guide/vulnerability/CVE-2026-81963)** ("Windows Update Stack Elevation of Privilege Vulnerability"), which was fixed in the mentioned update and describes the same vulnerability class (CWE-59 / CWE-284, link-following + improper access control in the CBS servicing stack). That CVE was credited to Romain Deperne (Airbus Helicopters) and Microsoft Threat Intelligence Center (MSTIC), and was confirmed as actively exploited in the wild before the patch. *BUT: I have no official confirmation that my report covers the same issue.*

## Usage & Repository Structure

Both PoCs must be run as a **normal, non-administrator user** (medium integrity) and contain the same base exploit. If you run them elevated, the exploit succeeds trivially because you already have admin which misses the entire point.

### [`PoC_Fun/`](PoC_Fun/)

The full proof-of-concept with a UI. A vibecoded graphical harness that wraps the exploit technique in an interactive interface. Good for demos and everybody who doesn't like boring terminal only PoCs (**MAKE POCS FUN AGAIN!**)

The monospace gothic pixel art font used in headings and the banner was created on my commision by **nico_n_art**.

**Build:**
```
$ cd PoC_Fun

$ dotnet clean -c Release && dotnet publish -c Release

$ .\bin\Release\net8.0-windows\win-x64\publish\TrustedHive.exe
```

**Usage:** Just double click the release and the UI should guide you or use the "`?`" to open the help menu.


### [`PoC_Boring/`](PoC_Boring/)

The standalone C file for the exploit. No UI and no dependencies beyond `ole32.lib` and `advapi32.lib`. Build with MSVC (`cl /O2 ...`), point it at an input file and an output path, and it does the thing.

**Build:**
```
$ cd PoC_Boring

$ cl /nologo /W3 /O2 TrustedHive_PoC.c ole32.lib advapi32.lib /link 

$ .\TrustedHive_PoC.exe
```


**Usage:**
```
$ .\TrustedHive_BoringPoC.exe <input_file> <output_path> [/keep]

<input_file>  : file whose bytes are written to the target

<output_path> : destination (its parent directory must exist),
				e.g. C:\Windows\System32\poc.dll

/keep         : keep the Hidden+System attributes the Configuration Manager
                stamps on the created hive-log file (default: clear them)
```

## See Also

- [CVE-2026-81963 (MSRC)](https://msrc.microsoft.com/update-guide/vulnerability/CVE-2026-81963) - the suspected CVE
- [Fortra/Core Security blog](https://www.coresecurity.com/blog/cve-2026-81963-public-cbs-session-system-code-execution) - a different exploitation path (dpx.dll sideloading via `EvaluateApplicability`) under the same CVE
- **nico_n_art** for her awesome art and as the creator of the gothic pixel art font in the UI

## UI Impressions
![Main UI](./imgs/fun_ui.png)
![Progress Modal](./imgs/fun_progress.png)
