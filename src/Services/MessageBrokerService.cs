using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Oxide.CompilerServices.Common;
using Oxide.CompilerServices.Enums;
using Oxide.CompilerServices.Interfaces;
using Oxide.CompilerServices.Models.Compiler;

namespace Oxide.CompilerServices.Services;

public class MessageBrokerService
{
    private const int DefaultMaxBufferSize = 1024;

    private readonly ILogger<MessageBrokerService> _logger;
    private readonly ICompilationService _compilationService;
    private readonly ISerializer _serializer;
    private readonly Pooling.IArrayPool<byte> _arrayPool;

    private NamedPipeClientStream _pipeClient;

    private bool _disposed;
    private int _messageId;

    public MessageBrokerService(ILogger<MessageBrokerService> logger, ICompilationService compilationService, ISerializer serializer)
    {
        _logger = logger;
        _compilationService = compilationService;
        _serializer = serializer;
        _arrayPool = Pooling.ArrayPool<byte>.Shared;
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        _pipeClient = new NamedPipeClientStream(".", "OxideNamedPipeServer",
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        await _pipeClient.ConnectAsync(cancellationToken);

        Task.Run(() => WorkerAsync(cancellationToken), cancellationToken);
    }

    private async ValueTask WorkerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            bool processed = false;

            try
            {
                CompilerMessage? compilerMessage = await ReadMessageAsync(cancellationToken);
                if (compilerMessage != null)
                {
                    await HandleReceivedMessageAsync(compilerMessage, cancellationToken);
                    processed = true;
                }
            }
            catch (Exception exception)
            {
                _logger.LogError($"Error reading message: {exception}");
            }

            if (!processed)
            {
                await Task.Delay(1000, cancellationToken);
            }
        }
    }

    private async ValueTask HandleReceivedMessageAsync(CompilerMessage compilerMessage, CancellationToken cancellationToken)
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

                    await SendMessageAsync(compilationMessage, cancellationToken);

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
                Stop();
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

    public async ValueTask SendMessageAsync(CompilerMessage message, CancellationToken cancellationToken)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        await WriteMessageAsync(message, cancellationToken);
    }

    private async ValueTask WriteMessageAsync(CompilerMessage message, CancellationToken cancellationToken)
    {
        byte[] data = _serializer.Serialize(message);
        byte[] buffer = _arrayPool.Take(data.Length + sizeof(int));
        try
        {
            int destinationIndex = data.Length.WriteBigEndian(buffer);
            Array.Copy(data, 0, buffer, destinationIndex, data.Length);
            await OnWriteAsync(buffer, 0, buffer.Length, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogError($"Error sending message to server: {exception}");
        }
        finally
        {
            _arrayPool.Return(buffer);
        }
    }

    private async ValueTask<CompilerMessage?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = _arrayPool.Take(sizeof(int));
        int read = 0;
        try
        {
            while (read < buffer.Length)
            {
                read += await OnReadAsync(buffer, read, buffer.Length - read, cancellationToken);
                if (read == 0)
                {
                    return null;
                }
            }

            int length = buffer.ReadBigEndian();
            byte[] buffer2 = _arrayPool.Take(length);
            read = 0;
            try
            {
                while (read < length)
                {
                    read += await OnReadAsync(buffer2, read, length - read, cancellationToken);
                }

                return _serializer.Deserialize<CompilerMessage>(buffer2);
            }
            finally
            {
                _arrayPool.Return(buffer2);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError($"Error reading message: {exception}");
            return null;
        }
        finally
        {
            _arrayPool.Return(buffer);
        }
    }

    private async ValueTask OnWriteAsync(byte[] buffer, int index, int count, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        Validate(buffer, index, count);

        int remaining = count;
        int written = 0;
        while (remaining > 0)
        {
            int toWrite = Math.Min(DefaultMaxBufferSize, remaining);
            await _pipeClient.WriteAsync(buffer.AsMemory(index + written, toWrite), cancellationToken);
            remaining -= toWrite;
            written += toWrite;
            await _pipeClient.FlushAsync(cancellationToken);
        }
    }

    private async ValueTask<int> OnReadAsync(byte[] buffer, int index, int count, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        Validate(buffer, index, count);

        int read = 0;
        int remaining = count;
        while (remaining > 0)
        {
            int toRead = Math.Min(DefaultMaxBufferSize, remaining);
            int r = await _pipeClient.ReadAsync(buffer.AsMemory(index + read, toRead), cancellationToken);

            if (r == 0 && read == 0)
            {
                return 0;
            }

            read += r;
            remaining -= r;
        }

        return read;
    }

    public async ValueTask<int> SendReadyMessageAsync(CancellationToken cancellationToken)
    {
        CompilerMessage message = new()
        {
            Id = _messageId++,
            Type = MessageType.Ready,
        };

        await SendMessageAsync(message, cancellationToken);
        return message.Id;
    }

    public void Stop() => Dispose();

    private void Validate(byte[] buffer, int index, int count)
    {
        if (buffer == null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Value must be zero or greater");
        }

        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Value must be zero or greater");
        }

        if (index + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException($"{nameof(index)} + {nameof(count)}",
                "Attempted to read more than buffer can allow");
        }
    }

    private void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _pipeClient.Dispose();
    }
}
