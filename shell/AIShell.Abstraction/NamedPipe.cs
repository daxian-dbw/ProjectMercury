﻿using System.Buffers;
using System.IO.Pipes;
using System.Text.Json;

namespace AIShell.Abstraction;

/// <summary>
/// Message types.
/// </summary>
public enum MessageType : int
{
    /// <summary>
    /// A query from command-line shell to AIShell.
    /// </summary>
    PostQuery = 0,

    /// <summary>
    /// A message from AIShell to command-line shell to ask for conncetion.
    /// </summary>
    AskConnection = 1,

    /// <summary>
    /// A message from AIShell to command-line shell to ask for context.
    /// </summary>
    AskContext = 2,

    /// <summary>
    /// A message from command-line shell to AIShell to post context.
    /// </summary>
    PostContext = 3,

    /// <summary>
    /// A message from AIShell to command-line shell to send code block.
    /// </summary>
    PostCode = 4,
}

/// <summary>
/// Base class for all pipe messages.
/// </summary>
public abstract class PipeMessage
{
    public MessageType Type { get; }

    protected PipeMessage(MessageType type)
    {
        Type = type;
    }
}

/// <summary>
/// Message for <see cref="MessageType.PostQuery"/>.
/// </summary>
public sealed class PostQueryMessage : PipeMessage
{
    /// <summary>
    /// Gets the query.
    /// </summary>
    public string Query { get; }

    /// <summary>
    /// Gets the context information.
    /// </summary>
    public string Context { get; set; }

    /// <summary>
    /// Gets the agent to use for the query.
    /// </summary>
    public string Agent { get; set; }

    /// <summary>
    /// Creates an instance of <see cref="PostQueryMessage"/>.
    /// </summary>
    public PostQueryMessage(string query, string context, string agent)
        : base(MessageType.PostQuery)
    {
        ArgumentException.ThrowIfNullOrEmpty(query);
        Query = query;
        Context = context;
        Agent = agent;
    }
}

/// <summary>
/// Message for <see cref="MessageType.AskConnection"/>.
/// </summary>
public sealed class AskConnectionMessage : PipeMessage
{
    /// <summary>
    /// Gets the pipe name for the shell to connect as a client.
    /// </summary>
    public string PipeName { get; }

    /// <summary>
    /// Creates an instance of <see cref="AskConnectionMessage"/>.
    /// </summary>
    /// <param name="pipeName"></param>
    public AskConnectionMessage(string pipeName)
        : base(MessageType.AskConnection)
    {
        ArgumentException.ThrowIfNullOrEmpty(pipeName);
        PipeName = pipeName;
    }
}

/// <summary>
/// Message for <see cref="MessageType.AskContext"/>.
/// </summary>
public sealed class AskContextMessage : PipeMessage
{
    /// <summary>
    /// Creates an instance of <see cref="AskContextMessage"/>.
    /// </summary>
    public AskContextMessage()
        : base(MessageType.AskContext)
    {
    }
}

/// <summary>
/// Message for <see cref="MessageType.PostContext"/>.
/// </summary>
public sealed class PostContextMessage : PipeMessage
{
    /// <summary>
    /// Represents a none instance to be used when the shell has no context information to return.
    /// </summary>
    public static readonly PostContextMessage None = new([]);

    /// <summary>
    /// Gets the command history.
    /// </summary>
    public List<string> CommandHistory { get; }

    /// <summary>
    /// Creates an instance of <see cref="PostContextMessage"/>.
    /// </summary>
    public PostContextMessage(List<string> commandHistory)
        : base(MessageType.PostContext)
    {
        ArgumentNullException.ThrowIfNull(commandHistory);
        CommandHistory = commandHistory;
    }
}

/// <summary>
/// Message for <see cref="MessageType.PostCode"/>.
/// </summary>
public sealed class PostCodeMessage : PipeMessage
{
    /// <summary>
    /// Gets the code blocks that are posted to the shell.
    /// </summary>
    public List<string> CodeBlocks { get; }

