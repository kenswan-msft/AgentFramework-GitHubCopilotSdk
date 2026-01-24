using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

namespace AgentFrameworkGitHubCopilot;

public class CopilotSdkChatClient(
    string? modelId = "gpt-5",
    CopilotClientOptions? clientOptions = null,
    SessionConfig? sessionConfig = null)
    : IChatClient
{
    private readonly CopilotClient client = new(clientOptions);

    private readonly SessionConfig sessionConfig = sessionConfig
                                                   ?? new SessionConfig
                                                   {
                                                       Model = modelId ?? "gpt-5"
                                                   };

    private readonly SemaphoreSlim sessionLock = new(1, 1);
    private CopilotSession? session;
    private bool disposed;

    public ChatClientMetadata Metadata { get; } = new("CopilotChatClient", null, modelId);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        CopilotSession nonStreamingSession =
            await GetOrCreateSessionAsync(options, streaming: false, cancellationToken)
                .ConfigureAwait(false);

        // Convert messages to a single prompt (Copilot SDK takes text prompts)
        string? prompt = ConvertMessagesToPrompt(messages);

        if (string.IsNullOrEmpty(prompt))
        {
            return new ChatResponse([])
            {
                ModelId = sessionConfig.Model
            };
        }

        var done = new TaskCompletionSource<string>();
        string responseContent = "";

        IDisposable subscription = nonStreamingSession.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageEvent msg:
                    responseContent = msg.Data.Content;
                    break;

                case SessionIdleEvent:
                    done.TrySetResult(responseContent);
                    break;

                case SessionErrorEvent err:
                    done.TrySetException(new InvalidOperationException(err.Data.Message));
                    break;
            }
        });

        try
        {
            await nonStreamingSession.SendAsync(
                new MessageOptions
                {
                    Prompt = prompt
                },
                cancellationToken).ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(5)); // Timeout

            string response = await done.Task.WaitAsync(cts.Token).ConfigureAwait(false);

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, response))
            {
                ModelId = sessionConfig.Model,
                CreatedAt = DateTimeOffset.UtcNow
            };
        }
        finally
        {
            subscription.Dispose();
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // Create a new session with streaming enabled for each streaming request
        CopilotSession streamingSession = await client.CreateSessionAsync(
            new SessionConfig
            {
                Model = options?.ModelId ?? sessionConfig.Model,
                Streaming = true,
                Tools = sessionConfig.Tools,
                SystemMessage = sessionConfig.SystemMessage
            },
            cancellationToken).ConfigureAwait(false);

        string? prompt = ConvertMessagesToPrompt(messages);

        if (string.IsNullOrEmpty(prompt))
        {
            yield break;
        }

        var channel = System.Threading.Channels.Channel.CreateUnbounded<ChatResponseUpdate>();
        string? responseId = Guid.NewGuid().ToString("N");

        IDisposable? subscription = streamingSession.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageDeltaEvent delta:
                    channel.Writer.TryWrite(
                        new ChatResponseUpdate(ChatRole.Assistant, delta.Data.DeltaContent)
                        {
                            Role = ChatRole.Assistant,
                            // Text = delta.Data.DeltaContent,
                            ModelId = sessionConfig.Model,
                            ResponseId = responseId,
                            CreatedAt = DateTimeOffset.UtcNow
                        });
                    break;

                case SessionIdleEvent:
                    channel.Writer.Complete();
                    break;

                case SessionErrorEvent err:
                    channel.Writer.Complete(new InvalidOperationException(err.Data.Message));
                    break;
            }
        });

        try
        {
            await streamingSession.SendAsync(
                new MessageOptions
                {
                    Prompt = prompt
                },
                cancellationToken).ConfigureAwait(false);

            await foreach (ChatResponseUpdate update in
                           channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }
        }
        finally
        {
            subscription.Dispose();
            await streamingSession.DisposeAsync().ConfigureAwait(false);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is null)
        {
            if (serviceType == typeof(ChatClientMetadata))
            {
                return Metadata;
            }

            if (serviceType.IsInstanceOfType(this))
            {
                return this;
            }

            if (serviceType == typeof(CopilotClient))
            {
                return client;
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        sessionLock.Dispose();

        GC.SuppressFinalize(this);
    }

    private async Task<CopilotSession> GetOrCreateSessionAsync(
        ChatOptions? options,
        bool streaming,
        CancellationToken cancellationToken)
    {
        await sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session == null)
            {
                await client.StartAsync(cancellationToken).ConfigureAwait(false);

                session = await client.CreateSessionAsync(
                    new SessionConfig
                    {
                        Model = options?.ModelId ?? sessionConfig.Model,
                        Streaming = streaming,
                        Tools = sessionConfig.Tools,
                        SystemMessage = sessionConfig.SystemMessage
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            return session;
        }
        finally
        {
            sessionLock.Release();
        }
    }

    private static string ConvertMessagesToPrompt(IEnumerable<ChatMessage> messages)
    {
        // TODO: Combining messages here, but can leverage session ID for chat history integration later
        IEnumerable<string> textMessages = messages
            .Select(m => $"{m.Role}: {m.Text}");

        return string.Join("\n", textMessages);
    }
}
