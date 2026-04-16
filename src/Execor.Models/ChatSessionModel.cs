namespace Execor.Models;

public class ChatSessionModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = "New Chat";
    public List<ChatMessageModel> Messages { get; set; } = new();
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public bool IsPinned { get; set; }
}