using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Yojana.DemoIncumbent;

internal sealed class ServiceLayerClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    private readonly DemoOptions options;
    private readonly HttpClient client;
    private bool loggedIn;

    public ServiceLayerClient(DemoOptions options)
    {
        this.options = options;
        var handler = new HttpClientHandler { UseCookies = false };
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null) return false;
            var observed = Convert.ToHexString(SHA256.HashData(certificate.RawData));
            return string.Equals(observed, options.CertificateSha256, StringComparison.OrdinalIgnoreCase)
                && (errors == System.Net.Security.SslPolicyErrors.None
                    || errors == System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors
                    || errors == System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch
                    || errors == (System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors
                        | System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch));
        };
        client = new(handler)
        {
            BaseAddress = new Uri(options.ServiceUri.GetLeftPart(UriPartial.Authority)),
            Timeout = TimeSpan.FromMinutes(3),
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task LoginAsync(CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync("/b1s/v2/Login", Content(new
        {
            CompanyDB = options.Company,
            options.UserName,
            options.Password,
        }), cancellationToken);
        await EnsureSuccessAsync(response, "login", cancellationToken);
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(value => value.Split(';', 2)[0]).ToArray() : [];
        if (cookies.Length == 0) throw new InvalidOperationException("The remote login returned no session cookie.");
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
        loggedIn = true;
    }

    public async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(Absolute(path), cancellationToken);
        await EnsureSuccessAsync(response, "GET " + path, cancellationToken);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
    }

    public async Task<string> GetTextAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(Absolute(path), cancellationToken);
        await EnsureSuccessAsync(response, "GET " + path, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>Calls a read-only service action (no body) and returns its JSON answer.</summary>
    public async Task<JsonDocument> ActionAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync(Absolute(path), null, cancellationToken);
        await EnsureSuccessAsync(response, "POST " + path, cancellationToken);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
    }

    public async Task<CreateResult> PostAsync(string collection, object body, string keyField,
        string? numberField, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync(Absolute(collection), Content(body), cancellationToken);
        await EnsureSuccessAsync(response, "POST " + collection, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(text);
        var key = Scalar(document.RootElement, keyField)
            ?? throw new InvalidOperationException($"POST {collection} returned no {keyField}.");
        return new(key, numberField is null ? null : Scalar(document.RootElement, numberField));
    }

    public async Task PatchAsync(string path, object body, CancellationToken cancellationToken)
    {
        using var response = await client.PatchAsync(Absolute(path), Content(body), cancellationToken);
        await EnsureSuccessAsync(response, "PATCH " + path, cancellationToken);
    }

    public async Task<JsonElement?> FindOneAsync(string collection, string filter,
        CancellationToken cancellationToken, string? select = null)
    {
        var path = collection + "?$filter=" + Uri.EscapeDataString(filter) + "&$top=1";
        if (select is not null) path += "&$select=" + Uri.EscapeDataString(select);
        using var result = await GetAsync(path, cancellationToken);
        if (!result.RootElement.TryGetProperty("value", out var values) || values.GetArrayLength() == 0)
            return null;
        return values[0].Clone();
    }

    public async ValueTask DisposeAsync()
    {
        if (loggedIn)
        {
            try
            {
                using var response = await client.PostAsync("/b1s/v2/Logout", null);
            }
            catch (HttpRequestException)
            {
                // The session expires remotely; failure to log out must not hide the seeding result.
            }
        }
        client.Dispose();
    }

    public static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    public static string? Scalar(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var item)) return null;
        return item.ValueKind switch
        {
            JsonValueKind.String => item.GetString(),
            JsonValueKind.Number => item.GetRawText(),
            _ => null,
        };
    }

    private static StringContent Content(object body) =>
        new(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

    private static string Absolute(string path) => path.StartsWith('/') ? path : "/b1s/v2/" + path;

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new ServiceLayerException(response.StatusCode, operation, body);
    }
}

internal sealed class ServiceLayerException(HttpStatusCode statusCode, string operation, string body)
    : Exception($"{operation} failed with HTTP {(int)statusCode}.\n{body}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string Operation { get; } = operation;
    public string Body { get; } = body;
}
