using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Cs2Highlight.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cs2Highlight.Web.Tests;

public sealed class YooKassaPaymentProviderTests
{
    [Fact]
    public async Task CreatePaymentUsesServerAuthIdempotencyAndRedirectFlow()
    {
        StubHandler handler = new(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.yookassa.ru/v3/payments", request.RequestUri?.ToString());
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            Assert.Equal(
                Convert.ToBase64String(Encoding.UTF8.GetBytes("shop:secret")),
                request.Headers.Authorization?.Parameter);
            Assert.Equal("generation-order", request.Headers.GetValues("Idempotence-Key").Single());
            string body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("\"value\":\"1.00\"", body);
            Assert.Contains("\"currency\":\"RUB\"", body);
            Assert.Contains("\"type\":\"redirect\"", body);
            Assert.Contains("https://merchant.example/return", body);
            return Json(HttpStatusCode.OK, """
                {
                  "id": "23d93cac-000f-5000-8000-126628f15141",
                  "status": "pending",
                  "confirmation": {
                    "type": "redirect",
                    "confirmation_url": "https://yoomoney.ru/payment/23d93cac"
                  }
                }
                """);
        });
        using HttpClient client = new(handler)
        {
            BaseAddress = new Uri("https://api.yookassa.ru/v3/")
        };
        YooKassaPaymentProvider provider = CreateProvider(client);

        PaymentSessionResult result = await provider.CreateSessionAsync(
            new PaymentRequest(
                "order",
                100,
                "RUB",
                "generation-order",
                "https://merchant.example/return",
                "CSHighlighter order"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("23d93cac-000f-5000-8000-126628f15141", result.ProviderPaymentId);
        Assert.Equal("https://yoomoney.ru/payment/23d93cac", result.ConfirmationUrl);
    }

    [Fact]
    public async Task StatusComesFromAuthoritativePaymentRequest()
    {
        StubHandler handler = new(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(
                "https://api.yookassa.ru/v3/payments/payment-id",
                request.RequestUri?.ToString());
            return Task.FromResult(Json(HttpStatusCode.OK, """
                { "id": "payment-id", "status": "succeeded" }
                """));
        });
        using HttpClient client = new(handler)
        {
            BaseAddress = new Uri("https://api.yookassa.ru/v3/")
        };
        YooKassaPaymentProvider provider = CreateProvider(client);

        PaymentStatusResult result = await provider.GetStatusAsync(
            "payment-id", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(ProviderPaymentStatus.Succeeded, result.Status);
    }

    private static YooKassaPaymentProvider CreateProvider(HttpClient client) =>
        new(
            client,
            new PaymentOptions
            {
                Provider = "YooKassa",
                ShopId = "shop",
                SecretKey = "secret"
            },
            NullLogger<YooKassaPaymentProvider>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode status, string content) =>
        new(status)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }
}
