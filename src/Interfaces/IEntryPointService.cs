namespace Oxide.CompilerServices.Interfaces;

public interface IEntryPointService
{
    public Task StartAsync(CancellationToken cancellationToken);

    public Task ExecuteAsync(CancellationToken cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken);
}