    /// <summary>
    /// Creates an instance of <see cref="PostCodeMessage"/>.
    /// </summary>
    public PostCodeMessage(List<string> codeBlocks)
        : base(MessageType.PostCode)
    {
        ArgumentNullException.ThrowIfNull(codeBlocks);
        CodeBlocks = codeBlocks;
    }
}

/// <summary>
/// The base type for common pipe operations.
/// </summary>
public abstract class PipeCommon : IDisposable
{
    private readonly string _pipeName;
    private readonly byte[] _lengthBuffer;
    private readonly PipeStream _pipeStream;
    private bool _disposed;

    /// <summary>
    /// Gets the pipe name.
    /// </summary>
    public string PipeName => _pipeName;

    /// <summary>
    /// Gets whether the pipe is connected.
    /// </summary>
    public bool Connected => _pipeStream.IsConnected;

    /// <summary>
    /// Gets the pipe stream.
    /// </summary>
    protected PipeStream PipeStream => _pipeStream;

    /// <summary>
    /// Base type constructor.
    /// </summary>
    protected PipeCommon(string pipeName, PipeStream pipeStream)
    {
        ArgumentException.ThrowIfNullOrEmpty(pipeName);
        ArgumentNullException.ThrowIfNull(pipeStream);

        _pipeName = pipeName;
        _lengthBuffer = new byte[4];
        _pipeStream = pipeStream;
    }

    /// <summary>
    /// Dispose the pipe object.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _pipeStream.Close();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Send a message.
    /// </summary>
    /// <typeparam name="T">Type of the message.</typeparam>
    /// <param name="message">The message instance.</param>
    /// <returns>The length of bytes written to the pipe stream.</returns>
    /// <exception cref="IOException">Throws when the pipe is closed by the other side.</exception>
    protected int SendMessage<T>(T message) where T : PipeMessage
    {
        if (!Connected)
        {
            throw new IOException("Pipe is not connected or has been closed.");
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message);

        // 1st byte: message type
        // next 4 bytes: message length
        // following bytes: message payload
        _pipeStream.WriteByte((byte)message.Type);
        _pipeStream.Write(BitConverter.GetBytes(bytes.Length));
        _pipeStream.Write(bytes);
        _pipeStream.Flush();

        return bytes.Length + 4 + 1;
    }

    /// <summary>
    /// Read a message from the pipe stream.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The received message.</returns>
    /// <exception cref="IOException">Throws when received data is corrupted.</exception>
    protected async Task<PipeMessage> GetMessageAsync(CancellationToken cancellationToken)
    {
        if (!Connected)
        {
            throw new IOException("Pipe is not connected or has been closed.");
        }

        try
        {
            // Read the message type: 1 byte.
            await _pipeStream.ReadExactlyAsync(_lengthBuffer.AsMemory(0, 1), cancellationToken);

            byte type = _lengthBuffer[0];
            if (type > (int)MessageType.PostCode)
            {
                _pipeStream.Close();
                throw new IOException($"Unknown message type received: {type}. Connection was dropped.");
            }

            // Read the message length: 4 bytes.
            await _pipeStream.ReadExactlyAsync(_lengthBuffer.AsMemory(), cancellationToken);

            // Rent byte buffer for the actual message.
            int length = BitConverter.ToInt32(_lengthBuffer);
            var jsonBuffer = ArrayPool<byte>.Shared.Rent(length);

            try
            {
                // Read and deserialize the actual message.
                await _pipeStream.ReadExactlyAsync(jsonBuffer.AsMemory(0, length), cancellationToken);
                return DeserializePayload(type, jsonBuffer.AsSpan(0, length));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(jsonBuffer);
            }
        }
        catch (EndOfStreamException)
        {
            // Pipe closed.
            return null;
        }
        catch (OperationCanceledException)
        {
            // Read operation was cancelled.
            return null;
        }
    }

