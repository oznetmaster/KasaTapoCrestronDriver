# Evidence Dump: Cached-Reload Managed-Device Publication/Activation Path

**Scope:** `PlatformDriver.cs`, `PlatformDriver.Discovery.cs`, `PlatformDriver.Refresh.cs`, `PlatformDriver.ManagedDevices.cs`, `PlatformDriver.DeviceMaterialization.cs`

## (1) await / .Result / .Wait() / GetAwaiter().GetResult() / lock / SemaphoreSlim / Monitor

| File:Line | Construct | Owning thread context |
|---|---|---|
| Discovery.cs:126 | `await Task.WhenAll(firstPassTask, secondPassTask)` | discovery refresh loop (Task.Run continuation, no captured context — `ConfigureAwait(false)` upstream) |
| Discovery.cs:129 | `firstPassTask.Result` | same — blocking on already-`WhenAll`-awaited task (safe, non-blocking in practice) |
| Discovery.cs:130 | `secondPassTask.Result` | same |
| Discovery.cs:259 | `await Task.Delay(interval, _runtimeCancellationSource.Token)` | discovery refresh loop |
| Discovery.cs:266 | `await RefreshPlatformSafelyAsync(_runtimeCancellationSource.Token)` | discovery refresh loop |
| Discovery.cs:608 | `Task.Run(async () => ...)` | spawns onto ThreadPool |
| Discovery.cs:639 | `await Discover.ConnectAsync(...)` | inside above Task.Run |
| Discovery.cs:734 | `_managedDeviceCacheWriteGate.Wait()` (sync, not `WaitAsync`) | cache-save path, thread not identified in reviewed range |
| PlatformDriver.cs:664 | `_refreshGate = new SemaphoreSlim(1,1)` (field) | — |
| PlatformDriver.cs:665 | `_scheduledRefreshGate = new SemaphoreSlim(1,1)` (field) | — |
| PlatformDriver.cs:666 | `_managedDeviceCacheWriteGate = new SemaphoreSlim(1,1)` (field) | — |
| PlatformDriver.cs:979 | `lock (_hubPollersGate)` | host config-apply thread (`ApplyRuntimeConfiguration` caller) |
| PlatformDriver.cs:733-735 | `ThreadPool.Get{Min,Max,Available}Threads` | diagnostic logging call site (thread not identified in reviewed range) |
| Refresh.cs:20 | `await DiscoverDevicesAsync(...)` | `RefreshPlatformAsync` caller thread |
| Refresh.cs:119 | `await ResolveManagedLightDescriptorsAsync(...)` | same |
| Refresh.cs:413 | `Task.Run(async () => ...)` | spawns onto ThreadPool |
| Refresh.cs:421 | `Task.Run(() => CreateManagedLightEntity(...), cancellationToken)` | spawns onto ThreadPool |
| Refresh.cs:442 | `await materialization.Task` | awaits the above Task.Run |
| ManagedDevices.cs:761 | `await _refreshGate.WaitAsync(cancellationToken)` | `RefreshPlatformSafelyAsync` caller |
| ManagedDevices.cs:770 | `await RefreshPlatformAsync(cancellationToken)` | same, inside gate |
| ManagedDevices.cs:796 | `await Task.Yield()` | `TriggerScheduledRefreshAsync`-type entry point |
| ManagedDevices.cs:803 | `await _scheduledRefreshGate.WaitAsync(0, _runtimeCancellationSource.Token)` | same, non-blocking probe (timeout=0) |
| ManagedDevices.cs:816 | `await RefreshPlatformSafelyAsync(_runtimeCancellationSource.Token)` | same |
| DeviceMaterialization.cs:99 | `lock (_hubPollersGate)` | materialization/hub-poller-lookup caller |
| DeviceMaterialization.cs:649 | `await Task.Delay(250ms)` | inside Task.Run at line 647 |
| DeviceMaterialization.cs:686 | `await Task.Delay(250ms)` | inside Task.Run at line 684 |
| DeviceMaterialization.cs:647 | `Task.Run(async () => ...)` | spawns onto ThreadPool |
| DeviceMaterialization.cs:684 | `Task.Run(async () => ...)` | spawns onto ThreadPool |
| DeviceMaterialization.cs:759 | `await lightEntity.SetConfiguredAsync(true, context, cancellationToken)` | materialization completion path |
| DeviceMaterialization.cs:1181 | `await Discover.GetOrConnectSharedAsync(...)` | strip child expansion |
| DeviceMaterialization.cs:1386 | `await hubPoller.ConnectSharedAsync(...)` | hub child expansion |
| DeviceMaterialization.cs:1494 | `await Discover.GetOrConnectSharedAsync(...)` | alias enrichment |
| ManagedDevices.cs:475-486 | no lock around `childConfigurationController.ApplyConfigurationStep(...)` / `.ApplyConfiguration(...)` | `PublishCachedChildControllers` — synchronous, no gate |

