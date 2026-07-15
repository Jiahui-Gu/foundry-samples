using System.Net;

namespace HarnessAgentDiagnostics;

internal static class LoopbackHttpUrl
{
    internal const string Default = "http://127.0.0.1:8088";

    internal static Uri Parse(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.Query)
            || uri.AbsolutePath != "/"
            || !IsLoopbackHost(uri))
        {
            throw new ArgumentException("URL must be an absolute loopback HTTP base URL.", nameof(value));
        }

        return uri;
    }

    private static bool IsLoopbackHost(Uri uri)
        => uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(uri.Host, out IPAddress? address)
                && IPAddress.IsLoopback(address);
}
