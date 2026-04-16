using Execor.Core;
using LLama;
using LLama.Common;
using LLama.Native;

namespace Execor.Inference.Services;

public class LlamaService : IChatService
{
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private InteractiveExecutor? _executor;

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
            if (freeVramMB >= 10000) return 1024;
            if (freeVramMB >= 6000) return 512;
            if (freeVramMB >= 3000) return 256;
            return 128;
        }

        if (ramGB >= 32) return 512;
        if (ramGB >= 16) return 256;
        return 128;
    }

    private int CalculateOptimalContextSize(ulong ramGB)
    {
        if (ramGB >= 32) return 8192;
        if (ramGB >= 16) return 4096;
        return 2048;
    }

    private HardwareProfile LoadOrBenchmarkHardware(string modelPath)
    {
        if (File.Exists(ProfilePath))
        {
            var json = File.ReadAllText(ProfilePath);
            return System.Text.Json.JsonSerializer.Deserialize<HardwareProfile>(json)!;
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

    public async IAsyncEnumerable<string> StreamChatAsync(string prompt, string? webContext = null)
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

        var formattedPrompt = FormatPrompt(finalPrompt, webContext);

        var inferenceParams = new InferenceParams
        {
            MaxTokens = 1024,
            AntiPrompts = GetAntiPrompts(activeModel.Name)
        };

        await foreach (var token in _executor!.InferAsync(
            formattedPrompt,
            inferenceParams))
        {
            yield return token;
        }
    }

    private string FormatPrompt(string prompt, string? webContext)
    {
        string systemInstruction = "You are Execor, an advanced AI assistant and expert developer.\n\n";

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
                    AddAssistant = true // Automatically appends the trigger for the AI to start typing
                };

                template.Add("system", systemInstruction);
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
        modelName = modelName.ToLower();

        if (modelName.Contains("phi"))
            return new List<string> { "<|user|>", "<|end|>" };

        if (modelName.Contains("llama"))
            return new List<string> { "<|eot_id|>" };

        if (modelName.Contains("mistral"))
            return new List<string> { "</s>" };

        if (modelName.Contains("gemma"))
            return new List<string> { "<end_of_turn>" };

        return new List<string> { "</s>" };
    }

    private void DisposeCurrentModel()
    {
        _context?.Dispose();
        _weights?.Dispose();

        _context = null;
        _weights = null;
        _executor = null;
    }
}

public class HardwareProfile
{
    public int GpuLayers { get; set; }
    public int BatchSize { get; set; }
    public int ContextSize { get; set; }
    public bool FlashAttention { get; set; }
}