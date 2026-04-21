using Execor.Models;

namespace Execor.Core;

public interface IChatService
{
    IAsyncEnumerable<string> StreamChatAsync(string prompt, string? webContext = null, string? imagePath = null, List<McpTool>? mcpTools = null);
    void LoadActiveModel();
    void ClearHistory();
}