    /// <summary>
    /// Deserialize payload bytes into a specific type of message.
    /// </summary>
    /// <param name="type">The message type.</param>
    /// <param name="bytes">The payload data.</param>
    /// <exception cref="NotSupportedException">Throws when the given message type is not supported.</exception>
    private static PipeMessage DeserializePayload(int type, ReadOnlySpan<byte> bytes)
    {
        return type switch
        {
            (int)MessageType.PostQuery => JsonSerializer.Deserialize<PostQueryMessage>(bytes),
            (int)MessageType.AskConnection => JsonSerializer.Deserialize<AskConnectionMessage>(bytes),
            (int)MessageType.PostContext => JsonSerializer.Deserialize<PostContextMessage>(bytes),
            (int)MessageType.AskContext => JsonSerializer.Deserialize<AskContextMessage>(bytes),
            (int)MessageType.PostCode => JsonSerializer.Deserialize<PostCodeMessage>(bytes),
            _ => throw new NotSupportedException("Unreachable code"),
        };
    }
}

/// <summary>
/// The type represents the server end of a named pipe in the shell side.
/// </summary>
public sealed class ShellServerPipe : PipeCommon
{
    private readonly NamedPipeServerStream _server;

    /// <summary>
    /// Creates an instance of <see cref="ShellServerPipe"/>.
    /// </summary>
    /// <param name="pipeName">The pipe name to create the <see cref="NamedPipeServerStream"/>.</param>
    public ShellServerPipe(string pipeName)
        : base(pipeName, new NamedPipeServerStream(pipeName))
    {
        _server = (NamedPipeServerStream)PipeStream;
    }

    /// <summary>
    /// Starts to receive and process messages sent from client.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async Task StartProcessingAsync(int timeout, CancellationToken cancellationToken)
    {
        if (timeout <= 0 && timeout != Timeout.Infinite)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout value should be greater than 0 or equal to -1 (infinite).");
        }

        try
        {
            CancellationToken tokenForConnection = cancellationToken;
            if (timeout > 0)
            {
                var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                source.CancelAfter(timeout);
                tokenForConnection = source.Token;
            }

            await _server.WaitForConnectionAsync(tokenForConnection);
            var message = await GetMessageAsync(cancellationToken);

            if (message is not AskConnectionMessage askConnection)
            {
                _server.Close();
                // Log: first message is unexpected.
                string error = message is null
                    ? "Pipe was closed or broken."
                    : $"Expect the first message to be '{nameof(MessageType.AskConnection)}', but it was '{message.Type}'.";
                throw new InvalidOperationException(error);
            }

            var client = new ShellClientPipe(askConnection.PipeName);
            await client.ConnectAsync(timeout, cancellationToken);
            InvokeOnAskConnection(client, exception: null);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                ex = new TimeoutException("Could not receive connection from AISH within the specified timeout period.");
            }

            InvokeOnAskConnection(shellClient: null, ex);

