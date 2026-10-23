using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;

namespace Yojana.Platform.DatabaseTests;

[Collection("platform host")]
public sealed class UtcDateTimeTests(PlatformHostFixture fixture)
{
    [Fact]
    public async Task Stored_times_read_back_as_utc_and_serialise_with_z()
    {
        var id = new TenantId(Guid.NewGuid());
        var created = new DateTime(2026, 10, 23, 22, 5, 20, 506, DateTimeKind.Utc);
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.Tenants.Add(new Tenant { Id = id, Code = ("utc" + Guid.NewGuid().ToString("N"))[..20], Name = "utc test", CreatedAt = created });
            await db.SaveChangesAsync();
        }

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var read = await db.Tenants.AsNoTracking().Where(row => row.Id == id).Select(row => row.CreatedAt).SingleAsync();
            Assert.Equal(DateTimeKind.Utc, read.Kind);
            Assert.Equal(created, read);
            Assert.Equal("\"2026-10-23T22:05:20.506Z\"", JsonSerializer.Serialize(read));

            // A local-time filter means the same instant: it is converted to UTC before it reaches SQL.
            var local = created.ToLocalTime();
            Assert.True(await db.Tenants.AsNoTracking().AnyAsync(row => row.Id == id && row.CreatedAt >= local && row.CreatedAt <= local));
        }
    }
}
