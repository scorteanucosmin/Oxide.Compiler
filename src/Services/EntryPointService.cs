using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Oxide.CompilerServices.Common;
using Oxide.CompilerServices.Interfaces;
using Oxide.CompilerServices.Models.Configuration;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Oxide.CompilerServices.Services;

public class EntryPointService : IEntryPointService
{
    private readonly ILogger _logger;
    private readonly AppConfiguration _appConfiguration;
    private readonly MessageBrokerService _messageBrokerService;

    public EntryPointService(ILogger<EntryPointService> logger, AppConfiguration appConfiguration,
        MessageBrokerService messageBrokerService)
    {
        Constants.ApplicationLogLevel.MinimumLevel = appConfiguration.GetLoggingConfiguration().Level.ToSerilog();

        _logger = logger;
        _appConfiguration = appConfiguration;
        _messageBrokerService = messageBrokerService;
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(Constants.StartupEventId, $"Starting compiler v{Assembly.GetExecutingAssembly().GetName().Version}. . .");
        _logger.LogInformation(Constants.StartupEventId, $"Minimal logging level is set to {Constants.ApplicationLogLevel.MinimumLevel}");

        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        Thread.CurrentThread.IsBackground = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop("SIGTERM", cancellationToken);
        Console.CancelKeyPress += (_, _) => Stop("SIGINT (Ctrl + C)", cancellationToken);

        Process? parentProcess = _appConfiguration.GetParentProcess();
        if (parentProcess == null)
        {
            _logger.LogWarning(Constants.StartupEventId, "Parent process is not defined, compiler may stay open if parent is improperly shutdown");
            return;
        }

        try
        {
            if (!parentProcess.HasExited)
            {
                parentProcess.EnableRaisingEvents = true;
                parentProcess.Exited += (_, _) => Stop("parent process shutdown", cancellationToken);

                _logger.LogInformation(Constants.StartupEventId, "Watching parent process ([{0}] {1}) for shutdown",
                    parentProcess.Id, parentProcess.ProcessName);
            }
            else
            {
                Stop("parent process exited", cancellationToken);
                return;
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(Constants.StartupEventId, exception,
                "Failed to attach to parent process, compiler may stay open if parent is improperly shutdown");
        }

        if (!_appConfiguration.GetCompilerConfiguration().EnableMessageStream)
        {
            return;
        }

        await _messageBrokerService.StartAsync(cancellationToken);

        await _messageBrokerService.SendReadyMessageAsync(cancellationToken);
    }

    private void Stop(string? source, CancellationToken cancellationToken)
    {
        string message = "Termination request has been received";
        if (!string.IsNullOrWhiteSpace(message))
        {
            message += $" from {source}";
        }

        _logger.LogInformation(Constants.ShutdownEventId, message);

        _messageBrokerService.Stop();

        Environment.Exit(0);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        //throw new NotImplementedException();
    }
}
