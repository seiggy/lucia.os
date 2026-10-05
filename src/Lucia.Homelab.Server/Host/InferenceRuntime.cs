using System.Globalization;
using Microsoft.Extensions.Options;
using TensorSharp.AgentHost.Skills;
using TensorSharp.Models.Embeddings;
using TensorSharp.Runtime;
using TensorSharp.Runtime.Scheduling;
using TensorSharp.Server;
using TensorSharp.Server.Hosting;
using TensorSharp.Server.ProtocolAdapters;
using TensorSharp.Server.Responses;

namespace Lucia.Homelab.Server.Host;

public sealed class InferenceRuntime : IAsyncDisposable
{
    private readonly HostPlatformOptions _options;
    private readonly ModelCatalog _catalog;
    private readonly ModelInspector _inspector;
    private readonly ILogger<InferenceRuntime> _logger;
    private readonly ServerHostingOptions _chatOptions;
    private readonly InMemoryResponsesStore _responsesStore = new();
    private readonly string? _previousHeadroom;
    private EmbeddingModel? _embedding;
    private EmbeddingAdapter? _embeddingAdapter;
    private Guid? _chatId;
    private Guid? _embeddingId;
    private ContextPlan? _chatPlan;
    private ContextPlan? _embeddingPlan;

    // ponytail: serialize inference and model swaps; use read/write leases if concurrent batching is needed.
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public ModelService Chat { get; }
    public OpenAIChatAdapter ChatAdapter { get; }
    public OpenAIResponsesAdapter ResponsesAdapter { get; }
    public bool HasEmbeddingModel => _embedding is not null;
    public string? StartupError { get; set; }
    /// <summary>Why loading is refused right now, such as another model holding the memory.</summary>
    public string? Paused { get; set; }
    public string? ChatModelName => Chat.IsLoaded ? Path.GetFileNameWithoutExtension(Chat.LoadedModelPath) : null;

    public InferenceRuntime(ModelCatalog catalog, ModelInspector inspector, IOptions<HostPlatformOptions> options,
        IHostEnvironment environment, ILoggerFactory loggerFactory, ILogger<InferenceRuntime> logger)
    {
        _catalog = catalog;
        _inspector = inspector;
        _options = options.Value;
        _logger = logger;
        _previousHeadroom = Environment.GetEnvironmentVariable("TS_VRAM_HEADROOM_MB");
        var minimumHeadroom = checked((long)((_options.OsReserveGiB + _options.ServicesReserveGiB
            + _options.VoiceReserveGiB + _options.RuntimeReserveGiB) * 1024));
        var headroom = long.TryParse(_previousHeadroom, CultureInfo.InvariantCulture, out var previous)
            ? Math.Max(previous, minimumHeadroom) : minimumHeadroom;
        Environment.SetEnvironmentVariable("TS_VRAM_HEADROOM_MB", headroom.ToString(CultureInfo.InvariantCulture));
        var root = Path.GetFullPath(_options.ModelDirectory, environment.ContentRootPath);
        _chatOptions = CreateOptions(Path.Combine(root, ".uploads"));
        Chat = new ModelService(loggerFactory.CreateLogger<ModelService>());
        InferenceMetrics.LoadedModel = () => ChatModelName;
        var queue = new InferenceQueue();
        var uploads = new UploadStoragePolicy(_chatOptions.UploadDirectory, maxFileBytes: 0, quotaBytes: 1);
        var skills = new SkillRegistry(new SkillRegistryOptions(), loggerFactory.CreateLogger<SkillRegistry>());
        ChatAdapter = new OpenAIChatAdapter(Chat, queue, _chatOptions, uploads, skills, null, null!, loggerFactory);
        ResponsesAdapter = new OpenAIResponsesAdapter(Chat, queue, _chatOptions, uploads, skills, null, null!,
            loggerFactory, _responsesStore);
    }

