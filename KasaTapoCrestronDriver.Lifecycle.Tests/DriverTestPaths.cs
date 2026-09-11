// Copyright (c) 2026 Neil Colvin.
// Licensed under the MIT License with Commons Clause. See LICENSE in the repository root.

using System;
using System.IO;

using NUnit.Framework;

namespace KasaTapoCrestronDriver.Tests;

internal static class DriverTestPaths
	{
	internal static string DataDirectory => Path.Combine (TestContext.Parameters.Get ("TestDataDirectory", AppContext.BaseDirectory), "DriverTestData");
	}