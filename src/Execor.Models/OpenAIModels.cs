using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Execor.Models;

public class OpenAIChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "execor-local";

    [JsonPropertyName("messages")]
    public List<OpenAIMessage> Messages { get; set; } = new();

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = false;

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }
}

public class OpenAIMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

// Streaming Response Models
public class OpenAIStreamResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = $"chatcmpl-{Guid.NewGuid()}";

    [JsonPropertyName("object")]
    public string Object { get; set; } = "chat.completion.chunk";

    [JsonPropertyName("created")]
    public long Created { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [JsonPropertyName("model")]
    public string Model { get; set; } = "execor-local";

    [JsonPropertyName("choices")]
    public List<OpenAIStreamChoice> Choices { get; set; } = new();
}

public class OpenAIStreamChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; } = 0;

    [JsonPropertyName("delta")]
    public OpenAIDelta Delta { get; set; } = new();

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

public class OpenAIDelta
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}