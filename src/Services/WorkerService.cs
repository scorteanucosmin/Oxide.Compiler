using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Oxide.CompilerServices.Interfaces;

namespace Oxide.CompilerServices.Services;

public class WorkerService : BackgroundService
{
    private readonly ILogger<WorkerService> _logger;
    private readonly IEntryPointService _entryPointService;

    public WorkerService(ILogger<WorkerService> logger, IEntryPointService entryPointService)
    {
        _logger = logger;
        _entryPointService = entryPointService;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Worker Service is starting.");

        await _entryPointService.StartAsync(cancellationToken);
        await base.StartAsync(cancellationToken);

        _logger.LogInformation("Worker Service has started.");
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _entryPointService.ExecuteAsync(cancellationToken);
            await Task.Delay(1000, cancellationToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Worker Service is stopping.");

        await _entryPointService.StopAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }
}
