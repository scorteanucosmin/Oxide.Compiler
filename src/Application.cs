using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Oxide.CompilerServices.Common;
using Oxide.CompilerServices.Enums;
using Oxide.CompilerServices.Interfaces;
using Oxide.CompilerServices.Models.Compiler;
using Oxide.CompilerServices.Models.Configuration;
using Oxide.CompilerServices.Services;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Oxide.CompilerServices
{
    internal sealed class Application
    {
        private readonly ILogger _logger;
        private readonly OxideSettings _settings;
        private readonly MessageBrokerService _messageBrokerService;
        private readonly ICompilationService _compilationService;
        private readonly ISerializer _serializer;

        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly CancellationToken _cancellationToken;

        private readonly ConcurrentQueue<CompilerMessage> _compilerQueue;

        public Application(MessageBrokerService messageBrokerService, ILogger<Application> logger, OxideSettings options,
            CancellationTokenSource cancellationTokenSource, ICompilationService compilationService,
            ISerializer serializer)
        {
            Program.ApplicationLogLevel.MinimumLevel = options.Logging.Level.ToSerilog();

            _messageBrokerService = messageBrokerService;
            _compilationService = compilationService;
            _serializer = serializer;
            _logger = logger;
            _settings = options;
            _compilerQueue = new ConcurrentQueue<CompilerMessage>();
            _cancellationTokenSource = cancellationTokenSource;
            _cancellationToken = _cancellationTokenSource.Token;
        }

        public async ValueTask StartAsync()
        {
            _logger.LogInformation(Constants.StartupEventId, $"Starting compiler v{Assembly.GetExecutingAssembly().GetName().Version}. . .");
            _logger.LogInformation(Constants.StartupEventId, $"Minimal logging level is set to {Program.ApplicationLogLevel.MinimumLevel}");

            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            Thread.CurrentThread.IsBackground = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop("SIGTERM");
            Console.CancelKeyPress += (_, _) => Stop("SIGINT (Ctrl + C)");

            if (_settings.ParentProcess != null)
            {
                try
                {
                    if (!_settings.ParentProcess.HasExited)
                    {
                        _settings.ParentProcess.EnableRaisingEvents = true;
                        _settings.ParentProcess.Exited += (s, o) => Stop("parent process shutdown");
                        _logger.LogInformation(Constants.StartupEventId, "Watching parent process ([{id}] {name}) for shutdown",
                            _settings.ParentProcess.Id, _settings.ParentProcess.ProcessName);
                    }
                    else
                    {
                        Stop("parent process exited");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(Constants.StartupEventId, ex, "Failed to attach to parent process, compiler may stay open if parent is improperly shutdown");
                }
            }

            if (!_settings.Compiler.EnableMessageStream)
            {
                return;
            }

            _messageBrokerService.Start(Console.OpenStandardOutput(), Console.OpenStandardInput());

            _messageBrokerService.OnMessageReceived += OnMessageReceived;

            _messageBrokerService.SendReadyMessage();

            await Task.Delay(TimeSpan.FromSeconds(2), _cancellationToken);

            _logger.LogDebug(Constants.StartupEventId, "Compiler has started successfully and is awaiting jobs. . .");

            try
            {
                await WorkerAsync();
            }
            catch (TaskCanceledException)
            {
                _logger.LogDebug(Constants.ShutdownEventId, "Worker has been cancelled");
            }
        }

        private void OnMessageReceived(CompilerMessage compilerMessage)
        {
            _logger.LogInformation($"Received message from client of type: {compilerMessage.Type}");
            if (_cancellationToken.IsCancellationRequested)
            {
                return;
            }

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

        private async ValueTask WorkerAsync()
        {
            while (!_cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(500, _cancellationToken);
                if (!_compilerQueue.TryDequeue(out CompilerMessage compilerMessage))
                {
                    continue;
                }

                try
                {
                    CompilerData data = _serializer.Deserialize<CompilerData>(compilerMessage.Data);

                    await _compilationService.Compile(compilerMessage.Id, data);

                    _logger.LogInformation($"Completed compile job {compilerMessage.Id}");
                }
                catch (Exception exception)
                {
                    _logger.LogError($"Error occurred while compiling job {compilerMessage.Id}: {exception}");
                    throw;
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

            _cancellationTokenSource.Cancel();

            _messageBrokerService.OnMessageReceived -= OnMessageReceived;
            _messageBrokerService.Stop();
        }
    }
}
