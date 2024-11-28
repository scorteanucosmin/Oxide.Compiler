using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Oxide.CompilerServices.Enums;
using Oxide.CompilerServices.Interfaces;
using Oxide.CompilerServices.Models.Compiler;

namespace Oxide.CompilerServices.Services;

public class MessageBrokerService
{
    private const int DefaultMaxBufferSize = 1024;

    private readonly ILogger<MessageBrokerService> _logger;
    private readonly ISerializer _serializer;
    private CancellationToken _cancellationToken;
    private Stream _input;
    private Stream _output;

    private readonly ConcurrentQueue<CompilerMessage> _messageQueue;
    private readonly Pooling.IArrayPool<byte> _pool;

    private bool _disposed;
    private int _messageId;

    private bool CanWrite => _input.CanWrite;
    private bool CanRead => _output.CanRead;

    public event Action<CompilerMessage> OnMessageReceived;

    public MessageBrokerService(ILogger<MessageBrokerService> logger, ISerializer serializer)
    {
        _logger = logger;
        _serializer = serializer;
        _messageQueue = new ConcurrentQueue<CompilerMessage>();
        _pool = Pooling.ArrayPool<byte>.Shared;
    }

    public void Initialize(Stream input, Stream output, CancellationToken cancellationToken)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _cancellationToken = cancellationToken;

        _logger.LogInformation("Message broker service initialized");
    }

    public void Start()
    {
        Task.Run(WorkerAsync, _cancellationToken);

        _logger.LogInformation("Message broker service started");
    }

    private async ValueTask WorkerAsync()
    {
        while (!_cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Message broker is running");
            bool processed = false;
            try
            {
                for (int index = 0; index < 3; ++index)
                {
                    if (!_messageQueue.IsEmpty)
                    {
                        if (!_messageQueue.TryDequeue(out CompilerMessage compilerMessage))
                        {
                            continue;
                        }

                        await WriteMessageAsync(compilerMessage);
                        processed = true;
                    }
                    else
                    {
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                _logger.LogError($"Error sending message: {exception}");
            }

            if (processed)
            {
                continue;
            }

            if (OnMessageReceived != null)
            {
                try
                {
                    for (int index = 0; index < 3; ++index)
                    {
                        CompilerMessage? compilerMessage = await ReadMessageAsync();
                        if (compilerMessage != null)
                        {
                            OnMessageReceived(compilerMessage);
                            processed = true;
                        }
                        else
                        {
                            break;
                        }
                    }
                }
                catch (Exception exception)
                {
                    _logger.LogError($"Error reading message: {exception}");
                }
            }

            if (!processed)
            {
                await Task.Delay(1500, _cancellationToken);
            }
        }
    }

    public async ValueTask SendMessageAsync(CompilerMessage message)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        _messageQueue.Enqueue(message);
    }

    private async ValueTask WriteMessageAsync(CompilerMessage message)
    {
        byte[] data = _serializer.Serialize(message);
        byte[] buffer = _pool.Take(data.Length + sizeof(int));
        try
        {
            _logger.LogInformation($"Sending message to client of type: {message.Type}");

            int destinationIndex = data.Length.WriteBigEndian(buffer);
            Array.Copy(data, 0, buffer, destinationIndex, data.Length);
            await OnWriteAsync(buffer, 0, buffer.Length);
        }
        catch (Exception exception)
        {
            _logger.LogError($"Error sending message to client: {exception}");
        }
        finally
        {
            _pool.Return(buffer);
        }
    }

    private async ValueTask<CompilerMessage?> ReadMessageAsync()
    {
        byte[] buffer = _pool.Take(sizeof(int));
        int read = 0;
        try
        {
            while (read < buffer.Length)
            {
                read += await OnReadAsync(buffer, read, buffer.Length - read);
                if (read == 0)
                {
                    return null;
                }
            }

            int length = buffer.ReadBigEndian();
            byte[] buffer2 = _pool.Take(length);
            read = 0;
            try
            {
                while (read < length)
                {
                    read += await OnReadAsync(buffer2, read, length - read);
                }

                return _serializer.Deserialize<CompilerMessage>(buffer2);
            }
            finally
            {
                _pool.Return(buffer2);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError($"Error reading message: {exception}");
            return null;
        }
        finally
        {
            _pool.Return(buffer);
        }
    }

    private async ValueTask OnWriteAsync(byte[] buffer, int index, int count)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        if (!CanWrite)
        {
            throw new InvalidOperationException("Underlying stream does not allow writing");
        }

        Validate(buffer, index, count);

        int remaining = count;
        int written = 0;
        while (remaining > 0)
        {
            int toWrite = Math.Min(DefaultMaxBufferSize, remaining);
            await _input.WriteAsync(buffer, index + written, toWrite, _cancellationToken);
            remaining -= toWrite;
            written += toWrite;
            await _input.FlushAsync(_cancellationToken);
        }
    }

    private async ValueTask<int> OnReadAsync(byte[] buffer, int index, int count)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        if (!CanRead)
        {
            throw new InvalidOperationException("Underlying stream does not allow reading");
        }

        Validate(buffer, index, count);

        int read = 0;
        int remaining = count;
        while (remaining > 0)
        {
            int toRead = Math.Min(DefaultMaxBufferSize, remaining);
            int r = await _output.ReadAsync(buffer, index + read, toRead, _cancellationToken);

            if (r == 0 && read == 0)
            {
                return 0;
            }

            read += r;
            remaining -= r;
        }

        return read;
    }

    public async ValueTask<int> SendReadyMessageAsync()
    {
        CompilerMessage message = new()
        {
            Id = _messageId++,
            Type = MessageType.Ready,
        };

        await SendMessageAsync(message);
        return message.Id;
    }

    public void Stop() => Dispose(true);

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

    private void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!disposing)
        {
            return;
        }

        _messageQueue.Clear();
    }
}
