using System.IO;
using System.Text.Json.Nodes;
using JarvisCode.App.Controls;

namespace JarvisCode.App.Tests.Services;

public sealed class StreamCeilingParityTests
{
    [Fact]
    public void ChatAndCodeCeilingsMatchTheInstalledJavascriptAcrossStreamPrefixes()
    {
        var fixtures = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stream-ceiling-1.46388.3.0.json"))) as JsonArray;
        var failures = new List<string>();
        foreach (var fixture in fixtures!)
        {
            // A real stream can stop between surrogate halves; System.Text.Json
            // rejects that string even though both JS and .NET can hold it.
            var text = new string((fixture!["units"] as JsonArray)!.Select(unit => (char)unit!.GetValue<int>()).ToArray());
            foreach (var (mode, protect) in new[] { ("chat", true), ("code", false) })
            {
                var expected = fixture[mode]!.GetValue<int>();
                var actual = ReferenceStreamCeiling.Ceiling(text, protect);
                if (expected != actual) failures.Add($"{mode} expected {expected}, got {actual}: {System.Text.Json.JsonSerializer.Serialize(text)}");
            }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(30)));
    }
}