No `.Result` other than Discovery.cs:129/130 (post-`WhenAll`). No `.GetAwaiter().GetResult()` found in these files. No `Monitor.Enter/TryEnter/Exit` found directly (only via `lock`).

## (2) State mutated from more than one thread; guarded or not

| Field | Mutation sites (file:line) | Guarded? |
|---|---|---|
| `_configuredChildControllerIds` (`ConcurrentDictionary`) | Discovery.cs:496, DeviceMaterialization.cs:390/730/750/770/869, Refresh.cs:146 | Type-level (lock-free), no external synchronization |
| `_inUseChildControllerIds` (`ConcurrentDictionary`) | DeviceMaterialization.cs:731/751/771/816, Refresh.cs:147 | Type-level, no external synchronization |
| `_childTreatAsLight` (`ConcurrentDictionary`) | Discovery.cs:436, Refresh.cs:372 | Type-level, no external synchronization |
| `_hubPollers` | DeviceMaterialization.cs:99 (read/write), PlatformDriver.cs:979 (iterate) | Guarded by `lock (_hubPollersGate)` at both sites |
| `_managedDevices` | Referenced/read at Refresh.cs:299 (`_managedDevices.Count`, `.Keys`), mutation site not located within reviewed lines | Not verified — not confirmed guarded |
| `_knownDescriptors` | Refresh.cs:120 (`_knownDescriptors[...] = descriptor`), ManagedDevices.cs (read at :239) | No lock observed around the dictionary itself (type not confirmed concurrent-safe in reviewed lines) |
| `_lightEntities` | ManagedDevices.cs:230 (write), Refresh.cs:126/155 (read/write) | No lock observed |
| `_childControllers` | ManagedDevices.cs:231 (write) | No lock observed |
| `_managedDeviceCacheMetadata` | Read at ManagedDevices.cs (SNAPSHOT-PAYLOAD-DIAG diagnostic) | Not verified |
| `_pendingRemovalMissCounts` (`ConcurrentDictionary`) | Refresh.cs:121 `.TryRemove` | Type-level |

## (3) catch blocks that swallow or log-and-continue on this path

| File:Line | Catch clause | Behavior |
|---|---|---|
| Discovery.cs:275-278 | `catch (OperationCanceledException)` | `LogInfo` only, swallowed |
| Discovery.cs:279-282 | `catch (Exception ex)` | `LogError` only, swallowed |
| Discovery.cs:511-515 | `catch (Exception ex)` | `LogInfo`, then deletes cache file, swallowed |
| Discovery.cs:548-551 | `catch (Exception ex)` | `LogInfo` only, swallowed |
| Discovery.cs:660-664 | `catch (Exception ex) when (!(ex is OperationCanceledException))` | `LogInfo` only, swallowed |
| Discovery.cs:821-824 | `catch (Exception ex)` | `LogInfo` only, swallowed |
| ManagedDevices.cs:324-327 | `catch (Exception ex)` (around `PeekStatus()`) | status set to `"peek-failed:{type}"`, swallowed, execution continues |
| ManagedDevices.cs:489-492 | `catch (Exception ex)` (around `ApplyConfigurationStep`/`ApplyConfiguration` replay) | `LogError` only, swallowed — this wraps the replay call whose result was `nextStep='Activation', errorKeys=DriverDataStore` in the observed capture |
| ManagedDevices.cs:773-776 | `catch (OperationCanceledException)` | swallowed (body not captured in this pass, present per grep) |
| ManagedDevices.cs:777-780 | `catch (Exception ex)` | logged, swallowed |
| ManagedDevices.cs:818 | `catch (OperationCanceledException)` | swallowed |
| Refresh.cs:214-218 | `catch (Exception ex)` | logs discovered-device error, swallowed (body partially captured) |
| Refresh.cs:479-483 | `catch (Exception ex) when (!(ex is OperationCanceledException))` | adds to `discoveryErrors` list, `LogError`, swallowed |
| Refresh.cs:532-535 | `catch (OperationCanceledException)` | `LogInfo` only, swallowed |
| Refresh.cs:536-539 | `catch (Exception ex)` | `LogError` only, swallowed |
| DeviceMaterialization.cs:414 | `catch (Exception ex)` | body not captured this pass, present per grep |
| DeviceMaterialization.cs:705/714 | `catch (OperationCanceledException)` / `catch (Exception ex)` | not captured this pass |
| DeviceMaterialization.cs:967 | `catch (Exception ex)` | not captured this pass |
| DeviceMaterialization.cs:1243-1256 | `catch (OperationCanceledException) when (...)` / `catch (Exception ex) when (...)` | both return `Array.Empty<ManagedLightDescriptor>()`, logged, swallowed |
| DeviceMaterialization.cs:1441-1454 | same pattern | returns empty array, swallowed |
| DeviceMaterialization.cs:1542-1553 | same pattern | no return override, swallowed |

