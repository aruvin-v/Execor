namespace Execor.Core;

public interface IChatService
{
    IAsyncEnumerable<string> StreamChatAsync(string prompt, string? webContext = null, string? imagePath = null);
    void LoadActiveModel();
    void ClearHistory();
}