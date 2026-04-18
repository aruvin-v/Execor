# Execor

Execor is a high-performance Windows-based desktop application designed to execute GGUF-based Large Language Models (LLMs) locally. It provides a full-featured UI for offline inference, real-time system monitoring, and specialized developer tools.

## 🚀 Overview

Execor enables developers to run quantized LLMs without cloud dependencies. Built with a focus on efficiency, it automatically benchmarks your hardware to optimize GPU offloading and memory usage.

## ✨ Key Features

* **Local GGUF Inference**: Run models locally using `LLamaSharp` with full support for CUDA acceleration.
* **Real-Time Dashboard**: Monitor CPU usage, RAM, GPU utilization, VRAM, and inference speed (tokens/sec) directly from the sidebar.
* **Specialized Developer Tools**:
    * **`/codereview`**: Analyzes your local Git repository changes (staged and unstaged) and generates a comprehensive code review report exported to Microsoft Word.
    * **`/db` (Database Integration)**: Securely connect to SQL Server, extract schemas into token-efficient Markdown, and chat with your database using natural language to execute read-only queries.
    * **`/web` (Web Search)**: Integrated Tavily API support to augment LLM responses with real-time web context.
    * **`/sys`**: Instant AI analysis of your current hardware performance.
* **Persistent Chat History**: Manage multiple chat sessions with pinning, renaming, and local JSON-based storage.
* **Smart Hardware Profiling**: Automatically benchmarks your system to calculate optimal GPU layers, batch sizes, and context windows.
* **CLI Installer**: A self-installing command-line interface that allows you to launch Execor from any terminal.

## 🛠 Tech Stack

* **Framework**: .NET 8 (WPF)
* **Inference**: LLamaSharp (v0.26.0) with CUDA 12 support
* **UI Components**: AvalonEdit (for syntax highlighting) and CommunityToolkit.Mvvm
* **Data & Config**: SQL Client, Newtonsoft.Json, and Markdown (Markdig)

## ⚙️ Configuration & Setup

1.  **Models Directory**: Create a folder named `models` in the root or set a custom path in `appsettings.json`. Place your `.gguf` files there.
2.  **API Keys**: To enable web search, add your [Tavily API key](https://tavily.com/) to the `appsettings.json` file in the `Execor.UI` project.
3.  **Hardware Profile**: On the first run, Execor will generate a `hardware-profile.json` to store calibrated settings for your specific GPU and RAM.

## ⌨️ Slash Commands

Use these commands directly in the chat input:
* `/web [query]` — Force a web search for the prompt.
* `/codereview [path]` — Perform a deep review of Git diffs in the specified repo.
* `/db [query]` — Ask questions about your connected SQL database.
* `/sys` — Analyze current PC performance stats.
* `/clear` — Wipe the current session history.

## 📦 Installation

To install Execor globally to your system PATH:
```bash
execor install
```

Once installed, launch the application from any terminal:
```bash
execor run
```