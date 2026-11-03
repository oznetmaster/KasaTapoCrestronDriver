# Shared driver versioning

Build helpers are copied unchanged from [BuildAutomation/DriverVersioning](https://github.com/oznetmaster/BuildAutomation/tree/021a2f84f1168ca529072a0b2fe1e335b4c0e10e/DriverVersioning). `build/driver-versioning.json` records the immutable revision and hashes. Existing local entry points call these helpers; ordinary clones and IDE builds do not fetch Cake to evaluate versions.

Public releases use three-part versions; manifests use four. Debug builds increment the fourth component, local Release builds preserve it, and CI Release builds reset it to zero after verifying the selected release. Repository-specific distribution registers, package identities and submission-document checks remain authoritative. Existing published and submitted packages are not rewritten.

CI runs the `driver-versioning-regressions` Cake recipe against the actual project imports. Run it locally using `build/Invoke.ps1 -Task driver-versioning-regressions`.
