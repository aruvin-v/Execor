namespace Execor.Inference.Config;

public class InferenceSettings
{
    public int ContextSize { get; set; } = 4096;
    public int GpuLayerCount { get; set; } = 35;
    public int BatchSize { get; set; } = 512;
    public string ModelsPath { get; set; } = "models";
}