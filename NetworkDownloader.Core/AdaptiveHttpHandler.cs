using System.Net;

namespace NetworkDownloader.Core;

// Redirects are followed here, not inside SocketsHttpHandler, so every redirected
// origin participates in the shared request budget. Only GET is used by the engine.
internal sealed class AdaptiveHttpHandler(AdaptiveHttpController owner, AdaptiveHttpSession session,
    HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var retries = 0;
        for (var redirects = 0; ;)
        {
            var lease = await session.AcquireAsync(uri, cancellationToken).ConfigureAwait(false);
            HttpResponseMessage? response = null;
            try
            {
                using var copy = new HttpRequestMessage(HttpMethod.Get, uri)
                { Version = request.Version, VersionPolicy = request.VersionPolicy };
                foreach (var header in request.Headers)
                {
                    if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                        AdaptiveHttpController.Origin(uri) != AdaptiveHttpController.Origin(request.RequestUri!) &&
                        header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) continue;
                    copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                try { response = await base.SendAsync(copy, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                { throw new HttpRequestException("连接或响应头等待超时。", ex); }
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
                {
                    if (++redirects > 10) throw new HttpRequestException("下载地址重定向超过 10 次。");
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (next.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(next.UserInfo) ||
                        uri.Scheme == "https" && next.Scheme != "https")
                        throw new HttpRequestException("拒绝不安全的下载重定向。");
                    uri = next;
                    response.Dispose(); response = null;
                    lease.Dispose();
                    continue;
                }
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    var retry = response.Headers.RetryAfter;
                    var delay = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(5 * (retries + 1));
                    if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                    owner.Throttled(session, uri, delay);
                    if (retries++ < 3)
                    {
                        response.Dispose(); response = null;
                        lease.Dispose();
                        continue;
                    }
                }
                response.Content = new LeasedContent(response.Content, lease);
                return response;
            }
            catch
            {
                response?.Dispose();
                lease.Dispose();
                if (!cancellationToken.IsCancellationRequested) session.BackOff("连接异常 / 超时，降低并发");
                throw;
            }
        }
    }

    private sealed class LeasedContent : HttpContent
    {
        private readonly HttpContent content;
        private readonly IDisposable lease;
        public LeasedContent(HttpContent content, IDisposable lease)
        {
            this.content = content; this.lease = lease;
            foreach (var h in content.Headers) Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        protected override bool TryComputeLength(out long length)
        { length = content.Headers.ContentLength ?? 0; return content.Headers.ContentLength.HasValue; }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => content.CopyToAsync(stream);
        protected override Task<Stream> CreateContentReadStreamAsync() => content.ReadAsStreamAsync();
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => content.ReadAsStreamAsync(token);
        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { content.Dispose(); } finally { lease.Dispose(); } }
            base.Dispose(disposing);
        }
    }
}
