using HarnessAgentDiagnostics;

namespace HarnessAgentDiagnostics.Tests;

public sealed class ProbeCommandTests
{
    [Fact]
    public void Parse_AcceptsProbeWithDefaultOutput()
    {
        string baseDirectory = Path.Combine("root", "app");

        ProbeCommand command = ProbeCommandParser.Parse(["probe"], baseDirectory);

        Assert.Equal(Path.Combine(baseDirectory, "run-output", "direct"), command.OutputDirectory);
    }

    [Fact]
    public void Parse_AcceptsProbeWithOutputOverride()
    {
        ProbeCommand command = ProbeCommandParser.Parse(["probe", "--output", "artifacts"]);

        Assert.Equal("artifacts", command.OutputDirectory);
    }

    [Theory]
    [InlineData()]
    [InlineData("serve")]
    [InlineData("probe", "--unknown")]
    [InlineData("probe", "--output")]
    [InlineData("probe", "--output", "")]
    [InlineData("probe", "--output", "--unknown")]
    [InlineData("probe", "--output", "first", "--output", "second")]
    public void Parse_RejectsUnknownMissingOrDuplicateArguments(params string[] arguments)
    {
        ProbeCommandException exception = Assert.Throws<ProbeCommandException>(
            () => ProbeCommandParser.Parse(arguments));

        Assert.Contains("Usage:", exception.Message, StringComparison.Ordinal);
    }
}
