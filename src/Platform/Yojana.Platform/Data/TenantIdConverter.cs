using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Platform.Data;

public sealed class TenantIdConverter() : ValueConverter<TenantId, Guid>(id => id.Value, value => new TenantId(value));
