using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Yojana.Platform.Time;
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
        var scenarioText = await File.ReadAllTextAsync(scenarioPath, cancellationToken);
        using var initialScenario = JsonDocument.Parse(scenarioText.Replace("${DELIVERY_KEY}", "0",
            StringComparison.Ordinal));
        var root = initialScenario.RootElement;
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

        using var scenario = JsonDocument.Parse(scenarioText.Replace("${DELIVERY_KEY}", deliveryKey,
            StringComparison.Ordinal));
        root = scenario.RootElement;
        var returned = root.GetProperty("return").GetRawText();
        using var postedReturn = await client.PostAsync(Cx01Protocol.Root + "Returns",
            new StringContent(returned, Encoding.UTF8, "application/json"), cancellationToken);
        Assert.Equal(HttpStatusCode.Created, postedReturn.StatusCode);
        using var afterReturn = await client.GetAsync(Cx01Protocol.Root + "DeliveryNotes(" + deliveryKey + ")",
            cancellationToken);
        Console.WriteLine("Delivery serial count after return: {0}", SerialCount(afterReturn));

        var invoice = root.GetProperty("invoice_after_return").GetRawText();
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

    [Cx01LiveFact]
    [Trait("Category", "Cx01Live")]
    public async Task Read_only_connection_survey_prints_summary()
    {
        using var session = await LiveClientAsync(CancellationToken.None);
        var allowlistPath = Environment.GetEnvironmentVariable("YOJANA_CX01_SQLTABLE_CONF");
        var allowlistText = string.IsNullOrWhiteSpace(allowlistPath)
            ? "" : await File.ReadAllTextAsync(allowlistPath, CancellationToken.None);
        var allowlist = Cx01Simulator.TrackingTables.Where(table =>
            allowlistText.Contains(table, StringComparison.OrdinalIgnoreCase)).ToList();
        var configured = new Cx01ConnectionSettings(session.Client.BaseAddress!, "LIVE", session.Release,
            AllowlistedTrackingTables: allowlist);
        var survey = await new Cx01SurveyProbe(new LiveSurveyRemotePort(session), new SystemClock()).ReadAsync(
            Guid.Empty, configured, new(), CancellationToken.None);

        Console.WriteLine("Release: {0}", survey.Release);
        Console.WriteLine("Webhooks: enabled={0}; retries={1}; interval={2}; timeout={3}",
            survey.Webhooks.Enabled, survey.Webhooks.RetryTimes, survey.Webhooks.RetryInterval,
            survey.Webhooks.RequestTimeout);
        Console.WriteLine("Subscriptions: {0}", survey.Webhooks.SubscriptionCount);
        foreach (var table in survey.UserFields.Where(field => field.WrittenObject)
                     .GroupBy(field => field.TableName).OrderBy(group => group.Key, StringComparer.Ordinal))
            Console.WriteLine("UDF {0}: count={1}; mandatory_without_default=[{2}]", table.Key, table.Count(),
                string.Join(',', table.Where(field => field.MandatoryWithoutDefault).Select(field => field.Name)));
        Console.WriteLine("Tracking allowlist: {0}", string.Join(", ", survey.TrackingTableAllowlist
            .OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value)));
    }

    [Cx01LiveFact]
    [Trait("Category", "Cx01Live")]
    public async Task Missing_document_patch_preserves_the_remote_error_shape()
    {
        using var session = await LiveClientAsync(CancellationToken.None);
        var client = session.Client;
        using var response = await client.PatchAsync(Cx01Protocol.Root + "DeliveryNotes(999999)",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(text);
        var error = body.RootElement.GetProperty("error");
        var classified = new Cx01RemoteException(response.StatusCode, "write",
            error.GetProperty("code").ToString(), error.GetProperty("message").GetString());
        Assert.True(classified.IsRecordGone);
        Assert.True(classified.IsBusinessRejection);
        Assert.Equal(error.GetProperty("message").GetString(), classified.RemoteMessage);
    }

    private static async Task<LiveSession> LiveClientAsync(CancellationToken cancellationToken)
    {
        var baseUri = new Uri(Environment.GetEnvironmentVariable("YOJANA_CX01_URL")!);
        var client = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            UseCookies = false,
        }) { BaseAddress = new Uri(baseUri.GetLeftPart(UriPartial.Authority)), Timeout = TimeSpan.FromMinutes(2) };
        var suppliedUser = Environment.GetEnvironmentVariable("YOJANA_CX01_USER")!;
        var identity = suppliedUser.Split('\\', 2, StringSplitOptions.TrimEntries);
        var company = Environment.GetEnvironmentVariable("YOJANA_CX01_COMPANY")
            ?? (identity.Length == 2 ? identity[0]
                : throw new InvalidOperationException("Set YOJANA_CX01_COMPANY or use COMPANY\\USER."));
        var user = identity.Length == 2 ? identity[1] : suppliedUser;
        var loginBody = JsonSerializer.Serialize(new
        {
            CompanyDB = company,
            UserName = user,
            Password = Environment.GetEnvironmentVariable("YOJANA_CX01_PASSWORD")!,
        });
        using var login = await client.PostAsync(Cx01Protocol.Login,
            new StringContent(loginBody, Encoding.UTF8, "application/json"), cancellationToken);
        login.EnsureSuccessStatusCode();
        using var loginResponse = JsonDocument.Parse(await login.Content.ReadAsStringAsync(cancellationToken));
        var release = loginResponse.RootElement.TryGetProperty("Version", out var version)
            ? version.ToString() : "unknown";
        var cookies = login.Headers.GetValues("Set-Cookie").ToList();
        client.DefaultRequestHeaders.Add("Cookie", $"{Cx01Protocol.SessionCookie}="
            + Cookie(cookies, Cx01Protocol.SessionCookie) + $"; {Cx01Protocol.RouteCookie}="
            + Cookie(cookies, Cx01Protocol.RouteCookie));
        return new(client, release);
    }

    private sealed record LiveSession(HttpClient Client, string Release) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    private sealed class LiveSurveyRemotePort(LiveSession session) : ICx01SurveyRemotePort
    {
        public Task<string> ReleaseAsync(Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult(session.Release);

        public async Task<string> GetTextAsync(Guid connectionId, string path,
            CancellationToken cancellationToken)
        {
            using var response = await session.Client.GetAsync(path, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }

        public async Task<JsonDocument> GetAsync(Guid connectionId, string path,
            CancellationToken cancellationToken)
        {
            using var response = await session.Client.GetAsync(path, cancellationToken);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }

        public async Task<JsonDocument?> PostAsync(Guid connectionId, string path,
            CancellationToken cancellationToken)
        {
            using var response = await session.Client.PostAsync(path,
                new StringContent("{}", Encoding.UTF8, "application/json"), cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.StatusCode == HttpStatusCode.NoContent) return null;
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }
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
