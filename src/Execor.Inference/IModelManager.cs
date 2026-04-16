using Execor.Models;

namespace Execor.Core;

public interface IModelManager
{
    List<ModelInfo> GetInstalledModels();
    void SetActiveModel(string modelName);
    void DeleteModel(string modelName);
    ModelInfo? GetActiveModel();

    string GetModelsPath();
    void UpdateModelsPath(string newPath);
}