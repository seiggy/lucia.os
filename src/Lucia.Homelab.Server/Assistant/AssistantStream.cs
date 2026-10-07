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
    string? ToolName = null, JsonNode? Input = null, JsonNode? Output = null, string? ErrorText = null, JsonObject? Approval = null);

public sealed record UiMessage(string Id, string Role, List<UiPart> Parts, JsonObject? Metadata = null);

/// <summary>
/// Maps one Copilot turn to AI SDK UI message stream chunks, and builds the finished message for the transcript.
/// Not thread-safe: the run and the approval broker both write, so callers hold the stream's lock while mapping and publishing.
/// </summary>
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
    private bool _stopped, _ended;

    public UiMessage Message => new(messageId, "assistant", [.. _parts], Metadata());

    /// <summary>The turn was stopped or failed, so its message is kept even when empty.</summary>
    public bool Interrupted => _stopped || _failure is not null;

    public bool Stopped => _stopped;
    public string? Failure => _failure;

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
                    Tool(chunks, call.CallId, call.Name, JsonSerializer.SerializeToNode(call.Arguments ?? new Dictionary<string, object?>(), Json));
                    break;
                case ToolExecutionCompleteEvent e when content is FunctionResultContent result:
                    var index = _parts.FindIndex(part => part.ToolCallId == e.Data.ToolCallId);
                    // A refused call already ended as denied; the runtime's own failure for it adds nothing.
                    if (index < 0 || _parts[index].State == "output-denied") break;
                    if (e.Data.Success)
                    {
                        var output = Output(result.Result);
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

    /// <summary>
    /// Shows the owner a tool call to approve, or records a decision the policy made itself (<paramref name="automatic"/>).
    /// The runtime may ask before it reports the call started, so the tool part is added here when it is missing.
    /// </summary>
    public List<object> Ask(string toolCallId, string toolName, JsonNode? input, string approvalId, string? reason, bool automatic)
    {
        var chunks = new List<object>();
        if (_ended) return chunks;
        var index = Tool(chunks, toolCallId, toolName, input);
        var approval = new JsonObject { ["id"] = approvalId };
        if (reason is not null) approval["requestReason"] = reason;
        if (automatic) approval["isAutomatic"] = true;
        _parts[index] = _parts[index] with { State = "approval-requested", Approval = approval };
        chunks.Add(new { type = "tool-approval-request", approvalId, toolCallId, reason, isAutomatic = automatic ? true : (bool?)null });
        return chunks;
    }

    /// <summary>Records the answer to a waiting approval; a refusal ends the call as denied. Empty when nothing waits on it.</summary>
    public List<object> Answer(string approvalId, bool approved, string? reason)
    {
        var chunks = new List<object>();
        var index = _parts.FindIndex(part => part.State == "approval-requested" && (string?)part.Approval?["id"] == approvalId);
        if (index < 0) return chunks;
        var approval = (JsonObject)_parts[index].Approval!.DeepClone();
        approval["approved"] = approved;
        if (reason is not null) approval["reason"] = reason;
        _parts[index] = _parts[index] with { State = approved ? "approval-responded" : "output-denied", Approval = approval };
        chunks.Add(new { type = "tool-approval-response", approvalId, approved, reason });
        if (!approved) chunks.Add(new { type = "tool-output-denied", toolCallId = _parts[index].ToolCallId });
        return chunks;
    }

    /// <summary>Ends the turn: unanswered approvals are refused and calls still running are marked unfinished, so a saved chat never shows them waiting.</summary>
    private List<object> End(params object[] end)
    {
        var chunks = new List<object>();
        Close(chunks);
        _ended = true;
        foreach (var part in _parts.Where(part => part.State == "approval-requested").ToList())
            chunks.AddRange(Answer((string)part.Approval!["id"]!, false, "The run stopped before you answered."));
        for (var i = 0; i < _parts.Count; i++)
        {
            if (_parts[i].State is not ("input-available" or "approval-responded")) continue;
            const string errorText = "The run stopped before this finished.";
            _parts[i] = _parts[i] with { State = "output-error", ErrorText = errorText };
            chunks.Add(new { type = "tool-output-error", toolCallId = _parts[i].ToolCallId, errorText, dynamic = true });
        }
        chunks.AddRange(end);
        return chunks;
    }

    /// <summary>The call's dynamic-tool part, added once whichever of the start event and the permission request comes first.</summary>
    private int Tool(List<object> chunks, string toolCallId, string toolName, JsonNode? input)
    {
        var index = _parts.FindIndex(part => part.ToolCallId == toolCallId);
        if (index >= 0) return index;
        input = AssistantTools.Mask(input);
        Close(chunks);
        _parts.Add(new UiPart("dynamic-tool", State: "input-available", ToolCallId: toolCallId, ToolName: toolName, Input: input));
        chunks.Add(new { type = "tool-input-available", toolCallId, toolName, input, dynamic = true });
        return _parts.Count - 1;
    }

    /// <summary>Lucia's tools answer in JSON text; parse it so the chat shows structure instead of an escaped string.</summary>
    private static JsonNode? Output(object? result)
    {
        if (result is string { Length: > 0 } text && text[0] is '{' or '[')
        {
            try { return JsonNode.Parse(text); }
            catch (JsonException) { }
        }
        return JsonSerializer.SerializeToNode(result, Json);
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
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _done;

    /// <summary>Id of the assistant message this turn produces.</summary>
    public string MessageId => messageId;
    public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(stopping);
    public Task Completion { get; set; } = Task.CompletedTask;
    public bool Done => _done;

    /// <summary>Completes when the turn has ended and its chat is saved; <see cref="Completion"/> lingers for reconnects.</summary>
    public Task Ended => _ended.Task;

    /// <summary>"succeeded", "stopped" or "failed", once the turn has ended.</summary>
    public string Outcome { get; set; } = "succeeded";
    public string? Error { get; set; }

    public void Publish(object chunk) => Add(JsonSerializer.Serialize(chunk, AssistantStream.Json), false);

    public void Publish(List<object> chunks) => chunks.ForEach(Publish);

    public void Complete()
    {
        Add("[DONE]", true);
        _ended.TrySetResult();
    }

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
