using Execor.Core;
using Execor.Inference;
using Execor.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Execor.UI.Services;

public class BackendHostService : IHostedService
{
    private WebApplication? _app;
    private readonly IServiceProvider _serviceProvider;

    public BackendHostService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();

        // Listen on localhost:8080
        builder.WebHost.UseUrls("http://127.0.0.1:8080");

        _app = builder.Build();

        // The exact endpoint external tools (like Cursor) will hit
        _app.MapPost("/v1/chat/completions", async (OpenAIChatRequest request, HttpContext context) =>
        {
            // Resolve the active chat service from the WPF Dependency Injection container
            var chatService = _serviceProvider.GetRequiredService<IChatService>();

            // Combine the message history from the IDE into a single context string
            var promptBuilder = new StringBuilder();
            foreach (var msg in request.Messages)
            {
                promptBuilder.AppendLine($"{msg.Role.ToUpper()}: {msg.Content}");
            }
            promptBuilder.AppendLine("ASSISTANT:");
            string finalPrompt = promptBuilder.ToString();

            if (request.Stream)
            {
                // Set headers for Server-Sent Events (SSE)
                context.Response.Headers.Append("Content-Type", "text/event-stream");
                context.Response.Headers.Append("Cache-Control", "no-cache");
                context.Response.Headers.Append("Connection", "keep-alive");

                try
                {
                    await foreach (var token in chatService.StreamChatAsync(finalPrompt))
                    {
                        if (string.IsNullOrEmpty(token)) continue;

                        var chunk = new OpenAIStreamResponse();
                        chunk.Choices.Add(new OpenAIStreamChoice
                        {
                            Delta = new OpenAIDelta { Content = token }
                        });

                        string jsonChunk = JsonSerializer.Serialize(chunk);
                        await context.Response.WriteAsync($"data: {jsonChunk}\n\n", cancellationToken);
                        await context.Response.Body.FlushAsync(cancellationToken);
                    }

                    // Send the standard OpenAI closing token
                    await context.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
                    await context.Response.Body.FlushAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    // Fallback to prevent silent pipe freezing
                    await context.Response.WriteAsync($"data: {{\"error\": \"{ex.Message}\"}}\n\n", cancellationToken);
                }
            }
            else
            {
                // Future Implementation: Handle Non-Streaming Responses if an IDE requires it.
                // 99% of developer tools request stream = true.
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("Execor API currently only supports streaming responses.");
            }
        });

        // Start the server silently in the background
        _ = _app.RunAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app != null)
        {
            await _app.StopAsync(cancellationToken);
            await _app.DisposeAsync();
        }
    }
}