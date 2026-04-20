using Execor.Core;
using Execor.Models;
using LLama;
using LLama.Common;
using LLama.Native;

namespace Execor.Inference.Services;

public class LlamaService : IChatService
{
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private InteractiveExecutor? _executor;
    private bool _isFirstPrompt = true;

    private readonly IModelManager _modelManager;

    private readonly string ProfilePath =
        Path.Combine(AppContext.BaseDirectory, "config", "hardware-profile.json");

    public LlamaService(IModelManager modelManager)
    {
        NativeLibraryConfig.All.WithCuda();
        _modelManager = modelManager;
        Console.WriteLine("GPU Offload Supported: " + NativeApi.llama_supports_gpu_offload());
        Console.WriteLine("CUDA Device Count: " + NativeApi.llama_max_devices());
    }

    public void LoadActiveModel()
    {
        _isFirstPrompt = true;
        var activeModel = _modelManager.GetActiveModel();
       
        if (activeModel == null)
            throw new Exception("No active model selected.");

        var profile = LoadOrBenchmarkHardware(activeModel.FilePath);

        DisposeCurrentModel();

        bool gpuAvailable = NativeApi.llama_supports_gpu_offload();

        ulong totalRamGB = GetSystemMemoryGB();
        ulong vramMB = GetApproxGpuMemoryMB();

        int gpuLayers = CalculateOptimalGpuLayers(vramMB, gpuAvailable, activeModel.FilePath);
        int batchSize = CalculateOptimalBatchSize(totalRamGB, gpuAvailable, vramMB);
        int contextSize = CalculateOptimalContextSize(totalRamGB);

        Console.WriteLine("========== EXECOR AUTO CONFIG ==========");
        Console.WriteLine($"GPU Available: {gpuAvailable}");
        Console.WriteLine($"Detected VRAM: {vramMB} MB");
        Console.WriteLine($"System RAM: {totalRamGB} GB");
        Console.WriteLine($"GPU Layers: {gpuLayers}");
        Console.WriteLine($"Batch Size: {batchSize}");
        Console.WriteLine($"Context Size: {contextSize}");
        Console.WriteLine("========================================");

        var parameters = new ModelParams(activeModel.FilePath)
        {
            ContextSize = (uint)profile.ContextSize,
            GpuLayerCount = profile.GpuLayers,
            BatchSize = (uint)profile.BatchSize,
            FlashAttention = profile.FlashAttention,
            UseMemorymap = true,
            UseMemoryLock = false,
            Threads = Math.Max(4, Environment.ProcessorCount / 2),
        };

        try
        {
            _weights = LLamaWeights.LoadFromFile(parameters);
        }
        catch
        {
            Console.WriteLine("Retrying with safer GPU settings...");

            parameters.GpuLayerCount /= 2;
            parameters.BatchSize /= 2;

            _weights = LLamaWeights.LoadFromFile(parameters);
        }
        _context = _weights.CreateContext(parameters);
        _executor = new InteractiveExecutor(_context);
    }

