# Public releases and Crestron submissions

Future public driver packages, NuGet wrappers, documentation ZIPs and other public downloads exclude Crestron submission help PDFs. Public documentation is the README and normal public documentation. Help sources may remain in the repository for preparing a separate Crestron submission.

The release workflows run `tools/Assert-PublicDriverArtifacts.ps1` on the actual artifacts before publication, including frozen/staged, documentation-only and processor-test release paths. The guard recursively checks `.pkg`, `.nupkg`, `.snupkg` and `.zip` files; it rejects PDFs, malformed archives, missing paths and an empty archive selection. It never strips or rewrites a frozen artifact. If the guard fails, fix preparation, stage new bytes and repeat applicable validation before freezing them.

Run the portable regression suite locally:

```powershell
pwsh -File tools/Test-PublicDriverArtifacts.ps1
```

Driver resource staging excludes PDFs before ManifestUtil creates the public `.pkg`. The PDF is not a public runtime dependency. No source help file is deleted by this policy. A developer's ordinary clone can build and validate without a sibling BuildAutomation checkout.

For Crestron submission, use the maintained DevTools `submission package-help` command with the driver help profile to add the required help PDF to a separate copy of the frozen public package. Apply submission-only naming/metadata as required. Compare the two packages and require unchanged runtime DLL, UI resources, translations and other runtime bytes. Then perform submission-specific validation, document review, signing and delivery approval. The public artifact and its hash must remain unchanged.

Published GitHub/NuGet releases and earlier submission receipts remain immutable. This correction is for future releases. Documentation-only NuGet releases retaining an older PDF-bearing driver will now fail the public guard; do not silently strip that old payload or claim it is byte-identical. Use a newly prepared and validated PDF-free driver release instead.

Shared source and regression provenance are recorded in `build/public-driver-packaging.json`.
