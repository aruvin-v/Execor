using Execor.Core;
using Execor.Models;
using Microsoft.Extensions.Configuration;

namespace Execor.Inference.Services;

public class ModelManager : IModelManager
{
    private string _modelsPath;
    private string? _activeModel;

    public ModelManager(IConfiguration config)
    {
        _modelsPath = config["ExecorSettings:ModelsPath"]
                      ?? throw new Exception("ModelsPath missing in appsettings.json");

        Console.WriteLine($"Models Path Used: {_modelsPath}");

        if (!Directory.Exists(_modelsPath))
            Directory.CreateDirectory(_modelsPath);
    }

    public List<ModelInfo> GetInstalledModels()
    {
        var files = Directory.GetFiles(_modelsPath, "*.gguf");

        return files.Select(file => new ModelInfo
        {
            Name = Path.GetFileName(file),
            FilePath = file,
            SizeBytes = new FileInfo(file).Length,
            IsActive = Path.GetFileName(file) == _activeModel
        }).ToList();
    }

    public void SetActiveModel(string modelName)
    {
        var fullPath = Path.Combine(_modelsPath, modelName);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Model not found: {modelName}");

        _activeModel = modelName;
    }

    public void DeleteModel(string modelName)
    {
        var fullPath = Path.Combine(_modelsPath, modelName);

        if (File.Exists(fullPath))
            File.Delete(fullPath);

        if (_activeModel == modelName)
            _activeModel = null;
    }

    public ModelInfo? GetActiveModel()
    {
        if (_activeModel == null)
            return null;

        var fullPath = Path.Combine(_modelsPath, _activeModel);

        if (!File.Exists(fullPath))
            return null;

        return new ModelInfo
        {
            Name = _activeModel,
            FilePath = fullPath,
            SizeBytes = new FileInfo(fullPath).Length,
            IsActive = true
        };
    }

    public string GetModelsPath()
    {
        return _modelsPath;
    }

    public void UpdateModelsPath(string newPath)
    {
        _modelsPath = newPath;
        if (!Directory.Exists(_modelsPath))
        {
            Directory.CreateDirectory(_modelsPath);
        }

        _activeModel = null; // Clear active model so it forces a reload from the new path
    }
}