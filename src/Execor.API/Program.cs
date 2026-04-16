using Execor.Core;
using Execor.Inference.Services;
using Execor.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IModelManager, ModelManager>();
builder.Services.AddSingleton<IChatService, LlamaService>();
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

var modelManager = app.Services.GetRequiredService<IModelManager>();
var chatService = app.Services.GetRequiredService<IChatService>();

var models = modelManager.GetInstalledModels();

if (models.Any())
{
    modelManager.SetActiveModel(models.First().Name);
    chatService.LoadActiveModel();
}

app.MapGet("/api/tags", (IModelManager modelManager) =>
{
    return Results.Ok(modelManager.GetInstalledModels());
});

app.MapPost("/api/models/switch/{modelName}",
    (string modelName, IModelManager modelManager, IChatService chatService) =>
    {
        modelManager.SetActiveModel(modelName);
        chatService.LoadActiveModel();

        return Results.Ok(new { success = true });
    });

app.MapDelete("/api/models/delete/{modelName}",
    (string modelName, IModelManager modelManager) =>
    {
        modelManager.DeleteModel(modelName);

        return Results.Ok(new { success = true });
    });

app.MapPost("/api/chat",
    async (
        ChatRequest request,
        HttpContext context,
        IChatService chatService) =>
    {
        context.Response.Headers.Append("Content-Type", "text/plain; charset=utf-8");
        context.Response.Headers.Append("Cache-Control", "no-cache");
        context.Response.Headers.Append("X-Accel-Buffering", "no");

        await foreach (var token in chatService.StreamChatAsync(request.Prompt))
        {
            await context.Response.WriteAsync(token);
            await context.Response.Body.FlushAsync();
        }
    });

app.Run("http://localhost:5078");