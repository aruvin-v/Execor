using Microsoft.Extensions.Configuration;
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace Execor.Inference.Services;

public class SearchService
{
    private readonly HttpClient _httpClient;
    private readonly string? _apiKey;

    public SearchService()
    {
        _httpClient = new HttpClient();

        // Dynamically read the config so we don't have to change MainWindow.xaml.cs
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        _apiKey = config["ExecorSettings:TavilyApiKey"];
    }

    public async Task<string> GetWebContextAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || _apiKey == "YOUR_TAVILY_API_KEY")
        {
            // If key is missing, return a string that the LLM will see as a failure, 
            // prompting it to tell the user to set up their API key.
            return "SYSTEM ERROR: Web search is disabled. The user must add a Tavily API key to appsettings.json.";
        }

        try
        {
            var requestBody = new
            {
                api_key = _apiKey,
                query = query,
                search_depth = "basic", // 'basic' is faster, 'advanced' is deeper
                max_results = 5
            };

            var response = await _httpClient.PostAsJsonAsync("https://api.tavily.com/search", requestBody);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseBody);

            var results = doc.RootElement.GetProperty("results");

            if (results.GetArrayLength() == 0) return ""; // Return empty on no results

            var context = "Web Search Results:\n";
            foreach (var result in results.EnumerateArray())
            {
                var title = result.GetProperty("title").GetString();
                var content = result.GetProperty("content").GetString();
                context += $"- {title}: {content}\n";
            }

            return context;
        }
        catch
        {
            // Fail gracefully so LlamaService falls back to internal knowledge
            return "";
        }
    }
}