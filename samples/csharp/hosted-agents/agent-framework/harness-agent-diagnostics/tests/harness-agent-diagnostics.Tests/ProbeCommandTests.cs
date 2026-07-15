using HarnessAgentDiagnostics;

namespace HarnessAgentDiagnostics.Tests;

public sealed class ProbeCommandTests
{
    [Fact]
    public void Parse_AcceptsProbeWithDefaultOutput()
    {
        string baseDirectory = Path.Combine("root", "app");

        ProbeCommand command = Assert.IsType<ProbeCommand>(
            ProbeCommandParser.Parse(["probe"], baseDirectory));

        Assert.Equal(Path.Combine(baseDirectory, "run-output", "direct"), command.OutputDirectory);
    }

    [Fact]
    public void Parse_AcceptsProbeWithOutputOverride()
    {
        ProbeCommand command = Assert.IsType<ProbeCommand>(
            ProbeCommandParser.Parse(["probe", "--output", "artifacts"]));

        Assert.Equal("artifacts", command.OutputDirectory);
    }

    [Fact]
    public void Parse_ComposesServeAndCaptureWireCommands()
    {
        string baseDirectory = Path.Combine("root", "app");

        ServeCommand serve = Assert.IsType<ServeCommand>(
            ProbeCommandParser.Parse(["serve", "--url", "http://localhost:9010"], baseDirectory));
        CaptureWireCommand capture = Assert.IsType<CaptureWireCommand>(
            ProbeCommandParser.Parse(
                ["capture-wire", "--output", "wire-output", "--url", "http://[::1]:9011"],
                baseDirectory));
        CaptureWireCommand defaultCapture = Assert.IsType<CaptureWireCommand>(
            ProbeCommandParser.Parse(["capture-wire"], baseDirectory));

        Assert.Equal(new Uri("http://localhost:9010"), serve.Url);
        Assert.Equal(new Uri("http://[::1]:9011"), capture.Url);
        Assert.Equal("wire-output", capture.OutputDirectory);
        Assert.Equal(new Uri("http://127.0.0.1:8088"), defaultCapture.Url);
        Assert.Equal(
            Path.Combine(baseDirectory, "run-output", "wire"),
            defaultCapture.OutputDirectory);
    }

    [Theory]
    [InlineData()]
    [InlineData("probe", "--unknown")]
    [InlineData("probe", "--output")]
    [InlineData("probe", "--output", "")]
    [InlineData("probe", "--output", "--unknown")]
    [InlineData("probe", "--output", "first", "--output", "second")]
    [InlineData("serve", "--url")]
    [InlineData("serve", "--url", "http://127.0.0.1:8088", "--url", "http://localhost:8089")]
    [InlineData("serve", "--output", "somewhere")]
    [InlineData("capture-wire", "--unknown")]
    [InlineData("capture-wire", "--url", "--output")]
    [InlineData("capture-wire", "--output", "first", "--output", "second")]
    public void Parse_RejectsUnknownMissingOrDuplicateArguments(params string[] arguments)
    {
        ProbeCommandException exception = Assert.Throws<ProbeCommandException>(
            () => ProbeCommandParser.Parse(arguments));

        Assert.Contains("Usage:", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("serve", "--url", "https://127.0.0.1:8088")]
    [InlineData("serve", "--url", "http://example.com:8088")]
    [InlineData("capture-wire", "--url", "http://user@127.0.0.1:8088")]
    [InlineData("capture-wire", "--url", "http://127.0.0.1:8088/#fragment")]
    public void Parse_RejectsUnsafeUrls(params string[] arguments)
    {
        ProbeCommandException exception = Assert.Throws<ProbeCommandException>(
            () => ProbeCommandParser.Parse(arguments));

        Assert.Contains("Usage:", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(arguments[^1], exception.Message, StringComparison.Ordinal);
    }
}