    private ulong GetSystemMemoryGB()
    {
        return (ulong)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024 / 1024);
    }

    private ulong GetApproxGpuMemoryMB()
    {
        try
        {
            var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = "nvidia-smi";
            process.StartInfo.Arguments =
                "--query-gpu=memory.free --format=csv,noheader,nounits";
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.UseShellExecute = false;
            process.Start();

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (ulong.TryParse(output.Trim(), out ulong memoryMB))
                return memoryMB;
        }
        catch { }

        return 0;
    }

    private int CalculateOptimalGpuLayers(ulong freeVramMB, bool gpuAvailable,string modelPath)
    {
        if (!gpuAvailable || freeVramMB == 0)
            return 0;

        var fileInfo = new FileInfo(modelPath);
        double modelSizeMB = fileInfo.Length / 1024.0 / 1024.0;

        int estimatedTotalLayers = EstimateModelLayers(modelSizeMB);

        double reserveVRAM = freeVramMB * 0.20;
        double usableVRAM = freeVramMB - reserveVRAM;

        double layerMemoryCost = modelSizeMB / estimatedTotalLayers;

        int gpuLayers = (int)(usableVRAM / layerMemoryCost);

        gpuLayers = Math.Min(gpuLayers, estimatedTotalLayers);

        return Math.Max(gpuLayers, 0);
    }

    private int EstimateModelLayers(double modelSizeMB)
    {
        if (modelSizeMB < 2500) return 32;
        if (modelSizeMB < 5000) return 40;
        if (modelSizeMB < 9000) return 60;
        return 80;
    }

    private int CalculateOptimalBatchSize(ulong ramGB, bool gpuAvailable, ulong freeVramMB)
    {
        if (gpuAvailable)
        {
            if (freeVramMB >= 8000) return 1024;
            // Force 512 minimum on GPU. Anything lower severely throttles Time-To-First-Token
            return 512;
        }

        if (ramGB >= 32) return 512;
        return 256;
    }

    private int CalculateOptimalContextSize(ulong ramGB)
    {
        if (ramGB >= 32) return 4096;
        if (ramGB >= 16) return 2048;
        return 1024;
    }

    private HardwareProfile LoadOrBenchmarkHardware(string modelPath)
    {
        if (File.Exists(ProfilePath))
        {
            var json = File.ReadAllText(ProfilePath);
            var cached = System.Text.Json.JsonSerializer.Deserialize<HardwareProfile>(json);

            // CRITICAL: Only use cache if it's for the EXACT SAME model
            if (cached != null && cached.ModelPath == modelPath)
            {
                return cached;
            }
            Console.WriteLine("Model changed. Recalculating hardware profile...");
        }

        Console.WriteLine("Running first-time hardware benchmark...");

        bool gpuAvailable = NativeApi.llama_supports_gpu_offload();
        ulong ramGB = GetSystemMemoryGB();
        ulong freeVramMB = GetApproxGpuMemoryMB();

        int gpuLayers = CalculateOptimalGpuLayers(
            freeVramMB,
            gpuAvailable,
            modelPath);

        int batchSize = CalculateOptimalBatchSize(
            ramGB,
            gpuAvailable,
            freeVramMB);

        int contextSize = CalculateOptimalContextSize(ramGB);

        var profile = new HardwareProfile
        {
            ModelPath = modelPath, // Save it
            GpuLayers = gpuLayers,
            BatchSize = batchSize,
            ContextSize = contextSize,
            FlashAttention = gpuLayers >= 28,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath)!);

        File.WriteAllText(
            ProfilePath,
            System.Text.Json.JsonSerializer.Serialize(profile));

        return profile;
    }

    public async IAsyncEnumerable<string> StreamChatAsync(string prompt, string? webContext = null, string? imagePath = null)
    {
        if (_executor == null)
        {
            LoadActiveModel();
        }

        var activeModel = _modelManager.GetActiveModel();

        if (activeModel == null)
            throw new Exception("No active model.");

        var finalPrompt = string.IsNullOrEmpty(webContext)
            ? prompt
            : $"### WEB SEARCH CONTEXT:\n{webContext}\n\n" +
              $"### INSTRUCTION:\nUsing ONLY the context above, provide a direct answer to: {prompt}. " +
              $"If the context contains a specific date or fact, state it clearly. Do not describe the websites.";

        var formattedPrompt = FormatPrompt(finalPrompt, webContext, _isFirstPrompt);
        _isFirstPrompt = false;

        var inferenceParams = new InferenceParams
        {
            MaxTokens = 1024,
            AntiPrompts = GetAntiPrompts(activeModel.Name),
            SamplingPipeline = new LLama.Sampling.DefaultSamplingPipeline
            {
                RepeatPenalty = 1.15f, // CRITICAL: Breaks infinite word-repetition loops
                Temperature = 0.7f,    // Keeps the model grounded
                TopP = 0.9f
            }
        };

        if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
        {
            yield return "\n[Analyzing Image...]\n";

            // (Native LLava execution requires a dedicated LLavaExecutor which breaks standard chat state).
            string multimodalPrompt = $"[SYSTEM NOTE: The user has uploaded an image located at: {imagePath}]\n{formattedPrompt}";

            await foreach (var token in _executor!.InferAsync(multimodalPrompt, inferenceParams))
            {
                yield return token;
            }
        }
        else
        {
            // Standard Text Inference
            await foreach (var token in _executor!.InferAsync(formattedPrompt, inferenceParams))
            {
                yield return token;
            }
        }
    }

    private string FormatPrompt(string prompt, string? webContext, bool includeSystem, List<McpTool>? mcpTools = null)
    {
        string systemInstruction = "You are Execor, an advanced AI assistant and expert developer.\n\n";

        if (mcpTools != null && mcpTools.Count > 0)
        {
            systemInstruction += "### AVAILABLE TOOLS\n" +
                                 "You have access to the following MCP tools. To use a tool, you MUST output EXACTLY this XML format: " +
                                 "<tool_call>{\"name\": \"tool_name\", \"arguments\": {\"arg1\": \"value\"}}</tool_call>\n\n" +
                                 "TOOLS LIST:\n";
            foreach (var tool in mcpTools)
            {
                systemInstruction += $"- {tool.Name}: {tool.Description}\n  Schema: {tool.InputSchema.GetRawText()}\n\n";
            }
        }

        if (!string.IsNullOrWhiteSpace(webContext) && webContext.Contains("Web Search Results:"))
        {
            systemInstruction +=
                $"### WEB SEARCH CONTEXT:\n{webContext}\n\n" +
                "INSTRUCTION: Answer the user's query factually using the web search context provided above.\n\n";
        }
        else
        {
            systemInstruction +=
                "CONVERSATIONAL RULES: If the user is just saying hello, making small talk, or asking a general question, respond naturally in plain text. DO NOT use markdown code blocks.\n\n" +
                "CODING CAPABILITIES: ONLY when the user explicitly asks you to write, fix, or explain code, act as a senior software engineer. In those specific cases, provide clean code wrapped in proper Markdown blocks (e.g., ```csharp).\n\n";
        }

        // ==========================================
        // DYNAMIC CHAT TEMPLATE EXTRACTION
        // ==========================================
        if (_weights != null)
        {
            try
            {
                var template = new LLamaTemplate(_weights)
                {
                    AddAssistant = true
                };

                // ONLY add the massive system prompt on the very first turn
                if (includeSystem)
                {
                    template.Add("system", systemInstruction);
                }

                template.Add("user", prompt);

                var templateBytes = template.Apply();
                return System.Text.Encoding.UTF8.GetString(templateBytes);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Failed to apply embedded chat template: {ex.Message}");
            }
        }

        // Fallback for extremely old models missing the embedded Jinja template
        return $"{systemInstruction}\n\nUser: {prompt}\nExecor:";
    }

    private List<string> GetAntiPrompts(string modelName)
    {
        // Universally catch all standard End-Of-Turn tokens to prevent infinite loops
        return new List<string>
        {
            "</s>",
            "<|eot_id|>",       // Llama 3 / 3.2 (This is likely the one you were missing)
            "<|end_of_text|>",  // Llama 3 Base
            "<|end|>",          // Phi
            "<end_of_turn>",    // Gemma
            "<|im_end|>",       // Qwen / ChatML
            "User:",
            "user:"
        };
    }

    private void DisposeCurrentModel()
    {
        _context?.Dispose();
        _weights?.Dispose();

        _context = null;
        _weights = null;
        _executor = null;
    }

    public void ClearHistory()
    {
        _isFirstPrompt = true;
        if (_weights == null) return;

        // Destroy the old memory buffer
        _context?.Dispose();

        // Pull the static profile settings
        var activeModel = _modelManager.GetActiveModel();
        var profile = LoadOrBenchmarkHardware(activeModel.FilePath);

        var parameters = new ModelParams(activeModel.FilePath)
        {
            ContextSize = (uint)profile.ContextSize,
            GpuLayerCount = profile.GpuLayers,
            BatchSize = (uint)profile.BatchSize,
            FlashAttention = profile.FlashAttention,
            UseMemorymap = true,
            UseMemoryLock = false,
            Threads = Math.Max(4, Environment.ProcessorCount / 2),
        };

        // Instantly allocate a fresh, empty KV Cache buffer in VRAM
        _context = _weights.CreateContext(parameters);
        _executor = new InteractiveExecutor(_context);
    }
}

public class HardwareProfile
{
    public string ModelPath { get; set; } = ""; // NEW: Tracks the exact model
    public int GpuLayers { get; set; }
    public int BatchSize { get; set; }
    public int ContextSize { get; set; }
    public bool FlashAttention { get; set; }
}