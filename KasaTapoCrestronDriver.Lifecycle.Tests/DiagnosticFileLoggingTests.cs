// Copyright (c) 2026 Neil Colvin. See LICENSE in the repository root.
#if DEBUG
using System.Text;
using Crestron.DeviceDrivers.EntityModel.Logging;
using Crestron.DeviceDrivers.SDK;
using Crestron.DeviceDrivers.SDK.EntityModel;
using Microsoft.Extensions.Logging;
using KasaTapoCrestronDriver;

namespace KasaTapoCrestronDriver.Tests;
#if NETFRAMEWORK
[Category ("Processor")]
#endif
[TestFixture]
public sealed class DiagnosticFileLoggingTests
{
    private string _directory = null!;
    [SetUp] public void SetUp () { _directory = Path.Combine (Path.GetTempPath (), "kasa-diagnostic-test-" + Guid.NewGuid ().ToString ("N")); Directory.CreateDirectory (_directory); }
    [TearDown] public void TearDown () { Directory.Delete (_directory, true); }
    [Test]
    public void CredentialsTokensAndMultilineHeadersAreRedacted ()
    {
        DiagnosticFileLoggerProvider.RegisterSecrets ("private-customer-password-!", "private-user@example.test");
        using var provider = new DiagnosticFileLoggerProvider (_directory);
        var logger = provider.CreateLogger ("test");
        logger.LogInformation ("password=\"a password with spaces\" token='private-token' stok=private-session username=private-user@example.test exception: private-customer-password-!");
        logger.LogInformation ("Authorization: Bearer private-bearer\r\nCookie: private-cookie");
        string text = File.ReadAllText (Path.Combine (_directory, "diagnostic.log"));
        Assert.That (text, Does.Contain ("[redacted]"));
        Assert.That (text, Does.Not.Contain ("private-").And.Not.Contain ("a password with spaces"));
        Assert.That (text.Split ('\n').Count (line => line.Length > 0), Is.EqualTo (2));
    }
    [Test]
    public void ConcurrentMessagesRemainBoundedToTwoFiles ()
    {
        using var provider = new DiagnosticFileLoggerProvider (_directory, 2048);
        var logger = provider.CreateLogger ("test");
        Parallel.For (0, 200, i => logger.LogInformation ("message {Index}: {Text}", i, new string ('x', 100)));
        var files = Directory.GetFiles (_directory);
        Assert.That (files.Length, Is.EqualTo (2));
        Assert.That (files.All (file => new FileInfo (file).Length <= 2048), Is.True);
        Assert.That (files.All (file => File.ReadAllLines (file).All (line => line.Contains ("message ") && line.EndsWith (new string ('x', 100)))), Is.True);
    }
    [Test]
    public void UnwritableSinkDoesNotPreventNativeLogging ()
    {
        string blocker = Path.Combine (_directory, "file");
        File.WriteAllText (blocker, "block directory creation");
        using var native = new DriverLogger ("diagnostic-failure-test");
        var bridge = new DiagnosticControllerLogger (native.AppLogger, "test", blocker);
        Assert.DoesNotThrow (() => bridge.Log ("test", LogEntryLevel.Error, "A retained native error"));
        Assert.That (File.ReadAllText (blocker), Is.EqualTo ("block directory creation"));
    }
    [Test]
    public void FileRetainsDiagnosticsWhenHomeLoggingIsOff ()
    {
        using var native = new DriverLogger ("diagnostic-retention-test");
        var bridge = new DiagnosticControllerLogger (native.AppLogger, "test", _directory);
        bridge.Log ("test", LogEntryLevel.Info, "startup marker {0}", 42);
        bridge.Exception ("test", new IOException ("test failure"), "operation {0}", "refresh");
        string text = File.ReadAllText (Path.Combine (_directory, "diagnostic.log"));
        Assert.That (text, Does.Contain ("DIAGNOSTIC BUILD ONLY").And.Contain ("startup marker 42").And.Contain ("operation refresh").And.Contain ("test failure"));
    }
}
#endif
