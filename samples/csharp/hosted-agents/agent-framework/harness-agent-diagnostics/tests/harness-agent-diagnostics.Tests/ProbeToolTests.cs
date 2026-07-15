using HarnessAgentDiagnostics;

namespace HarnessAgentDiagnostics.Tests;

public class ProbeToolTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ComputeProbe_ThrowsWhenLabelIsBlank(string? label)
    {
        var exception = Assert.Throws<ArgumentException>(() => ProbeTool.ComputeProbe(label!, [1]));

        Assert.Contains("label", exception.ParamName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComputeProbe_ThrowsWhenValuesAreNull()
    {
        var exception = Assert.Throws<ArgumentException>(() => ProbeTool.ComputeProbe("alpha", null!));

        Assert.Contains("values", exception.ParamName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComputeProbe_ThrowsWhenValuesAreEmpty()
    {
        var exception = Assert.Throws<ArgumentException>(() => ProbeTool.ComputeProbe("alpha", []));

        Assert.Contains("values", exception.ParamName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComputeProbe_ReturnsSortedValuesAndSumWithoutMutatingInput()
    {
        int[] values = [4, -2, 7, 4];
        int[] original = [.. values];

        ProbeResult result = ProbeTool.ComputeProbe("alpha", values);

        Assert.Equal(original, values);
        Assert.Equal("alpha", result.Label);
        Assert.Equal([-2, 4, 4, 7], result.OrderedValues);
        Assert.Equal(13, result.Sum);
        Assert.NotSame(values, result.OrderedValues);
    }
}
