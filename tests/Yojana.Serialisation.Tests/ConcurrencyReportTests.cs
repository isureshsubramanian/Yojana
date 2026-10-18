using Yojana.Bench;
using Xunit;

namespace Yojana.Serialisation.Tests;

public sealed class ConcurrencyReportTests
{
    [Fact]
    public void Summary_uses_nearest_rank_percentiles_and_excludes_expected_conflicts_from_errors()
    {
        var samples = Enumerable.Range(1, 100).Select(value => new LoadSample("edit", value, 200, null, false)).ToList();
        samples.Add(new("edit", 101, 409, "document_changed", true));
        samples.Add(new("edit", 102, 500, "internal_server_error", false));

        var result = Assert.Single(ConcurrencyReportMath.Summarise(samples, TimeSpan.FromSeconds(2)));

        Assert.Equal(102, result.Requests);
        Assert.Equal(101, result.Successful);
        Assert.Equal(1, result.Errors);
        Assert.Equal(51, result.Throughput);
        Assert.Equal(51, result.P50);
        Assert.Equal(97, result.P95);
        Assert.Equal(101, result.P99);
    }

    [Fact]
    public void Errors_group_non_expected_failures_by_type()
    {
        LoadSample[] samples =
        [
            new("edit", 1, 409, "document_changed", true),
            new("create", 2, 409, "business_rule", false),
            new("create", 3, 409, "business_rule", false),
            new("read", 4, 503, "service_unavailable", false),
        ];

        var errors = ConcurrencyReportMath.Errors(samples);

        Assert.Equal(2, errors["business_rule"]);
        Assert.Equal(1, errors["service_unavailable"]);
        Assert.DoesNotContain("document_changed", errors.Keys);
    }

    [Fact]
    public void Summary_keeps_expected_document_conflicts_in_throughput_without_counting_them_as_failures()
    {
        LoadSample[] samples =
        [
            new("document_edit", 10, 200, null, false),
            new("document_edit", 20, 409, "document_changed", true),
        ];

        var result = Assert.Single(ConcurrencyReportMath.Summarise(samples, TimeSpan.FromSeconds(4)));

        Assert.Equal(2, result.Requests);
        Assert.Equal(2, result.Successful);
        Assert.Equal(0, result.Errors);
        Assert.Equal(0.5, result.Throughput);
        Assert.Empty(ConcurrencyReportMath.Errors(samples));
    }
}
