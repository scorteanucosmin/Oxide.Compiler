using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Oxide.CompilerServices.Common;
using Oxide.CompilerServices.Enums;
using Oxide.CompilerServices.Interfaces;
using Oxide.CompilerServices.Models.Compiler;
using Oxide.CompilerServices.Models.Configuration;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Oxide.CompilerServices.Services;

public class EntryPointService : IEntryPointService
{
    private readonly ILogger _logger;
    private readonly AppConfiguration _appConfiguration;
    private readonly MessageBrokerService _messageBrokerService;
    private readonly ICompilationService _compilationService;
    private readonly ISerializer _serializer;

    public EntryPointService(ILogger<EntryPointService> logger, AppConfiguration appConfiguration,
        MessageBrokerService messageBrokerService, ICompilationService compilationService, ISerializer serializer)
    {
        Constants.ApplicationLogLevel.MinimumLevel = appConfiguration.GetLoggingConfiguration().Level.ToSerilog();

        _logger = logger;
        _appConfiguration = appConfiguration;
        _messageBrokerService = messageBrokerService;
        _compilationService = compilationService;
        _serializer = serializer;
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
        _messageBrokerService.OnMessageReceived += compilerMessage => OnMessageReceivedAsync(compilerMessage, cancellationToken);

        await Task.Delay(2000, cancellationToken);

        await _messageBrokerService.SendReadyMessageAsync(cancellationToken);
    }

    private async ValueTask OnMessageReceivedAsync(CompilerMessage compilerMessage, CancellationToken cancellationToken)
    {
        switch (compilerMessage.Type)
        {
            case MessageType.Data:
            {
                try
                {
                    CompilerData compilerData = _serializer.Deserialize<CompilerData>(compilerMessage.Data);

                    _logger.LogDebug(Constants.CompileEventId,
                        $"Received compile job {compilerMessage.Id} | Plugins: {compilerData.SourceFiles.Length}, References: {compilerData.ReferenceFiles.Length}");

                    CompilerMessage compilationMessage =
                        await _compilationService.GetCompilationAsync(compilerMessage.Id, compilerData,
                            cancellationToken);

                    await _messageBrokerService.SendMessageAsync(compilationMessage, cancellationToken);

                    _logger.LogInformation(Constants.CompileEventId, $"Completed compile job {compilerMessage.Id}");
                }
                catch (Exception exception)
                {
                    _logger.LogError(Constants.CompileEventId,
                        $"Error occurred while compiling job {compilerMessage.Id}: {exception}");
                }
                break;
            }
            case MessageType.Heartbeat:
            {
                _logger.LogInformation("Received heartbeat from server");
                break;
            }
            case MessageType.Shutdown:
            {
                Stop("shutdown", cancellationToken);
                break;
            }
            case MessageType.Unknown:
            {
                break;
            }
            case MessageType.Acknowledge:
            {
                break;
            }
            case MessageType.VersionInfo:
            {
                break;
            }
            case MessageType.Ready:
            {
                break;
            }
            case MessageType.Command:
            {
                break;
            }
            case MessageType.Error:
            {
                break;
            }
        }
    }

    private void Stop(string? source, CancellationToken cancellationToken)
    {
        string message = "Termination request has been received";
        if (!string.IsNullOrWhiteSpace(message))
        {
            message += $" from {source}";
        }

        _logger.LogInformation(Constants.ShutdownEventId, message);

        _messageBrokerService.OnMessageReceived -= compilerMessage =>
            OnMessageReceivedAsync(compilerMessage, cancellationToken);

        _messageBrokerService.Stop();

        Environment.Exit(0);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        //throw new NotImplementedException();
    }
}