## (4) Constructs whose behavior differs between .NET 4.7.2/.NET 10 and Crestron's Mono/CLR runtime

| File:Line | Construct | Note (fact only) |
|---|---|---|
| `KasaTapoCrestronDriver.csproj:3` | `<TargetFramework>net472</TargetFramework>` | Driver builds only for net472; no net10 TFM present in this project |
| PlatformDriver.cs:733-735 | `ThreadPool.GetMinThreads/GetMaxThreads/GetAvailableThreads` | Present in code; behavior on Crestron's runtime not verified in this pass |
| Discovery.cs:608, Refresh.cs:413/421, DeviceMaterialization.cs:647/684 | `Task.Run(...)` | Schedules onto `ThreadPool`; no `TaskScheduler`/`SynchronizationContext` override located in these files |
| All `await X.ConfigureAwait(false)` sites listed in (1) | `.ConfigureAwait(false)` used uniformly on every located `await` in these five files | No `.ConfigureAwait(true)` or bare `await` (no `ConfigureAwait` call) found in these files |
| — | `async void` | Not found in these five files |
| — | `SIO_UDP_CONNRESET` | Not found in these five files |
| — | `HttpClient` | Not found in these five files (discovery/connect calls go through `KasaTapoClient`'s `Discover`/`KasaDevice`, an external package, not reviewed here) |
| — | `SynchronizationContext` | Not found in these five files |

## (5) Types present in more than one assembly the ILRepack step merges

| Item | file:line |
|---|---|
| ILRepack invoked via `ILRepackMerge.ps1` | `KasaTapoCrestronDriver.csproj:107` (None include), `:146` (Exec command in `MergeDependencies` target) |
| Merge input set | `KasaTapoCrestronDriver.csproj:139-141`: `$(TargetPath)` + `$(TargetDir)*.dll` excluding `Crestron.*.dll`, `RAD*.dll`, `Ir*.dll`, `SimplSharp*.dll`, `CrestronCertifiedDriverResourcesLibrary.dll` |
| Referenced packages that are merge candidates (not excluded by the glob above) | `KasaTapoCrestronDriver.csproj:66-69`: `Hafner.Compatibility.MetaPackage 1.9.0`, `KasaTapoClient 1.7.0`, `Microsoft.Bcl.Memory 11.0.0-preview.4.26230.115`, `Microsoft.Extensions.Logging.Abstractions 10.0.11` |
| Vendored reference, also a merge candidate | `KasaTapoCrestronDriver.csproj:75`: `Renci.SshNet` via `HintPath` to `..\lib\Renci.SshNet\Renci.SshNet.dll` |
| Explicit documented type-identity collision | `KasaTapoCrestronDriver.csproj:38-45` (comment) and `KasaTapoCrestronDriver.Tests.csproj:24,28`: `KasaDevice`/`DeviceType` from the merged `KasaTapoCrestronDriver.dll` are stated to be physically different types from the same-named types in the unmerged NuGet `KasaTapoClient` package referenced directly by the Tests project — confirmed by the `SkipMergeDependencies=true` / separate `bin\Unmerged\` output path workaround |
| Post-merge patch step | `KasaTapoCrestronDriver.csproj:147`: `PatchMergedAssembly.ps1` run immediately after ILRepack, output to `$(TargetDir)patched\$(AssemblyName).dll` — implies the raw merge output requires correction, exact edits not reviewed in this pass |
