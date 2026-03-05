# Agent Framework GitHub Copilot SDK

A .NET library that bridges the [Microsoft Agent Framework](https://github.com/microsoft/agents) with [GitHub Copilot SDK](https://github.com/github/copilot-sdk), enabling you to use GitHub Copilot as a chat client within the Microsoft Agent Framework.

## Overview

This project provides a `CopilotSdkChatClient` that implements the `IChatClient` interface from Microsoft.Extensions.AI, allowing seamless integration with the Microsoft Agent Framework. It supports both streaming and non-streaming responses from GitHub Copilot models.

## Features

- **IChatClient Implementation** - Fully compatible with Microsoft.Extensions.AI abstractions
- **Streaming Support** - Real-time streaming responses via `GetStreamingResponseAsync`
- **Model Selection** - Configure any available Copilot model (e.g., `gpt-5`, `opus-4.5`)
- **Agent Framework Integration** - Easily convert to `ChatClientAgent` for agent-based workflows

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [GitHub Copilot CLI](https://docs.github.com/en/copilot/using-github-copilot/using-github-copilot-in-the-command-line)

## Getting Started

1. Build and run the project:
   ```bash
   dotnet run --project AgentFrameworkGitHubCopilot
   ```

## Usage Example

```csharp
using AgentFrameworkGitHubCopilot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

// Create a Copilot chat client with your preferred model
var chatClient = new CopilotSdkChatClient(modelId: "opus-4.5");

// Convert to an Agent Framework agent
ChatClientAgent agent = chatClient.AsAIAgent(
    name: "My Agent",
    description: "Helpful agent assistant.",
    instructions: "You are a helpful assistant.");

// Start a conversation
AgentSession session = await agent.CreateSessionAsync();

await foreach (AgentResponseUpdate update in agent.RunStreamingAsync("Hello!", session))
{
    // Handle streaming response
}
```
