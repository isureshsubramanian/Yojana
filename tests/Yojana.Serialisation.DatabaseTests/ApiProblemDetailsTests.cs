using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Yojana.Serialisation.DatabaseTests;

[Collection("serialisation host")]
public sealed class ApiProblemDetailsTests(SerialisationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Unhandled_api_exception_is_problem_details_even_in_development()
    {
        using var client = fixture.Client();

        using var response = await client.GetAsync("/api/test/unhandled");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("Something went wrong", problem.Title);
        Assert.Equal("Something went wrong (HTTP 500)", problem.Detail);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret stack", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("yojana.session", body, StringComparison.OrdinalIgnoreCase);
    }

    [DatabaseTheory]
    [InlineData("\"{\\\"name\\\":\\\"a\\\",\\\"count\\\":1}\"")] // the body encoded twice: a JSON string, not an object
    [InlineData("{\"name\":")]
    [InlineData("{\"name\":\"a\",\"count\":\"many\"}")]
    public async Task Unreadable_request_body_is_a_400_problem_not_a_500(string body)
    {
        using var client = fixture.Client();

        using var response = await client.PostAsync("/api/test/body",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("Request could not be read", problem.Title);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("ProblemTestBody", text, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task Readable_request_body_still_reaches_the_endpoint()
    {
        using var client = fixture.Client();

        using var response = await client.PostAsJsonAsync("/api/test/body", new { name = "a", count = 1 });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
