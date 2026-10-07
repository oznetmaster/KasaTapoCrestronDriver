// Copyright (c) 2026 Neil Colvin. MIT with Commons Clause; see LICENSE.
using CrestronHomeDevTools.SubmissionTests;
using NUnit.Framework;

namespace KasaTapoCrestronDriver.SubmissionTests;

[TestFixture]
public sealed class DriverSubmissionTests : SubmissionFixture
{
    protected override string SettingsEnvironment => "KASATAPO_SUBMISSION_SETTINGS";
}
