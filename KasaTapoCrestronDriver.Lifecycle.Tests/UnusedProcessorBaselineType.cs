// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

namespace KasaTapoCrestronDriver;

// Signature-only shim for the unrelated processor SSH baseline callback. Strip tests
// pass null for that callback and never exercise colour tuning or SSH behavior.
internal enum ProcessorLightTuningMode { Color, White }