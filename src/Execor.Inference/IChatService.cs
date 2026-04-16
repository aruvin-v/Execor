namespace Execor.Core;

public interface IChatService
{
    IAsyncEnumerable<string> StreamChatAsync(string prompt, string? webContext = null);
    void LoadActiveModel();
}