// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.IO;

using NUnit.Framework;

namespace KasaTapoCrestronDriver.Tests;

internal static class DriverTestPaths
	{
	// Packaged UI/manifest fixtures belong beside the assembly; the processor's
	// TestDataDirectory parameter names the separate private-input directory.
	internal static string DataDirectory => Path.Combine (TestContext.CurrentContext.TestDirectory, "DriverTestData");
	}