using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.PublicApi;

public static class PublicApiRegistration
{
    public static IHostApplicationBuilder AddSerialisationPublicApi(this IHostApplicationBuilder builder)
    {
        builder.Services.AddScoped<IPublicApiReadPort, PublicApiReadPort>();
        builder.Services.AddScoped<IPublicApiWritePort, PublicApiWritePort>();
        return builder;
    }
}
