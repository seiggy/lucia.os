using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GitHub.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Lucia.Homelab.Server.Assistant;

/// <summary>An AI SDK <c>UIMessage</c> part: text, reasoning or dynamic-tool.</summary>
public sealed record UiPart(string Type, string? Text = null, string? State = null, string? ToolCallId = null,
    string? ToolName = null, JsonNode? Input = null, JsonNode? Output = null, string? ErrorText = null);

public sealed record UiMessage(string Id, string Role, List<UiPart> Parts, JsonObject? Metadata = null);

/// <summary>Maps one Copilot turn to AI SDK UI message stream chunks, and builds the finished message for the transcript.</summary>
public sealed class AssistantStream(string messageId, string? model)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly List<UiPart> _parts = [];
    private readonly StringBuilder _text = new();
    private readonly HashSet<string> _streamed = [];
    private string? _open, _openKey, _partId, _model = model, _error, _failure;
    private int _next;
    private long? _input, _output;
    private bool _stopped;

    public UiMessage Message => new(messageId, "assistant", [.. _parts], Metadata());

    /// <summary>The turn was stopped or failed, so its message is kept even when empty.</summary>
    public bool Interrupted => _stopped || _failure is not null;

    public object Start() => new { type = "start", messageId };

    public List<object> Map(AgentResponseUpdate update)
    {
        var chunks = new List<object>();
        foreach (var content in (update.RawRepresentation as AgentResponseUpdate ?? update).Contents)
        {
            switch (content.RawRepresentation)
            {
                case AssistantMessageDeltaEvent e:
                    Append(chunks, "text", e.Data.MessageId, e.Data.DeltaContent);
                    break;
                case AssistantMessageEvent e when !_streamed.Contains("text:" + e.Data.MessageId):
                    Append(chunks, "text", e.Data.MessageId, e.Data.Content);
                    break;
                case AssistantReasoningDeltaEvent e:
                    Append(chunks, "reasoning", e.Data.ReasoningId, e.Data.DeltaContent);
                    break;
                case AssistantReasoningEvent e when !_streamed.Contains("reasoning:" + e.Data.ReasoningId):
                    Append(chunks, "reasoning", e.Data.ReasoningId, e.Data.Content);
                    break;
                case ToolExecutionStartEvent when content is FunctionCallContent call:
                    Close(chunks);
                    var input = JsonSerializer.SerializeToNode(call.Arguments ?? new Dictionary<string, object?>(), Json);
                    _parts.Add(new UiPart("dynamic-tool", State: "input-available", ToolCallId: call.CallId, ToolName: call.Name, Input: input));
                    chunks.Add(new { type = "tool-input-available", toolCallId = call.CallId, toolName = call.Name, input, dynamic = true });
                    break;
                case ToolExecutionCompleteEvent e when content is FunctionResultContent result:
                    var index = _parts.FindIndex(part => part.ToolCallId == e.Data.ToolCallId);
                    if (index < 0) break;
                    if (e.Data.Success)
                    {
                        var output = JsonSerializer.SerializeToNode(result.Result, Json);
                        _parts[index] = _parts[index] with { State = "output-available", Output = output };
                        chunks.Add(new { type = "tool-output-available", toolCallId = e.Data.ToolCallId, output, dynamic = true });
                    }
                    else
                    {
                        var errorText = result.Result?.ToString() ?? "The tool failed.";
                        _parts[index] = _parts[index] with { State = "output-error", ErrorText = errorText };
                        chunks.Add(new { type = "tool-output-error", toolCallId = e.Data.ToolCallId, errorText, dynamic = true });
                    }
                    break;
                case AssistantUsageEvent e:
                    _model = e.Data.Model;
                    _input = (_input ?? 0) + (e.Data.InputTokens ?? 0);
                    _output = (_output ?? 0) + (e.Data.OutputTokens ?? 0);
                    break;
                case SessionErrorEvent e:
                    _error = e.Data.Message.Length > 300 ? e.Data.Message[..300] + "…" : e.Data.Message;
                    break;
            }
        }
        return chunks;
    }

    public List<object> Finish() => End(new { type = "finish", finishReason = "stop", messageMetadata = Metadata() });

    /// <summary>Prefers the runtime's own session error, which is more specific than the exception that ends the run.</summary>
    public List<object> Fail(string? message)
    {
        _failure = _error ?? message ?? "The assistant stopped unexpectedly.";
        return End(new { type = "message-metadata", messageMetadata = Metadata() }, new { type = "error", errorText = _failure });
    }

    public List<object> Abort()
    {
        _stopped = true;
        return End(new { type = "message-metadata", messageMetadata = Metadata() }, new { type = "abort" });
    }

    private List<object> End(params object[] end)
    {
        var chunks = new List<object>();
        Close(chunks);
        chunks.AddRange(end);
        return chunks;
    }

    private void Append(List<object> chunks, string kind, string key, string delta)
    {
        if (delta.Length == 0) return;
        _streamed.Add(kind + ":" + key);
        if (_open != kind || _openKey != key)
        {
            Close(chunks);
            (_open, _openKey, _partId) = (kind, key, $"{kind[0]}{_next++}");
            chunks.Add(new { type = kind + "-start", id = _partId });
        }
        _text.Append(delta);
        chunks.Add(new { type = kind + "-delta", id = _partId, delta });
    }

    private void Close(List<object> chunks)
    {
        if (_open is null) return;
        chunks.Add(new { type = _open + "-end", id = _partId });
        _parts.Add(new UiPart(_open, _text.ToString(), "done"));
        _text.Clear();
        _open = _openKey = null;
    }

    private JsonObject? Metadata()
    {
        var metadata = new JsonObject();
        if (_model is not null) metadata["model"] = _model;
        if (_input is not null) metadata["usage"] = new JsonObject { ["inputTokens"] = _input, ["outputTokens"] = _output };
        if (_stopped) metadata["stopped"] = true;
        if (_failure is not null) metadata["error"] = _failure;
        return metadata.Count == 0 ? null : metadata;
    }
}

/// <summary>One turn's serialized chunks, kept so a reopened chat bar can replay and follow it.</summary>
public sealed class AssistantRun(string messageId, CancellationToken stopping)
{
    private readonly List<string> _chunks = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _done;

    /// <summary>Id of the assistant message this turn produces.</summary>
    public string MessageId => messageId;
    public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(stopping);
    public Task Completion { get; set; } = Task.CompletedTask;
    public bool Done => _done;

    public void Publish(object chunk) => Add(JsonSerializer.Serialize(chunk, AssistantStream.Json), false);

    public void Publish(List<object> chunks) => chunks.ForEach(Publish);

    public void Complete() => Add("[DONE]", true);

    /// <summary>Chunks from <paramref name="from"/> on, whether the turn has ended, and a task that completes on the next change.</summary>
    public (List<string> Chunks, bool Done, Task Changed) Read(int from)
    {
        lock (_chunks) return (_chunks.GetRange(from, _chunks.Count - from), _done, _changed.Task);
    }

    private void Add(string data, bool done)
    {
        TaskCompletionSource changed;
        lock (_chunks)
        {
            if (_done) return;
            _chunks.Add(data);
            _done = done;
            changed = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        changed.TrySetResult();
    }
}
