using Assistant.Api.Services.Abstracts;

namespace Assistant.Api.Services.Concretes;

public class MemoryMaintenanceJob(
    IMemoryMaintenanceService memoryMaintenanceService,
    ILogger<MemoryMaintenanceJob> logger)
{
    public async Task ExecuteAsync()
    {
        logger.LogInformation("Memory maintenance job started.");
        await memoryMaintenanceService.RunNightlyMaintenanceAsync(CancellationToken.None);
        logger.LogInformation("Memory maintenance job finished.");
    }
}