            // Failed to establish the connection, so return.
            return;
        }

        while (true)
        {
            var message = await GetMessageAsync(cancellationToken);
            if (message is null)
            {
                // Log: pipe closed/broken, or the server is about to exit.
                break;
            }

            switch (message.Type)
            {
                case MessageType.AskContext:
                    var response = InvokeOnAskContext((AskContextMessage)message) ?? PostContextMessage.None;
                    SendMessage(response);
                    break;

                case MessageType.PostCode:
                    InvokeOnPostCode((PostCodeMessage)message);
                    break;

                default:
                    // Log: unexpected messages ignored.
                    break;
            }
        }
    }

    /// <summary>
    /// Helper to invoke the <see cref="OnPostCode"/> event.
    /// </summary>
    private void InvokeOnPostCode(PostCodeMessage message)
    {
        if (OnPostCode is null)
        {
            // Log: event handler not set.
            return;
        }

        try
        {
            OnPostCode(message);
        }
        catch (Exception)
        {
            // Log: exception when invoking 'OnPostCode'
        }
    }

    /// <summary>
    /// Helper to invoke the <see cref="OnAskConnection"/> event.
    /// </summary>
    private void InvokeOnAskConnection(ShellClientPipe shellClient, Exception exception)
    {
        if (OnAskConnection is null)
        {
            // Log: event handler not set.
            return;
        }

        try
        {
            OnAskConnection(shellClient, exception);
        }
        catch (Exception)
        {
            // Log: exception when invoking 'OnAskConnection'
        }
    }

    /// <summary>
    /// Helper to invoke the <see cref="OnAskContext"/> event.
    /// </summary>
    private PostContextMessage InvokeOnAskContext(AskContextMessage message)
    {
        if (OnAskContext is null)
        {
            // Log: event handler not set.
            return null;
        }

        try
        {
            return OnAskContext(message);
        }
        catch (Exception)
        {
            // Log: exception when invoking 'OnAskContext'
        }

        return null;
    }

    /// <summary>
    /// Event for handling the <see cref="MessageType.PostCode"/> message.
    /// </summary>
    public event Action<PostCodeMessage> OnPostCode;

    /// <summary>
    /// Event for handling the <see cref="MessageType.AskConnection"/> message.
    /// </summary>
    public event Action<ShellClientPipe, Exception> OnAskConnection;

    /// <summary>
    /// Event for handling the <see cref="MessageType.AskContext"/> message.
    /// </summary>
    public event Func<AskContextMessage, PostContextMessage> OnAskContext;
}

/// <summary>
/// The type represents the client end of a named pipe in the shell side.
/// </summary>
public sealed class ShellClientPipe : PipeCommon
{
    private readonly NamedPipeClientStream _client;

    /// <summary>
    /// Creates an instance of <see cref="ShellClientPipe"/>.
    /// </summary>
    /// <param name="pipeName">The pipe name to create the <see cref="NamedPipeClientStream"/>.</param>
    public ShellClientPipe(string pipeName)
        : base(pipeName, new NamedPipeClientStream(pipeName))
    {
        _client = (NamedPipeClientStream)PipeStream;
    }

    /// <summary>
    /// Connect to the server end of the pipe on the AIShell side.
    /// </summary>
    /// <param name="timeout">Timeout for the connection attempt.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    internal Task ConnectAsync(int timeout, CancellationToken cancellationToken) => _client.ConnectAsync(timeout, cancellationToken);

    /// <summary>
    /// Post a query to AIShell.
    /// </summary>
    /// <param name="message">The <see cref="MessageType.PostQuery"/> message.</param>
    /// <exception cref="IOException">Throws when the pipe is closed by the other side.</exception>
    public void PostQuery(PostQueryMessage message) => SendMessage(message);
}

/// <summary>
/// The type represents the server end of a named pipe in the AIShell side.
/// </summary>
public sealed class AishServerPipe : PipeCommon
{
    private readonly NamedPipeServerStream _server;

    /// <summary>
    /// Creates an instance of <see cref="AishServerPipe"/>.
    /// </summary>
    /// <param name="pipeName">The pipe name to create the <see cref="NamedPipeServerStream"/>.</param>
    public AishServerPipe(string pipeName)
        : base(pipeName, new NamedPipeServerStream(pipeName))
    {
        _server = (NamedPipeServerStream)PipeStream;
    }

    /// <summary>
    /// Starts to receive and process messages sent from client.
    /// </summary>
    /// <param name="timeout">The number of milliseconds to wait for a client to connect to the server.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async Task StartProcessingAsync(int timeout, CancellationToken cancellationToken)
    {
        if (timeout <= 0 && timeout != Timeout.Infinite)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout value should be greater than 0 or equal to -1 (infinite).");
        }

        try
        {
            CancellationToken tokenForConnection = cancellationToken;
            if (timeout > 0)
            {
                var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                source.CancelAfter(timeout);
                tokenForConnection = source.Token;
            }

            await _server.WaitForConnectionAsync(tokenForConnection);
            InvokeOnConnectedOrFailed(exception: null);
        }
        catch (Exception exception)
        {
            if (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                exception = new TimeoutException("Could not receive connection from a client within the specified timeout period.");
            }

            InvokeOnConnectedOrFailed(exception);
            return;
        }

