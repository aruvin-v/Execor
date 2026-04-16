namespace Execor.Models;

public class ModelInfo
{
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
    public long SizeBytes { get; set; }
    public bool IsActive { get; set; }
}