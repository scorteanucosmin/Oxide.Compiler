using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Oxide.CompilerServices.Enums;
using Oxide.CompilerServices.Interfaces;
using Oxide.CompilerServices.Models.Compiler;

namespace Oxide.CompilerServices.Services;

public class MessageBrokerService
{
    private readonly ILogger<MessageBrokerService> _logger;

    private readonly ISerializer _serializer;

    private readonly CancellationTokenSource _cancellationTokenSource;

    private readonly CancellationToken _cancellationToken;

    private Stream _input;

    private Stream _output;

    private readonly ConcurrentQueue<CompilerMessage> _messageQueue;

    private readonly Pooling.IArrayPool<byte> _pool;

    private bool _disposed;

    private int _messageId;

    public event Action<CompilerMessage> OnMessageReceived;

    public MessageBrokerService(ILogger<MessageBrokerService> logger, ISerializer serializer, CancellationTokenSource cancellationTokenSource)
    {
        _logger = logger;
        _serializer = serializer;
        _cancellationTokenSource = cancellationTokenSource;
        _cancellationToken = _cancellationTokenSource.Token;
        _messageQueue = new ConcurrentQueue<CompilerMessage>();
        _pool = Pooling.ArrayPool<byte>.Shared;
    }

    public void Start(Stream input, Stream output)
    {
        _input = input;
        _output = output;

        Task.Run(WorkerAsync, _cancellationToken);

        _logger.LogInformation("Message broker service started");
    }

    public void SendMessage(CompilerMessage message)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        //_messageQueue.Enqueue(message);
        WriteMessage(message);
    }

    private void WriteMessage(CompilerMessage message)
    {
        byte[] sourceArray = _serializer.Serialize(message);
        byte[] numArray = _pool.Take(sourceArray.Length + 4);
        try
        {
            _logger.LogInformation($"Sending message to client of type: {message.Type}");

            int destinationIndex = sourceArray.Length.WriteBigEndian(numArray);
            Array.Copy(sourceArray, 0, numArray, destinationIndex, sourceArray.Length);
            _input.Write(numArray, 0, numArray.Length);
        }
        catch (Exception exception)
        {
            _logger.LogError($"Error sending message to client: {exception}");
        }
        finally
        {
            _pool.Return(numArray);
        }
    }

    private CompilerMessage? ReadMessage()
    {
        byte[] numArray1 = _pool.Take(4);
        int index1 = 0;
        try
        {
            while (index1 < numArray1.Length)
            {
                index1 += _output.Read(numArray1, index1, numArray1.Length - index1);
                if (index1 == 0)
                {
                    return null;
                }
            }

            int length = numArray1.ReadBigEndian();
            byte[] numArray2 = _pool.Take(length);
            int index2 = 0;
            try
            {
                while (index2 < length)
                {
                    index2 += _output.Read(numArray2, index2, length - index2);
                }

                return _serializer.Deserialize<CompilerMessage>(numArray2);
            }
            finally
            {
                _pool.Return(numArray2);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError($"Error reading message: {exception}");
            return null;
        }
        finally
        {
            _pool.Return(numArray1);
        }
    }

    private async ValueTask WorkerAsync()
    {
        while (!_cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Worker method running");

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

                        WriteMessage(compilerMessage);
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

            if (OnMessageReceived != null)
            {
                try
                {
                    for (int index = 0; index < 3; ++index)
                    {
                        CompilerMessage? compilerMessage = ReadMessage();
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
                await Task.Delay(500, _cancellationToken);
            }
        }
    }

    public int SendReadyMessage()
    {
        CompilerMessage message = new()
        {
            Id = _messageId++,
            Type = MessageType.Ready,
        };

        SendMessage(message);
        return message.Id;
    }

    public void Stop() => Dispose(true);

    private void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing)
        {
            /*if (this.Receiever is IDisposable receiever)
                receiever.Dispose();
            if (this.Transmitter is IDisposable transmitter)
                transmitter.Dispose();
            if (this.Formatter is IDisposable formatter)
                formatter.Dispose();*/
            _messageQueue.Clear();
        }
    }
}
