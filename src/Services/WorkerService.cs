using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Oxide.CompilerServices.Interfaces;

namespace Oxide.CompilerServices.Services;

public class WorkerService : IHostedService
{
    private readonly ILogger<WorkerService> _logger;
    private readonly IEntryPointService _entryPointService;

    public WorkerService(ILogger<WorkerService> logger, IEntryPointService entryPointService)
    {
        _logger = logger;
        _entryPointService = entryPointService;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _entryPointService.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _entryPointService.StopAsync(cancellationToken);
    }
}
