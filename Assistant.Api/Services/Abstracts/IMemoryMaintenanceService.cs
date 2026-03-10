namespace Assistant.Api.Services.Abstracts;

public interface IMemoryMaintenanceService
{
    Task RunNightlyMaintenanceAsync(CancellationToken cancellationToken);
}
