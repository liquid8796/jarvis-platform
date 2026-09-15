using System.Net;
using System.Text;

namespace JarvisCode.Providers.Tests;

/// <summary>Returns a canned response and records the request body for assertions.</summary>
public sealed class StubHttpHandler(HttpStatusCode status, string body, string contentType = "text/event-stream")
    : HttpMessageHandler
{
    public string? LastRequestBody { get; private set; }
    public HttpRequestMessage? LastRequest { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body))
            {
                Headers = { { "Content-Type", contentType } },
            },
        };
    }
}
