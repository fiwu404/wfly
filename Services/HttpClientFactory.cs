namespace WFly.Services;

internal static class HttpClientFactory
{
    public static HttpClient Create()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            // CoreInstaller follows only an explicitly allow-listed redirect chain.
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(ProductInfo.UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
