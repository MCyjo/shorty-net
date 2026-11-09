using Npgsql;

namespace Shorty.Analytics;

public static class ClickAnalyticsExtensions
{
    /// <summary>Registers the click consumer and what it needs. Needs ConnectionStrings Postgres, EventHubs and Checkpoints.</summary>
    public static IServiceCollection AddClickAnalytics(this IServiceCollection services, IConfiguration configuration)
    {
        var postgres = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Configure ConnectionStrings:Postgres.");

        services.AddNpgsqlDataSource(postgres);
        services.Configure<WorkerSettings>(configuration.GetSection(WorkerSettings.Section));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ClickStore>();
        services.AddHostedService<ClickConsumer>();
        return services;
    }
}
