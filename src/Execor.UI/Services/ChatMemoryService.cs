using Execor.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Execor.UI.Services;

public class ChatMemoryService
{
    private readonly string _filePath =
        Path.Combine(AppContext.BaseDirectory, "chat_memory.json");

    public List<ChatSessionModel> LoadChats()
    {
        if (!File.Exists(_filePath))
            return new List<ChatSessionModel>();

        var json = File.ReadAllText(_filePath);
        return JsonSerializer.Deserialize<List<ChatSessionModel>>(json)
               ?? new List<ChatSessionModel>();
    }

    public void SaveChats(List<ChatSessionModel> chats)
    {
        var json = JsonSerializer.Serialize(chats,
            new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(_filePath, json);
    }
}
