using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TextInOCR
{
    internal sealed class TextInException(string message) : Exception(message);

    /// <summary>Client for https://docs.textin.com/api-reference/endpoint/parse</summary>
    internal sealed class TextInClient : IDisposable
    {
        const string Endpoint = "https://api.textin.com/ai/service/v1/pdf_to_markdown";

        /// <summary>Some pages failed to parse; the result for the rest is still returned.</summary>
        public const int PartialSuccessCode = 50207;

        readonly HttpClient http;

        /// <summary>The proxy requests go through, for display: its scheme://host:port, "none" or "bypassed".</summary>
        public string Proxy { get; }

        /// <param name="useProxy">false ignores both the system proxy and the HTTP(S)_PROXY environment variables.</param>
        public TextInClient(string appId, string secretCode, bool useProxy)
        {
            // Timeouts differ per file, so each ParseAsync call enforces its own.
            http = new HttpClient(new SocketsHttpHandler { UseProxy = useProxy }) { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.Add("x-ti-app-id", appId);
            http.DefaultRequestHeaders.Add("x-ti-secret-code", secretCode);

            var endpoint = new Uri(Endpoint);
            IWebProxy system = HttpClient.DefaultProxy;
            Proxy = !useProxy ? "bypassed (--no-proxy)"
                : !system.IsBypassed(endpoint) && system.GetProxy(endpoint) is Uri proxy
                    ? $"{proxy.Scheme}://{proxy.Authority}" // Authority leaves out any user:password
                    : "none";
        }

        public static bool IsUrl(string input) =>
            Uri.TryCreate(input, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        /// <summary>
        /// Uploads a local file as a binary body, or sends an http(s) URL as a text body,
        /// and returns the response JSON. Throws <see cref="TextInException"/> on API errors.
        /// <paramref name="timeout"/> covers the whole exchange: upload, processing and reply.
        /// <paramref name="uploadProgress"/> receives the number of file bytes sent so far.
        /// </summary>
        public async Task<JsonObject> ParseAsync(string input, IReadOnlyDictionary<string, string> query,
            TimeSpan timeout, Action<long>? uploadProgress = null)
        {
            string url = query.Count == 0
                ? Endpoint
                : Endpoint + "?" + string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            bool isUrl = IsUrl(input);
            using HttpContent content = isUrl ? new StringContent(input) : new ProgressFileContent(input, uploadProgress);
            content.Headers.ContentType = new MediaTypeHeaderValue(isUrl ? "text/plain" : "application/octet-stream");

            using var timeoutCts = new CancellationTokenSource(timeout);
            string body, status;
            int statusCode;
            try
            {
                using HttpResponseMessage response = await http.PostAsync(url, content, timeoutCts.Token);
                statusCode = (int)response.StatusCode;
                status = $"HTTP {statusCode} {response.ReasonPhrase}";
                body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new TextInException($"No reply within {timeout.TotalSeconds:0}s; raise it with --timeout <seconds>.");
            }

            JsonObject? json;
            try
            {
                json = JsonNode.Parse(body) as JsonObject;
            }
            catch (JsonException)
            {
                json = null;
            }
            if (json == null)
                throw new TextInException($"{status}: {Truncate(body, 500)}");

            int code = json["code"]?.GetValue<int>() ?? statusCode;
            if (code != 200 && code != PartialSuccessCode)
                throw new TextInException($"TextIn error {code}: {json["message"]}");
            return json;
        }

        static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

        public void Dispose() => http.Dispose();
    }

    /// <summary>Request body streamed from a file, reporting the running byte count after every 64 KB chunk.</summary>
    internal sealed class ProgressFileContent(string path, Action<long>? progress) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken cancellationToken)
        {
            await using FileStream file = File.OpenRead(path);
            byte[] buffer = new byte[64 * 1024];
            long sent = 0;
            int read;
            progress?.Invoke(0);
            while ((read = await file.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                progress?.Invoke(sent += read);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = new FileInfo(path).Length;
            return true;
        }
    }
}