    private ServerHostingOptions CreateOptions(string uploads, string? embeddingPath = null) => new(
        startupModelPath: embeddingPath, startupMmProjPath: null,
        defaultBackend: _options.Backend, supportedBackends: [],
        defaultMaxTokens: _options.MaxOutputTokens, maxTokensPinned: true,
        defaultVideoFrames: 1, defaultVideoFps: 1, defaultVideoWidth: 1, defaultVideoHeight: 1,
        defaultVideoSteps: 1, defaultVideoMode: null, uploadDirectory: uploads, logDirectory: uploads,
        fileLoggingEnabled: false, samplingDefaults: new SamplingDefaults(new SamplingConfig()),
        uploadMaxFileBytes: 0, webUiEnabled: false, skillsEnabled: false,
        skillsDiscovery: false, skillsAllowScripts: false, skillsAllowNetwork: false,
        prefixCacheEnabled: false, embeddingsEnabled: embeddingPath is not null);

    public object Status() => new
    {
        backend = _options.Backend,
        chat = _chatId is { } chat ? new { id = chat, name = ChatModelName, plan = _chatPlan } : null,
        embedding = _embeddingId is { } embedding ? new { id = embedding, name = _embedding?.ModelName, backend = _embedding?.Backend, plan = _embeddingPlan } : null,
        voiceReserveGiB = _options.VoiceReserveGiB,
        startupError = StartupError,
        paused = Paused
    };

    public object OpenAIModels()
    {
        var data = new List<object>();
        if (ChatModelName is { } chat)
            data.Add(new
            {
                id = chat, @object = "model", created = 0, owned_by = "local",
                capabilities = new[] { "chat", "responses" }, backend = Chat.LoadedBackend,
                context_length = Chat.ContextTokens, native_context_length = Chat.ModelContextTokens,
                max_output_tokens = _options.MaxOutputTokens
            });
        if (_embedding is { } embedding)
            data.Add(new { id = embedding.ModelName, @object = "model", created = 0, owned_by = "local",
                capabilities = new[] { "embedding" }, backend = embedding.Backend,
                context_length = embedding.MaxTokens, embedding_dimensions = embedding.Dimensions });
        return new { @object = "list", data };
    }

    public ContextPlan Inspect(Guid id, int? contextTokens = null)
    {
        var model = _catalog.Find(id) ?? throw new KeyNotFoundException("Model not found.");
        var inspection = ModelInspector.Inspect(_catalog.ModelPath(id), model.Source.Kind);
        return _inspector.Plan(inspection, OtherResidentBytes(model.Source.Kind), contextTokens);
    }

    public ContextPlan PreviewContext(ModelInspection inspection) =>
        _inspector.Plan(inspection, OtherResidentBytes(inspection.Kind));

    private long OtherResidentBytes(ModelKind kind)
    {
        var other = kind == ModelKind.Chat ? _embeddingPlan : _chatPlan;
        return other is null ? 0 : checked(other.ResidentWeightBytes
            + (long)other.EffectiveContextTokens * other.Model.KvBytesPerToken);
    }

