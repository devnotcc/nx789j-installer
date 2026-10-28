using System.Net.Http.Headers;

namespace NxInstaller.Core;

public static class Net
{
    /// <summary>One client for the app. GitHub's API refuses requests without a User-Agent.</summary>
    public static HttpClient CreateClient()
    {
        var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = Timeout.InfiniteTimeSpan, // large downloads; callers cancel instead
        };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NxInstaller", Version));
        return http;
    }

    public static string Version => typeof(Net).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
