# Kasa/Tapo Binding Diagnostics

Read-only Windows support utility, version 1.0.0. This does not contain or install a driver package.

Reads only `/user/Data/PyngDeviceManifest/DeviceManifest.cfg` and, if present, the Kasa/Tapo `managed-devices-cache.json`. It parses source files in memory and exports a fixed selection of Kasa/Tapo platform/child IDs, saved gateway/controller bindings, device metadata, configuration-marker summaries and cache identity/classification fields. It never writes to the processor. It does not export raw configuration, credentials, arbitrary attributes or DriverDataStore contents. Reports contain device names, IDs and room numbers and should be sent privately to support.

Home's saved `DriverGuid` is a catalogue key on the tested processor. The collector validates the exact Kasa/Tapo catalogue key or package GUID. `ChildDevices`, resolved JSON references, parent information and saved gateway relationships establish scope. A matching controller ID does not prove every other binding or definition field is unchanged.

Build with `dotnet build BindingDiagnostics.csproj -c Release`. Dependencies and notices are embedded in the executable. Windows requires .NET Framework 4.7.2 or later. Check the exact executable in an otherwise empty directory with `--self-test <new report path>`, `--verify <report path>`, and `--preview <PNG path>`. These switches do not connect to a processor. Use Start-Process -Wait -WindowStyle Hidden from PowerShell when invoking this WinExe for validation.

For a controlled live read-only validation, `--collect <new JSON path>` requires CRESTRON_HOME_HOST, CRESTRON_HOME_USER, CRESTRON_HOME_PASSWORD and CRESTRON_HOME_SSH_FINGERPRINT in the environment; it has no fallback target or trust-on-first-use path. The interactive form probes the SSH key before authentication and asks the user to confirm the intended processor identity.

A failed or interrupted collection must not be reported as successful. Existing output files are never replaced. Source reads are bounded to 16 MiB each and do not attempt repair, replay, commissioning, reload or device removal.
