using HarnessAgentDiagnostics;

namespace HarnessAgentDiagnostics.Tests;

public sealed class LoopbackHttpUrlTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8088")]
    [InlineData("http://localhost:8088")]
    [InlineData("http://[::1]:8088")]
    public void Parse_AcceptsAbsoluteLoopbackHttpUrls(string value)
    {
        Uri result = LoopbackHttpUrl.Parse(value);

        Assert.Equal(new Uri(value), result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/responses")]
    [InlineData("https://127.0.0.1:8088")]
    [InlineData("ftp://127.0.0.1:8088")]
    [InlineData("http://example.com:8088")]
    [InlineData("http://192.168.1.10:8088")]
    [InlineData("http://user:password@127.0.0.1:8088")]
    [InlineData("http://127.0.0.1:8088/#fragment")]
    [InlineData("http://127.0.0.1:8088/responses")]
    [InlineData("http://127.0.0.1:8088?tlsSkipVerify=true")]
    public void Parse_RejectsNonBaseOrUnsafeUrls(string value)
    {
        Assert.Throws<ArgumentException>(() => LoopbackHttpUrl.Parse(value));
    }
}