    public async Task<ContextPlan> LoadAsync(Guid id, int? contextTokens, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (Paused is { } reason) throw new InvalidOperationException(reason);
            var model = _catalog.Find(id) ?? throw new KeyNotFoundException("Model not found.");
            var plan = Inspect(id, contextTokens);
            if (plan.EffectiveContextTokens < (model.Source.Kind == ModelKind.Chat ? 256 : 1))
                throw new InvalidOperationException("The model does not fit after voice, OS, services, and runtime reservations.");
            var path = _catalog.ModelPath(id);
            if (model.Source.Kind == ModelKind.Embedding)
            {
                await UnloadEmbeddingAsync();
                _embedding = await Task.Run(() => EmbeddingModel.Load(path, new EmbeddingModelOptions
                {
                    Backend = "CPU",
                    MaxTokens = plan.EffectiveContextTokens,
                    ModelName = Path.GetFileNameWithoutExtension(path)
                }), CancellationToken.None);
                _embeddingAdapter = new EmbeddingAdapter(_embedding, CreateOptions(_chatOptions.UploadDirectory, path));
                _embeddingId = id;
                _embeddingPlan = plan;
            }
            else
            {
                Chat.UnloadModel();
                _chatId = null;
                _chatPlan = null;
                _chatOptions.RepointHostedModel(null!, null!);
                Chat.SchedulerConfigOverride = new SchedulerConfig
                {
                    MaxNumRunningSequences = 1,
                    NumBlocks = Math.Max(1, plan.EffectiveContextTokens / 256),
                    BlockSize = 256,
                    MaxNumBatchedTokens = 512,
                    MaxPrefillChunkSize = 256,
                    SoloPrefillChunkSize = 512,
                    EnablePrefixCaching = false
                };
                var previousContext = Environment.GetEnvironmentVariable("MAX_CONTEXT");
                try
                {
                    Environment.SetEnvironmentVariable("MAX_CONTEXT", plan.EffectiveContextTokens.ToString(CultureInfo.InvariantCulture));
                    await Task.Run(() => Chat.LoadModel(path, null!, _options.Backend), CancellationToken.None);
                    if (!Chat.IsLoaded || Chat.ContextTokens > plan.EffectiveContextTokens)
                        throw new InvalidOperationException("TensorSharp did not honor the admitted model/context limit.");
                    _chatOptions.RepointHostedModel(path, null!);
                    _chatId = id;
                    _chatPlan = plan;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Loading chat model {ModelId} failed; clearing the failed model", id);
                    Chat.UnloadModel();
                    throw;
                }
                finally
                {
                    Environment.SetEnvironmentVariable("MAX_CONTEXT", previousContext);
                }
            }
            await _catalog.SaveSelectionAsync(model.Source.Kind, id, plan.EffectiveContextTokens);
            _logger.LogInformation("Loaded {Kind} model {ModelId} with {ContextTokens} context tokens", model.Source.Kind, id, plan.EffectiveContextTokens);
            StartupError = null;
            return plan;
        }
        finally { Gate.Release(); }
    }

    /// <param name="keepSelection">Pause only: keep the saved startup selection so it can be restored later.</param>
    public async Task UnloadAsync(ModelKind kind, CancellationToken cancellationToken, bool keepSelection = false)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (kind == ModelKind.Chat)
            {
                Chat.UnloadModel();
                _chatOptions.RepointHostedModel(null!, null!);
                _chatId = null;
                _chatPlan = null;
            }
            else if (kind == ModelKind.Embedding)
                await UnloadEmbeddingAsync();
            else
                throw new ArgumentException("Unknown model kind.", nameof(kind));
            if (!keepSelection)
                await _catalog.SaveSelectionAsync(kind, null, null);
        }
        finally { Gate.Release(); }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (id == _chatId || id == _embeddingId)
                throw new InvalidOperationException("Unload the model before deleting it.");
            await _catalog.DeleteAsync(id, cancellationToken);
        }
        finally { Gate.Release(); }
    }

    public void RequireChatModel(string? name = null)
    {
        if (!Chat.IsLoaded)
            throw new InvalidOperationException("No chat model is loaded.");
        if (name is not null && name != ChatModelName && name != Chat.LoadedModelName)
            throw new KeyNotFoundException("The requested model is not the loaded chat model. Query /v1/models.");
    }

    public Task EmbedAsync(HttpContext context) => _embeddingAdapter?.OpenAIAsync(context)
        ?? throw new InvalidOperationException("No embedding model is loaded.");

    private async ValueTask UnloadEmbeddingAsync()
    {
        if (_embeddingAdapter is not null)
            await _embeddingAdapter.DisposeAsync();
        _embedding?.Dispose();
        _embeddingAdapter = null;
        _embedding = null;
        _embeddingId = null;
        _embeddingPlan = null;
    }

    public async ValueTask DisposeAsync()
    {
        await Gate.WaitAsync();
        try
        {
            await UnloadEmbeddingAsync();
            Chat.Dispose();
            Environment.SetEnvironmentVariable("TS_VRAM_HEADROOM_MB", _previousHeadroom);
        }
        finally { Gate.Release(); }
        Gate.Dispose();
    }
}
