TEST DRIVER SIDELOADER - MAINTAINER NOTES

This Windows GUI embeds one existing, validated driver .pkg and its metadata in
one executable. Runtime dependencies are embedded too. It uses .NET Framework
4.7.2 or later (present on current Windows 10/11 installations), rather than
requiring the tester to install a separate .NET SDK or run PowerShell scripts.
The executable uploads only. The tester updates their existing driver using
Crestron Home Setup or Configure Pro afterward.

Version allocation
1. Read driver-distributions.json on the maintained driver branch before allocating
   a test version. Reserve a unique four-part package version above the latest
   public package and all previous test versions. Never reuse a reserved version.
2. Build and validate the package. Record its exact SHA256, source commit, purpose,
   and distribution status. Commit the register before publishing a download.
3. Build the sideloader around those exact bytes; do not rebuild the .pkg afterward.
4. Record the final EXE/ZIP checksums and versioned URL. Mark it distributed only
   after verifying the published download. Record recipient feedback separately.
5. Preserve the register when merging/promoting branches. The public release
   workflow must contain its distribution-version guard before another release.

Public versions on GitHub and NuGet remain MAJOR.MINOR.PATCH. Public package
manifests use those three components followed by revision zero. Test package
revisions do not create four-part GitHub tags or NuGet versions. Reserved and
withdrawn test numbers continue to count when selecting the next public version.

Build (PowerShell 7, .NET SDK and normal NuGet access are maintainer prerequisites)
  ./tools/Build-TestSideload.ps1 -PackagePath ABSOLUTE_PKG_PATH -OutputDirectory ABSOLUTE_OUTPUT_FOLDER -NoticesPath ABSOLUTE_NOTICES_FILE

The notice file must include this repository's licence, SSH.NET 2026.0.0 MIT,
BouncyCastle.Cryptography 2.7.0 MIT and Microsoft MIT notices, plus the exact
transitive versions from tools/TestDriverSideload/obj/project.assets.json.
The embedded driver retains its own packaged THIRD-PARTY-NOTICES.txt.

Validation
- Test the exact frozen .pkg as an upgrade of an existing authorized demo-only
  driver tree. Processor test-host execution alone does not cover that upgrade.
  Preserve existing child IDs, room placement and configuration; do not remove
  and recreate children to make the test pass.
- After startup and after a separate tree reload, verify readiness for the root
  and every installed child. Check actual installed-driver commands with an
  independent physical-device observer and restore original demo-device states.
  Match the extracted processor payload to the frozen package and verify release
  of processor reservations. Keep this evidence with the candidate record.
- Run the produced EXE from a folder containing no companion DLLs.
- EXE --verify REPORT_JSON validates the package identity/hash and loads SSH.NET.
- EXE --self-test REPORT_JSON exercises ten upload/failure/integrity checks.
- EXE --extract NEW_PKG_PATH writes a new file; compare it byte-for-byte with the
  validated input. It refuses to overwrite an existing file.
- EXE --preview PNG_PATH renders the form offscreen for layout review.
- With Python and Paramiko 4.0.0 available, run:
  python tools/TestDriverSideload/loopback_sftp.py EXE_PATH NEW_REPORT_FOLDER
  This uses only an isolated loopback SFTP server and a temporary test root.
  It verifies host-key refusal before password authentication, actual upload and
  remote hash, and duplicate preservation. No real processor is contacted.
- Run tools/Test-DistributionVersioning.ps1 and tools/Test-DriverVersioning.ps1.

The GUI never saves passwords or has an installed-driver update/reboot action.
It reads the SSH identity without credentials, asks for confirmation, and pins
that identity on the authenticated connection. Transfer uses a unique .tmp file,
checks its downloaded SHA256, then renames it to .pkg in the import directory.
Existing different .pkg files are preserved. An uncertain transfer is not retried
automatically; the tester is instructed to inspect Setup or contact support.
