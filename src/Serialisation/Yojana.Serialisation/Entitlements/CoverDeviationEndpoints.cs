using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Yojana.Platform.Contracts.Security;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Entitlements;

public sealed record CoverChangesRequest(DateOnly? SoldFrom = null, DateOnly? SoldTo = null, Guid? Customer = null,
    string? ItemFamily = null, string? ChangeKind = null, string? Serial = null, string? Cursor = null, int? Take = null)
{
    public CoverChangeFilters Filters => new(SoldFrom, SoldTo, Customer, ItemFamily, ChangeKind, Serial);
}

public static class CoverDeviationEndpoints
{
    public static void MapCoverDeviationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/units/{unitId:guid}/cover-since-sale", async (Guid unitId, CoverDeviationQueries queries, CancellationToken ct) =>
            await queries.ForUnitAsync(unitId, ct) is { } result ? Results.Ok(result) : Results.NotFound())
            .RequireAuthorization(YojanaPolicy.TenantMember);
        var report = app.MapGroup("/api/entitlements/cover-changes").RequireAuthorization(YojanaPolicy.TenantMember);
        report.MapGet("", async ([AsParameters] CoverChangesRequest request, CoverDeviationQueries queries, CancellationToken ct) =>
        {
            try { return Results.Ok(await queries.PageAsync(request.Filters, request.Cursor, request.Take ?? 25, ct)); }
            catch (ArgumentException e) { return Invalid(e.Message); }
        });
        report.MapGet("/summary", async ([AsParameters] CoverChangesRequest request, CoverDeviationQueries queries, CancellationToken ct) =>
        {
            try { return Results.Ok(await queries.SummaryAsync(request.Filters, ct)); }
            catch (ArgumentException e) { return Invalid(e.Message); }
        });
        report.MapGet("/export", async ([AsParameters] CoverChangesRequest request, string? format, CoverDeviationQueries queries,
            ITabularExporter exporter, HttpContext http, CancellationToken ct) =>
        {
            var selected = (format ?? "csv").ToLowerInvariant();
            if (selected is not ("csv" or "xlsx")) return Invalid("Choose CSV or XLSX.");
            try
            {
                if (await queries.ExportTooLargeAsync(request.Filters, ct)) return Invalid("Export exceeds 100,000 changes. Narrow the filters.");
            }
            catch (ArgumentException e) { return Invalid(e.Message); }
            http.Response.Headers["X-Export-Row-Limit"] = "100000";
            var table = new TabularExport(
                ["Serial", "Item", "Customer", "Item family", "Sold", "Sale document", "Cover", "Policy", "Change", "Business date", "Months after sale",
                    "Document", "Line", "Amount", "Currency", "Payer", "Actor", "Approver", "Reason", "Before and after"], queries.ExportAsync(request.Filters, ct));
            if (selected == "xlsx")
            {
                // The spreadsheet package needs a seekable stream and synchronous ZIP writes. Buffer to a temporary
                // file, then let ASP.NET send it asynchronously; a 100,000-row export never occupies a workbook in RAM.
                var path = Path.Combine(Path.GetTempPath(), "yojana-cover-" + Guid.NewGuid().ToString("N") + ".xlsx");
                var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                try
                {
                    await exporter.WriteAsync(selected, table, file, ct);
                    file.Position = 0;
                    return Results.File(file, exporter.ContentType(selected), "cover-changes.xlsx");
                }
                catch { await file.DisposeAsync(); throw; }
            }
            return Results.Stream(stream => exporter.WriteAsync(selected, table, stream, http.RequestAborted),
                exporter.ContentType(selected), "cover-changes.csv");
        });
    }

    private static IResult Invalid(string message) => Results.Problem(statusCode: 400, title: "Invalid cover changes request", detail: message);
}
