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
}
