using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AgentFrameworkGitHubCopilot;

public class CopilotSdkChatClient(
    string? modelId = "gpt-4.1",
    CopilotClientOptions? clientOptions = null,
    SessionConfig? sessionConfig = null,
    ILogger<CopilotSdkChatClient>? logger = null)
    : IChatClient, IAsyncDisposable
{
    private readonly CopilotClient client = new(clientOptions);

    private readonly SessionConfig sessionConfig = sessionConfig
                                                   ?? new SessionConfig
                                                   {
                                                       Model = modelId ?? "gpt-4.1",
                                                       OnPermissionRequest = PermissionHandler.ApproveAll
                                                   };

    private bool started;
    private readonly SemaphoreSlim startLock = new(1, 1);
    private bool disposed;

    public ChatClientMetadata Metadata { get; } = new("CopilotChatClient", null, modelId);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        (SystemMessageConfig? systemMessage, string? prompt) = ExtractSystemAndPrompt(messages, options);

        string requestModel = options?.ModelId ?? sessionConfig.Model;

        // Create a per-request session to avoid leaking conversation context between callers
        CopilotSession requestSession = await client.CreateSessionAsync(
            new SessionConfig
            {
                Model = requestModel,
                Streaming = false,
                Tools = MergeTools(sessionConfig.Tools, options?.Tools),
                SystemMessage = systemMessage ?? sessionConfig.SystemMessage,
                OnPermissionRequest = sessionConfig.OnPermissionRequest
            },
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(prompt))
        {
            await requestSession.DisposeAsync().ConfigureAwait(false);

            return new ChatResponse([])
            {
                ModelId = requestModel
            };
        }

        var done = new TaskCompletionSource<string>();
        string responseContent = "";

        IDisposable subscription = requestSession.On(evt =>
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
            await requestSession.SendAsync(
                new MessageOptions
                {
                    Prompt = prompt
                },
                cancellationToken).ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(5)); // Timeout

            string response = await done.Task.WaitAsync(cts.Token).ConfigureAwait(false);

            // Simulate structured output: normalize the response JSON to match the requested schema.
            if (options?.ResponseFormat is ChatResponseFormatJson { Schema: JsonElement schema })
            {
                logger?.LogDebug("Copilot raw response: {RawResponse}", response);

                string? normalized = CopilotSdkJsonResponseNormalizer.TryNormalize(response, schema);

                if (normalized is not null)
                {
                    logger?.LogDebug("Copilot normalized response: {NormalizedResponse}", normalized);
                    response = normalized;
                }
                else
                {
                    logger?.LogWarning("Copilot response normalization failed; using raw response.");
                }
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, response))
            {
                ModelId = requestModel,
                CreatedAt = DateTimeOffset.UtcNow
            };
        }
        finally
        {
            subscription.Dispose();
            await requestSession.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        (SystemMessageConfig? systemMessage, string? prompt) = ExtractSystemAndPrompt(messages, options);

        string requestModel = options?.ModelId ?? sessionConfig.Model;

        // Create a per-request session with streaming enabled
        CopilotSession streamingSession = await client.CreateSessionAsync(
            new SessionConfig
            {
                Model = requestModel,
                Streaming = true,
                Tools = MergeTools(sessionConfig.Tools, options?.Tools),
                SystemMessage = systemMessage ?? sessionConfig.SystemMessage,
                OnPermissionRequest = sessionConfig.OnPermissionRequest
            },
            cancellationToken).ConfigureAwait(false);

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
                            ModelId = requestModel,
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
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        await client.DisposeAsync().ConfigureAwait(false);
        startLock.Dispose();

        GC.SuppressFinalize(this);
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (started)
        {
            return;
        }

        await startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!started)
            {
                await client.StartAsync(cancellationToken).ConfigureAwait(false);
                started = true;
            }
        }
        finally
        {
            startLock.Release();
        }
    }

    /// <summary>
    /// Merges constructor-level tools with per-request tools from <see cref="ChatOptions.Tools"/>.
    /// The Copilot SDK handles tool execution internally via <see cref="AIFunction.InvokeAsync"/>,
    /// so we just need to register them on the session.
    /// </summary>
    private static ICollection<AIFunction>? MergeTools(
        ICollection<AIFunction>? sessionTools,
        IList<AITool>? requestTools)
    {
        var requestFunctions = requestTools?
            .OfType<AIFunction>()
            .ToList();

        if (requestFunctions is null or { Count: 0 })
        {
            return sessionTools;
        }

        if (sessionTools is null or { Count: 0 })
        {
            return requestFunctions;
        }

        return [.. sessionTools, .. requestFunctions];
    }

    private static (SystemMessageConfig? SystemMessage, string? Prompt) ExtractSystemAndPrompt(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options)
    {
        List<string> systemParts = [];
        List<string> promptParts = [];

        foreach (ChatMessage message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                systemParts.Add(message.Text ?? string.Empty);
            }
            else
            {
                string role = message.Role.Value;
                string content = message.Text ?? string.Empty;
                promptParts.Add($"{role}: {content}");
            }
        }

        // When the caller requests JSON output (e.g., RunAsync<T>), reinforce it
        // in the system message since the Copilot SDK does not support ResponseFormat.
        if (options?.ResponseFormat is ChatResponseFormatJson)
        {
            systemParts.Add("You MUST respond with ONLY valid JSON. No markdown fences, no explanation, no extra text.");
        }

        SystemMessageConfig? systemMessage = systemParts.Count > 0
            ? new SystemMessageConfig { Content = string.Join("\n\n", systemParts), Mode = SystemMessageMode.Replace }
            : null;

        string? prompt = promptParts.Count > 0 ? string.Join("\n", promptParts) : null;

        return (systemMessage, prompt);
    }
}