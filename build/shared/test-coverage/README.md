# Coverage evidence primitives

These build-only PowerShell functions compare complete case identities, parse NUnit discovery/result trees and apply explicitly supplied skip-reason rules. Callers retain their existing suite selection, live/processor category rules, inventories, name normalization, path resolution and runner execution. A shared parser does not make synthetic evidence into live-device evidence.

Copy this directory unchanged into `build/shared/test-coverage/` with immutable source commit and file hashes in `build/test-coverage.json`. Existing tools remain local entry points. The current regression suites stay authoritative, and `Test-Coverage.ps1` supplies cross-platform helper checks. Extracted code retains its original MIT License with Commons Clause; see LICENSE.
