using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Execor.Models;
using LLama;
using LLama.Common;
using System.Text;
using UglyToad.PdfPig;
using DocumentFormat.OpenXml.Packaging;

namespace Execor.Inference.Services;

public class WorkspaceIntelligenceService : IDisposable
{
    private readonly List<DocumentChunk> _vectorDb = new();
    private LLamaEmbedder? _embedder;
    private LLamaWeights? _weights;

    public string ActiveWorkspacePath { get; private set; } = string.Empty;
    public bool IsInitialized => _embedder != null;

    public async Task InitializeAsync(string embeddingModelPath)
    {
        if (_embedder != null) return;

        var parameters = new ModelParams(embeddingModelPath)
        {
            ContextSize = 512, // Safest limit for mxbai and bge models
            Threads = Math.Max(1, Environment.ProcessorCount / 2) // Keep UI responsive
        };

        _weights = LLamaWeights.LoadFromFile(parameters);
        _embedder = new LLamaEmbedder(_weights, parameters);

        await Task.CompletedTask;
    }

    public async Task<int> IndexDirectoryAsync(string path, IProgress<string>? progress = null)
    {
        if (!IsInitialized) throw new InvalidOperationException("Embedder not initialized.");

        ActiveWorkspacePath = path;
        _vectorDb.Clear();

        // Safely support all text-based programming, web, data, and config files
        var extensions = new[]
        { 
            // .NET & Windows
            ".cs", ".xaml", ".ps1", ".bat",
            // Web (JS/TS ecosystem)
            ".js", ".jsx", ".ts", ".tsx", ".html", ".css",
            // Python & Data
            ".py", ".sql", ".csv",
            // C / C++ / Rust / Go
            ".c", ".cpp", ".h", ".hpp", ".rs", ".go",
            // Java ecosystem
            ".java", ".kt", ".scala",
            // Config & Documentation
            ".md", ".txt", ".json", ".xml", ".yaml", ".yml", ".ini",
            ".pdf", ".docx" 
        };

        // 2. Blacklist all massive/auto-generated build directories
        var excludedDirs = new[] { "\\bin\\", "\\obj\\", "\\.git\\", "\\.vs\\", "\\node_modules\\", "\\packages\\" };

        var files = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                             .Where(f => extensions.Contains(Path.GetExtension(f).ToLower()))
                             .Where(f => !excludedDirs.Any(dir => f.IndexOf(dir, StringComparison.OrdinalIgnoreCase) >= 0))
                             .ToList();

        int chunkCount = 0;
        int fileCount = 0;
        int totalFiles = files.Count;

        foreach (var file in files)
        {
            fileCount++;
            // Send the status back to the UI safely
            progress?.Report($"Indexing [{fileCount}/{totalFiles}]: {Path.GetFileName(file)}");

            var fileInfo = new FileInfo(file);
            string ext = Path.GetExtension(file).ToLower();

            // Relax the file size limit for PDFs/Word docs, as they carry heavy binary data
            // but keep the 500KB strict limit for text/code files
            if (ext != ".pdf" && ext != ".docx" && fileInfo.Length > 1024 * 500)
            {
                continue;
            }

            // Route through the smart document parser
            string content = await ExtractTextFromFileAsync(file, ext);

            if (string.IsNullOrWhiteSpace(content)) continue;

            // 1200 chars ~ 350 tokens. Safe for embedding limits.
            var chunks = ChunkText(content, 1200);

            foreach (var chunk in chunks)
            {
                if (string.IsNullOrWhiteSpace(chunk)) continue;

                var embeddingResult = await _embedder!.GetEmbeddings(chunk);
                float[] embedding = embeddingResult.FirstOrDefault() ?? Array.Empty<float>();

                _vectorDb.Add(new DocumentChunk
                {
                    FilePath = file,
                    Content = chunk,
                    Embedding = embedding
                });
                chunkCount++;
            }
        }
        return chunkCount;
    }

    public async Task<string> SearchAsync(string query, int topK = 3)
    {
        if (!IsInitialized || !_vectorDb.Any()) return string.Empty;

        var queryResult = await _embedder!.GetEmbeddings(query);
        float[] queryEmbedding = queryResult.FirstOrDefault() ?? Array.Empty<float>();

        var results = _vectorDb
            .Select(chunk => new
            {
                Chunk = chunk,
                Score = CosineSimilarity(queryEmbedding, chunk.Embedding)
            })
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .ToList();

        if (!results.Any()) return string.Empty;

        string context = "--- LOCAL WORKSPACE CONTEXT ---\n";
        foreach (var res in results)
        {
            context += $"File: {res.Chunk.FilePath}\nContent:\n{res.Chunk.Content}\n\n";
        }
        return context;
    }

    private List<string> ChunkText(string text, int chunkSize)
    {
        var chunks = new List<string>();
        for (int i = 0; i < text.Length; i += chunkSize)
        {
            int length = Math.Min(chunkSize, text.Length - i);
            chunks.Add(text.Substring(i, length));
        }
        return chunks;
    }

    private float CosineSimilarity(float[] v1, float[] v2)
    {
        float dot = 0, norm1 = 0, norm2 = 0;
        int length = Math.Min(v1.Length, v2.Length);

        for (int i = 0; i < length; i++)
        {
            dot += v1[i] * v2[i];
            norm1 += v1[i] * v1[i];
            norm2 += v2[i] * v2[i];
        }

        if (norm1 == 0 || norm2 == 0) return 0;
        return (float)(dot / (Math.Sqrt(norm1) * Math.Sqrt(norm2)));
    }

    public void Clear()
    {
        _vectorDb.Clear();
        ActiveWorkspacePath = string.Empty;
    }

    public void Dispose()
    {
        _embedder?.Dispose();
        _weights?.Dispose();
    }

    private async Task<string> ExtractTextFromFileAsync(string filePath, string extension)
    {
        if (extension == ".pdf")
        {
            // Extract text page by page
            StringBuilder sb = new StringBuilder();
            using (PdfDocument document = PdfDocument.Open(filePath))
            {
                foreach (var page in document.GetPages())
                {
                    sb.AppendLine(page.Text);
                }
            }
            return sb.ToString();
        }
        else if (extension == ".docx")
        {
            // Extract text from Word document body
            using (WordprocessingDocument wordDoc = WordprocessingDocument.Open(filePath, false))
            {
                return wordDoc.MainDocumentPart?.Document.Body?.InnerText ?? string.Empty;
            }
        }
        else
        {
            // Standard plain-text reading for all code/data files
            return await File.ReadAllTextAsync(filePath);
        }
    }
}