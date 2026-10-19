using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Yojana.Integration.Connectors.CX01.Testing;

internal sealed class Cx01LiveFactAttribute : FactAttribute
{
    public Cx01LiveFactAttribute()
    {
        if (Required("YOJANA_CX01_URL") && Required("YOJANA_CX01_USER")
            && Required("YOJANA_CX01_PASSWORD")) return;
        Skip = "YOJANA_CX01_URL, YOJANA_CX01_USER and YOJANA_CX01_PASSWORD are required.";
    }

    private static bool Required(string name) => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));
}

public sealed class Cx01LiveTests
{
    [Cx01LiveFact]
    [Trait("Category", "Cx01Live")]
    public async Task Smoke_steps_1_4_5_and_6()
    {
        var cancellationToken = CancellationToken.None;
        var baseUri = new Uri(Environment.GetEnvironmentVariable("YOJANA_CX01_URL")!);
        using var client = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            UseCookies = false,
        }) { BaseAddress = new Uri(baseUri.GetLeftPart(UriPartial.Authority)), Timeout = TimeSpan.FromMinutes(2) };
        var suppliedUser = Environment.GetEnvironmentVariable("YOJANA_CX01_USER")!;
        var password = Environment.GetEnvironmentVariable("YOJANA_CX01_PASSWORD")!;
        var identity = suppliedUser.Split('\\', 2, StringSplitOptions.TrimEntries);
        var company = Environment.GetEnvironmentVariable("YOJANA_CX01_COMPANY")
            ?? (identity.Length == 2 ? identity[0]
                : throw new InvalidOperationException(
                    "Set YOJANA_CX01_COMPANY or use COMPANY\\USER in YOJANA_CX01_USER."));
        var user = identity.Length == 2 ? identity[1] : suppliedUser;
        var loginBody = JsonSerializer.Serialize(new { CompanyDB = company, UserName = user, Password = password });
        using var login = await client.PostAsync(Cx01Protocol.Login,
            new StringContent(loginBody, Encoding.UTF8, "application/json"), cancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var setCookies = login.Headers.GetValues("Set-Cookie").ToList();
        var session = Cookie(setCookies, Cx01Protocol.SessionCookie);
        var route = Cookie(setCookies, Cx01Protocol.RouteCookie);
        Assert.False(string.IsNullOrWhiteSpace(session));
        Assert.False(string.IsNullOrWhiteSpace(route));
        client.DefaultRequestHeaders.Add("Cookie",
            $"{Cx01Protocol.SessionCookie}={session}; {Cx01Protocol.RouteCookie}={route}");

        var allowlistPath = Environment.GetEnvironmentVariable("YOJANA_CX01_SQLTABLE_CONF");
        if (!string.IsNullOrWhiteSpace(allowlistPath))
        {
            var allowlist = await File.ReadAllTextAsync(allowlistPath, cancellationToken);
            Assert.NotEmpty(allowlist);
        }

        using var metadata = await client.GetAsync(Cx01Protocol.Metadata, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        var metadataText = await metadata.Content.ReadAsStringAsync(cancellationToken);
        Assert.Contains("SerialNumberDetail", metadataText, StringComparison.Ordinal);

        var scenarioPath = Environment.GetEnvironmentVariable("YOJANA_CX01_KBA_SCENARIO");
        if (!string.IsNullOrWhiteSpace(scenarioPath))
            await RunKbaScenarioAsync(client, scenarioPath, cancellationToken);
    }

    private static async Task RunKbaScenarioAsync(HttpClient client, string scenarioPath,
        CancellationToken cancellationToken)
    {
        using var scenario = JsonDocument.Parse(await File.ReadAllTextAsync(scenarioPath, cancellationToken));
        var root = scenario.RootElement;
        var delivery = root.GetProperty("delivery").GetRawText();
        using var created = await client.PostAsync(Cx01Protocol.Root + "DeliveryNotes",
            new StringContent(delivery, Encoding.UTF8, "application/json"), cancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var deliveryKey = await CreatedKeyAsync(created, cancellationToken);
        using var before = await client.GetAsync(Cx01Protocol.Root + "DeliveryNotes(" + deliveryKey + ")",
            cancellationToken);
        var beforeCount = SerialCount(before);
        Assert.True(beforeCount > 0);
        Console.WriteLine("Delivery serial count after creation: {0}", beforeCount);

        var returned = root.GetProperty("return").GetRawText().Replace("${DELIVERY_KEY}", deliveryKey,
            StringComparison.Ordinal);
        using var postedReturn = await client.PostAsync(Cx01Protocol.Root + "Returns",
            new StringContent(returned, Encoding.UTF8, "application/json"), cancellationToken);
        Assert.Equal(HttpStatusCode.Created, postedReturn.StatusCode);
        using var afterReturn = await client.GetAsync(Cx01Protocol.Root + "DeliveryNotes(" + deliveryKey + ")",
            cancellationToken);
        Console.WriteLine("Delivery serial count after return: {0}", SerialCount(afterReturn));

        var invoice = root.GetProperty("invoice_after_return").GetRawText().Replace("${DELIVERY_KEY}", deliveryKey,
            StringComparison.Ordinal);
        using var postedInvoice = await client.PostAsync(Cx01Protocol.Root + "Invoices",
            new StringContent(invoice, Encoding.UTF8, "application/json"), cancellationToken);
        Assert.Equal(HttpStatusCode.Created, postedInvoice.StatusCode);
        using var afterInvoice = await client.GetAsync(Cx01Protocol.Root + "DeliveryNotes(" + deliveryKey + ")",
            cancellationToken);
        Console.WriteLine("Delivery serial count after invoice: {0}", SerialCount(afterInvoice));

        var cancellation = root.GetProperty("cancellation_delivery").GetRawText();
        using var cancellable = await client.PostAsync(Cx01Protocol.Root + "DeliveryNotes",
            new StringContent(cancellation, Encoding.UTF8, "application/json"), cancellationToken);
        Assert.Equal(HttpStatusCode.Created, cancellable.StatusCode);
        var cancellationKey = await CreatedKeyAsync(cancellable, cancellationToken);
        using var cancelled = await client.PostAsync(Cx01Protocol.Root + "DeliveryNotes(" + cancellationKey
            + ")/Cancel", new StringContent("{}", Encoding.UTF8, "application/json"), cancellationToken);
        Assert.True(cancelled.IsSuccessStatusCode);
        using var afterCancellation = await client.GetAsync(Cx01Protocol.Root + "DeliveryNotes("
            + cancellationKey + ")", cancellationToken);
        Assert.True(afterCancellation.IsSuccessStatusCode);
        Console.WriteLine("Delivery serial count after cancellation: {0}", SerialCount(afterCancellation));

        var zeroQuantity = root.GetProperty("zero_quantity_delivery").GetRawText();
        using var zeroCreated = await client.PostAsync(Cx01Protocol.Root + "DeliveryNotes",
            new StringContent(zeroQuantity, Encoding.UTF8, "application/json"), cancellationToken);
        Assert.Equal(HttpStatusCode.Created, zeroCreated.StatusCode);
        var zeroKey = await CreatedKeyAsync(zeroCreated, cancellationToken);
        using var zeroRead = await client.GetAsync(Cx01Protocol.Root + "DeliveryNotes(" + zeroKey + ")",
            cancellationToken);
        Console.WriteLine("Zero-quantity line serial count: {0}", SerialCount(zeroRead));
    }

    private static async Task<string> CreatedKeyAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("DocEntry").ToString();
    }

    private static int SerialCount(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode);
        var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("DocumentLines").EnumerateArray()
            .Sum(line => line.GetProperty("SerialNumbers").GetArrayLength());
    }

    private static string Cookie(IEnumerable<string> headers, string name)
    {
        var prefix = name + "=";
        return headers.Select(x => x.Split(';', 2)[0]).First(x => x.StartsWith(prefix, StringComparison.Ordinal))
            [prefix.Length..];
    }
}
