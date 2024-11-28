using System.Collections.Concurrent;
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

    //private readonly CancellationTokenSource _cancellationTokenSource;
    //private readonly CancellationToken _cancellationToken;
    private readonly ConcurrentQueue<CompilerMessage> _compilerQueue;

    public EntryPointService(ILogger<EntryPointService> logger, AppConfiguration appConfiguration,
        MessageBrokerService messageBrokerService, ICompilationService compilationService, ISerializer serializer)
    {
        Constants.ApplicationLogLevel.MinimumLevel = appConfiguration.GetLoggingConfiguration().Level.ToSerilog();

        _messageBrokerService = messageBrokerService;
        _compilationService = compilationService;
        _serializer = serializer;
        _logger = logger;
        _appConfiguration = appConfiguration;
        _compilerQueue = new ConcurrentQueue<CompilerMessage>();
        //_cancellationTokenSource = new CancellationTokenSource();
        //_cancellationToken = _cancellationTokenSource.Token;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(Constants.StartupEventId, $"Starting compiler v{Assembly.GetExecutingAssembly().GetName().Version}. . .");
        _logger.LogInformation(Constants.StartupEventId, $"Minimal logging level is set to {Constants.ApplicationLogLevel.MinimumLevel}");

        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        Thread.CurrentThread.IsBackground = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop("SIGTERM");
        Console.CancelKeyPress += (_, _) => Stop("SIGINT (Ctrl + C)");

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
                parentProcess.Exited += (_, _) => Stop("parent process shutdown");

                _logger.LogInformation(Constants.StartupEventId, "Watching parent process ([{0}] {1}) for shutdown",
                    parentProcess.Id, parentProcess.ProcessName);
            }
            else
            {
                Stop("parent process exited");
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

        _messageBrokerService.Initialize(Console.OpenStandardOutput(), Console.OpenStandardInput(), cancellationToken);

        _messageBrokerService.OnMessageReceived += OnMessageReceived;

        _messageBrokerService.Start();
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(Constants.StartupEventId, "Compiler is running");

        if (_compilerQueue.IsEmpty)
        {
            _logger.LogInformation(Constants.StartupEventId, "No compile jobs to process");
            return;
        }

        if (!_compilerQueue.TryDequeue(out CompilerMessage compilerMessage))
        {
            _logger.LogInformation(Constants.StartupEventId, "Failed to dequeue compile job");
            return;
        }

        try
        {
            CompilerData compilerData = _serializer.Deserialize<CompilerData>(compilerMessage.Data);

            CompilerMessage compilationMessage =
                await _compilationService.GetCompilationAsync(compilerMessage.Id, compilerData, cancellationToken);

            await _messageBrokerService.SendMessageAsync(compilationMessage);

            _logger.LogInformation(Constants.CompileEventId,$"Completed compile job {compilerMessage.Id}");
        }
        catch (Exception exception)
        {
            _logger.LogError(Constants.CompileEventId,
                $"Error occurred while compiling job {compilerMessage.Id}: {exception}");
        }
    }

    private void OnMessageReceived(CompilerMessage compilerMessage)
    {
        _logger.LogInformation($"Received message {compilerMessage.Id} of type {compilerMessage.Type}");
        /*if (cancellationToken.IsCancellationRequested)
        {
            return;
        }*/

        switch (compilerMessage.Type)
        {
            case MessageType.Data:
            {
                CompilerData compilerData = _serializer.Deserialize<CompilerData>(compilerMessage.Data);

                _logger.LogDebug(Constants.CompileEventId,
                    $"Received compile job {compilerMessage.Id} | Plugins: {compilerData.SourceFiles.Length}, References: {compilerData.ReferenceFiles.Length}");

                _compilerQueue.Enqueue(compilerMessage);
                break;
            }
            case MessageType.Heartbeat:
            {
                //_logger.LogDebug(Constants.HeartbeatEventId, "Received heartbeat from client");
                break;
            }
            case MessageType.Shutdown:
            {
                Stop("compiler stream");
                break;
            }
        }
    }

    private void Stop(string? source)
    {
        string message = "Termination request has been received";
        if (!string.IsNullOrWhiteSpace(message))
        {
            message += $" from {source}";
        }

        _logger.LogInformation(Constants.ShutdownEventId, message);

        //_cancellationTokenSource.Cancel();

        _messageBrokerService.OnMessageReceived -= OnMessageReceived;
        _messageBrokerService.Stop();

        Environment.Exit(0);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        //throw new NotImplementedException();

    }
}
