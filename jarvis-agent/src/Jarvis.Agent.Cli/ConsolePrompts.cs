using System.Text.Json;
using Jarvis.Agent.Core;
using Jarvis.Agent.Windows;
using Jarvis.Protocol;
using JarvisCode.Core.Tools.BuiltIn;
namespace Jarvis.Agent.Cli;
public sealed class ConsolePrompts : IApprovalService, IUserQuestions
{
    private readonly SemaphoreSlim _input = new(1,1);
    public async Task<bool> ApproveAsync(ToolDescriptor tool, JsonElement arguments, CancellationToken ct)
    {
        await _input.WaitAsync(ct);
        try
        {
            Console.WriteLine("\nAPPROVE ONE ACTION: " + tool.Name);
            Console.WriteLine(JsonSerializer.Serialize(arguments, new JsonSerializerOptions { WriteIndented = true }));
            Console.Write("Type APPROVE to permit exactly this action; anything else denies: ");
            return await Console.In.ReadLineAsync(ct) == "APPROVE";
        }
        finally { _input.Release(); }
    }
    public async Task<UserQuestionAnswers?> AskAsync(IReadOnlyList<UserQuestion> questions, CancellationToken ct)
    {
        await _input.WaitAsync(ct);
        try
        {
            var answers = new Dictionary<string,string>();
            foreach (var question in questions)
            {
                Console.WriteLine("\n" + question.Question);
                foreach (var option in question.Options) Console.WriteLine("  " + option.Label + " — " + option.Description);
                Console.WriteLine(question.MultiSelect ? "Type exact labels separated by commas, or a custom answer:" : "Type an exact label or a custom answer:");
                var answer = await Console.In.ReadLineAsync(ct); if (answer is null) return null; answers[question.Question] = answer;
            }
            return new UserQuestionAnswers(answers);
        }
        finally { _input.Release(); }
    }
}
public sealed class FileArtifactSink(string workspace) : IArtifactSink
{
    public async Task ShowAsync(WidgetArtifact artifact, CancellationToken ct)
    {
        var boundary = new WorkspaceDirectories(workspace);
        var directory = boundary.Resolve(Path.Combine(".jarvis", "artifacts")); Directory.CreateDirectory(directory);
        var file = boundary.Resolve(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".html"));
        await File.WriteAllTextAsync(file, SandboxHtml.Wrap(artifact), ct);
        Console.WriteLine("Visual artifact saved (not opened automatically): " + file);
    }
}
