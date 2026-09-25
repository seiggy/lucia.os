using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using TensorSharp.Runtime;
using TensorSharp.Server;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;
using TensorMessage = TensorSharp.Runtime.ChatMessage;

namespace Lucia.Homelab.Server.Host;

public sealed class TensorSharpChatClient(InferenceRuntime runtime, IOptions<HostPlatformOptions> options) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<AiMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<AiMessage> messages, ChatOptions? chatOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (chatOptions?.ResponseFormat is not null)
            throw new NotSupportedException("Use the OpenAI endpoint for structured-output requests.");
        if (chatOptions?.ToolMode is { } mode && !Equals(mode, ChatToolMode.Auto) && !Equals(mode, ChatToolMode.None))
            throw new NotSupportedException("The in-process SRE adapter supports automatic or disabled tool selection.");
        if (chatOptions?.FrequencyPenalty is not null || chatOptions?.PresencePenalty is not null)
            throw new NotSupportedException("The SRE adapter does not support frequency or presence penalties.");

        var history = ConvertMessages(messages, chatOptions?.Instructions);
        var tools = Equals(chatOptions?.ToolMode, ChatToolMode.None) ? [] : ConvertTools(chatOptions?.Tools);
        await runtime.Gate.WaitAsync(cancellationToken);
        try
        {
            runtime.RequireChatModel(chatOptions?.ModelId);
            var modelId = runtime.ChatModelName;
            var responseId = $"sre_{Guid.NewGuid():N}";
            var messageId = $"msg_{Guid.NewGuid():N}";
            var parser = OutputParserFactory.Create(runtime.Chat.Architecture);
            parser.Init(options.Value.SreThinking, tools);
            var sampling = new SamplingConfig
            {
                Temperature = chatOptions?.Temperature ?? 0.2f,
                TopP = chatOptions?.TopP ?? 0.95f,
                TopK = chatOptions?.TopK ?? 40,
                Seed = chatOptions?.Seed is { } seed ? checked((int)seed) : -1,
                StopSequences = chatOptions?.StopSequences?.ToList()
            };
            var maxTokens = chatOptions?.MaxOutputTokens ?? options.Value.MaxOutputTokens;
            if (maxTokens < 1 || maxTokens > options.Value.MaxOutputTokens)
                throw new ArgumentOutOfRangeException(nameof(chatOptions), "Output token limit exceeds the configured host limit.");
            var pendingCalls = new List<FunctionCallContent>();
            bool completed = false;
            bool parserPrimed = false;
            await foreach (var update in runtime.Chat.ChatStreamWithMetricsAsync(
                history, maxTokens, cancellationToken, sampling, tools, options.Value.SreThinking))
            {
                if (!parserPrimed)
                {
                    if (update.RawGenerationSuffix is not null)
                        parser.SetGenerationPromptSuffix(update.RawGenerationSuffix);
                    parserPrimed = update.RawGenerationSuffix is not null || update.Piece.Length > 0 || update.Done;
                }
                if (update.Done && update.FinishReason is "error" or "aborted" or "cancelled")
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new InvalidOperationException($"TensorSharp generation ended with {update.FinishReason}.");
                }
                var parsed = parser.Add(update.Piece, update.Done);
                var contents = new List<AIContent>();
                if (!string.IsNullOrEmpty(parsed.Content))
                    contents.Add(new TextContent(parsed.Content));
                foreach (var call in parsed.ToolCalls ?? [])
                {
                    if (!tools.Any(tool => tool.Name == call.Name))
                        throw new InvalidDataException($"Model requested an undeclared tool: {call.Name}.");
                    pendingCalls.Add(new FunctionCallContent(call.Id ?? $"call_{Guid.NewGuid():N}", call.Name, call.Arguments));
                }
                if (contents.Count > 0)
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, contents)
                    {
                        ResponseId = responseId,
                        MessageId = messageId,
                        ModelId = modelId
                    };
                }
                if (update.Done)
                {
                    completed = true;
                    var completion = CompleteGeneration(update, pendingCalls);
                    completion.ResponseId = responseId;
                    completion.MessageId = messageId;
                    completion.ModelId = modelId;
                    yield return completion;
                }
            }
            if (!completed)
                throw new InvalidDataException("TensorSharp ended the stream without completion metadata.");
        }
        finally { runtime.Gate.Release(); }
    }

    public static ChatResponseUpdate CompleteGeneration(ChatStreamUpdate update, IReadOnlyList<FunctionCallContent> calls)
    {
        if (!update.Done)
            throw new ArgumentException("A terminal generation update is required.", nameof(update));
        var truncated = update.FinishReason?.ToLowerInvariant() is "max_tokens" or "thinking_budget" or "repetition";
        // Tool invocation must wait until the complete generation is known not to be truncated.
        var contents = truncated ? new List<AIContent>() : calls.Cast<AIContent>().ToList();
        contents.Add(new UsageContent(new UsageDetails
        {
            InputTokenCount = update.PromptTokens,
            OutputTokenCount = update.EvalTokens,
            TotalTokenCount = (long)update.PromptTokens + update.EvalTokens
        }));
        return new ChatResponseUpdate(ChatRole.Assistant, contents)
        {
            FinishReason = truncated ? ChatFinishReason.Length : calls.Count > 0 ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop
        };
    }

    public static List<TensorMessage> ConvertMessages(IEnumerable<AiMessage> messages, string? instructions = null)
    {
        var history = new List<TensorMessage>();
        if (!string.IsNullOrWhiteSpace(instructions))
            history.Add(new TensorMessage { Role = "system", Content = instructions });
        foreach (var message in messages)
        {
            if (message.Role != ChatRole.System && message.Role != ChatRole.User
                && message.Role != ChatRole.Assistant && message.Role != ChatRole.Tool
                && message.Role.Value != "developer")
                throw new NotSupportedException($"Unsupported message role: {message.Role}.");
            if (message.Contents.Any(content => content is not (TextContent or FunctionCallContent or FunctionResultContent)))
                throw new NotSupportedException("The SRE agent currently accepts text and function calls/results only.");
            var results = message.Contents.OfType<FunctionResultContent>().ToArray();
            if (results.Length > 0)
            {
                if (message.Role != ChatRole.Tool || results.Length != message.Contents.Count)
                    throw new ArgumentException("Tool results must be separate tool-role messages.");
                foreach (var result in results)
                    history.Add(new TensorMessage
                    {
                        Role = "tool",
                        ToolCallId = result.CallId,
                        Content = result.Result is string text ? text : JsonSerializer.Serialize(result.Result)
                    });
                continue;
            }
            var calls = message.Contents.OfType<FunctionCallContent>().ToArray();
            if (calls.Length > 0 && message.Role != ChatRole.Assistant)
                throw new ArgumentException("Function calls must belong to an assistant message.");
            history.Add(new TensorMessage
            {
                Role = message.Role.Value,
                Content = message.Text,
                ToolCalls = calls.Length == 0 ? null : calls.Select(call => new ToolCall
                {
                    Id = call.CallId,
                    Name = call.Name,
                    Arguments = call.Arguments is null ? [] : new(call.Arguments)
                }).ToList()
            });
        }
        return history;
    }

    public static List<ToolFunction> ConvertTools(IList<AITool>? tools)
    {
        if (tools is null || tools.Count == 0)
            return [];
        if (tools.Any(tool => tool is not AIFunctionDeclaration))
            throw new NotSupportedException("Only function tools are supported by the SRE adapter.");
        return ToolFunction.ParseList(JsonSerializer.Serialize(tools.Cast<AIFunctionDeclaration>().Select(tool => new
        {
            name = tool.Name,
            description = tool.Description,
            parameters = tool.JsonSchema
        })));
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
            return null;
        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    // The host owns the shared runtime, not this client adapter.
    public void Dispose() { }
}