        while (true)
        {
            var message = await GetMessageAsync(cancellationToken);
            if (message is null)
            {
                // Log: pipe closed/broken, or the server is about to exit.
                break;
            }

            switch (message.Type)
            {
                case MessageType.PostQuery:
                    InvokeOnPostQuery((PostQueryMessage)message);
                    break;

                default:
                    // Log: unexpected messages ignored.
                    break;
            }
        }
    }

    /// <summary>
    /// Helper to invoke the <see cref="OnPostQuery"/> event.
    /// </summary>
    private void InvokeOnPostQuery(PostQueryMessage message)
    {
        if (OnPostQuery is null)
        {
            // Log: event handler not set.
            return;
        }

        try
        {
            OnPostQuery(message);
        }
        catch (Exception)
        {
            // Log: exception when invoking 'OnPostCode'
        }
    }

    private void InvokeOnConnectedOrFailed(Exception exception)
    {
        if (OnConnectedOrFailed is null)
        {
            // Log: event handler not set.
            return;
        }

        try
        {
            OnConnectedOrFailed(exception);
        }
        catch (Exception)
        {
            // Log: exception when invoking 'OnConnectedOrFailed'
        }
    }

    /// <summary>
    /// Event for handling the <see cref="MessageType.PostQuery"/> message.
    /// </summary>
    public event Action<PostQueryMessage> OnPostQuery;

    /// <summary>
    /// Event to be triggered when connection to the server pipe succeeds or fails.
    /// </summary>
    public event Action<Exception> OnConnectedOrFailed;
}

/// <summary>
/// The type represents the client end of a named pipe in the AIShell side.
/// </summary>
public sealed class AishClientPipe : PipeCommon
{
    private readonly NamedPipeClientStream _client;

    /// <summary>
    /// Creates an instance of <see cref="AishClientPipe"/>.
    /// </summary>
    /// <param name="pipeName">The pipe name to create the <see cref="NamedPipeClientStream"/>.</param>
    public AishClientPipe(string pipeName)
        : base(pipeName, new NamedPipeClientStream(pipeName))
    {
        _client = (NamedPipeClientStream)PipeStream;
    }

    /// <summary>
    /// Connect to the server end of the pipe on the shell side.
    /// </summary>
    /// <param name="timeout">Timeout for the connection attempt.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public Task ConnectAsync(int timeout, CancellationToken cancellationToken) => _client.ConnectAsync(timeout, cancellationToken);

    /// <summary>
    /// Post code blocks to the shell.
    /// </summary>
    /// <param name="message">The <see cref="MessageType.PostCode"/> message.</param>
    /// <exception cref="IOException">Throws when the pipe is closed by the other side.</exception>
    public void PostCode(PostCodeMessage message) => SendMessage(message);

    /// <summary>
    /// Ask connection from the shell.
    /// </summary>
    /// <param name="message">The <see cref="MessageType.AskConnection"/> message.</param>
    /// <exception cref="IOException">Throws when the pipe is closed by the other side.</exception>
    public void AskConnection(AskConnectionMessage message) => SendMessage(message);

    /// <summary>
    /// Ask context information from the shell.
    /// </summary>
    /// <param name="message">The <see cref="MessageType.AskContext"/> message.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="MessageType.PostContext"/> message as the response.</returns>
    /// <exception cref="IOException">Throws when the pipe is closed by the other side.</exception>
    public async Task<PostContextMessage> AskContext(AskContextMessage message)
    {
        // Send the request message to the shell.
        SendMessage(message);

        // Receiving response from the shell.
        var response = await GetMessageAsync(CancellationToken.None);
        if (response is not PostContextMessage postContext)
        {
            // Log: unexpected message. drop connection.
            _client.Close();
            string error = response is null
                ? "Pipe was closed or broken."
                : $"Expecting '{MessageType.PostContext}' response, but received '{response.Type}' message.";
            throw new IOException(error);
        }

        return postContext;
    }
}
