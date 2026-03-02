using Assistant.Api.Services.Concretes;
using Hangfire;
using Hangfire.PostgreSql;

namespace Assistant.Api.Extensions;

public static class HangfireServiceRegistration
{
    public static IServiceCollection AddHangfireServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var hangfireConnectionString = configuration.GetConnectionString("HangfireDb");

        services.AddHangfire(config => config
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(options =>
                options.UseNpgsqlConnection(hangfireConnectionString)));

        services.AddHangfireServer();
        services.AddScoped<WorkdayEndReminderJob>();

        return services;
    }
}
