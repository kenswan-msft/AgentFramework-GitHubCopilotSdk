using AgentFrameworkGitHubCopilot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

var chatClient = new CopilotSdkChatClient(modelId: "opus-4.5");

ChatClientAgent agent = chatClient.AsAIAgent(
    name: "Local Agent",
    description: "Helpful agent assistant.",
    instructions: "You are a helpful assistant.");

AgentThread thread = await agent.GetNewThreadAsync();

Console.WriteLine("Welcome to the Agent Framework GitHub Copilot demo!");
Console.WriteLine("Type your messages below. Type 'quit' or 'exit' to stop session.");

try
{
    while (true)
    {
        Console.Write("You: ");

        string? message = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(message))
        {
            Console.WriteLine("Request cannot be empty.");
            continue;
        }

        if (message is "quit" or "exit")
        {
            break;
        }

        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(message, thread))
        {
            foreach (AIContent content in update.Contents)
            {
                switch (content)
                {
                    case TextContent textContent:
                        Console.Write(textContent.Text);
                        break;

                    case ErrorContent errorContent:
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine(errorContent.Message);
                        Console.ResetColor();
                        break;
                }
            }
        }

        Console.WriteLine();
    }
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine(ex.Message);
    Console.ResetColor();
}
