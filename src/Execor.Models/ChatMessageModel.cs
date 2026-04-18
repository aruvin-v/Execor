namespace Execor.Models;

public class ChatMessageModel
{
    public bool IsUser { get; set; }
    public string Text { get; set; } = "";
    public string? ImagePath { get; set; }
}