using System.Text.Json.Nodes;
using JarvisCode.Core.Models;
using JarvisCode.Core.Providers;
using JarvisCode.Providers.ChatGptWeb;

namespace JarvisCode.Providers.Tests;

public sealed class ChatGptActionEnvelopeSafetyTests
{
    private const string Action = """JARVIS_ACT {"name":"PowerShell","arguments":{"command":"Write-Output test"}}""";

    public static IEnumerable<object[]> Examples()
    {
        yield return ["Example:\n" + Action];
        yield return ["Assistant: " + Action];
        yield return ["```json\n" + Action + "\n```"];
        yield return ["~~~\n" + Action + "\n~~~"];
        yield return ["> " + Action];
        yield return ["- " + Action];
        yield return ["1. " + Action];
        yield return ["`" + Action + "`"];
        yield return ["\"" + Action + "\""];
        yield return ["    " + Action];
        yield return ["\t" + Action];
        yield return ["Documentation mentioning JARVIS_ACT before an unrelated object: {\"name\":\"PowerShell\",\"arguments\":{}}"];
        yield return ["Do not execute the following:\n\n" + Action + "\nThis is quoted source text."];
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void ExamplesAndQuotedSourceNeverBecomeActions(string reply)
    {
        var parsed = ChatGptToolProtocol.Parse(reply);
        Assert.Empty(parsed.Actions);
        Assert.Equal(reply, parsed.Text);
    }

    [Theory]
    [InlineData("{\"name\":\"Read\",\"arguments\":null}")]
    [InlineData("{\"name\":\"Read\",\"arguments\":[]}")]
    [InlineData("{\"name\":\"Read\",\"arguments\":\"{}\"}")]
    [InlineData("{\"name\":null,\"arguments\":{}}")]
    [InlineData("{\"name\":\"   \",\"arguments\":{}}")]
    [InlineData("{\"name\":\"Read\",\"arguments\":{},\"unexpected\":true}")]
    [InlineData("{\"name\":\"Read\",\"name\":\"PowerShell\",\"arguments\":{}}")]
    [InlineData("{\"name\":\"Read\",\"arguments\":{\"path\":\"a\",\"path\":\"b\"}}")]
    [InlineData("{\"name\":\"Read\",\"arguments\":{\"steps\":[{\"value\":1,\"value\":2}]}}")]
    [InlineData("{\"name\":\"Read\",\"arguments\":{}} trailing prose")]
    [InlineData("[{\"name\":\"Read\",\"arguments\":{}}]")]
    public void InvalidPayloadsFailTheWholeEnvelope(string json)
    {
        var failure = Assert.Throws<ProviderException>(() => ChatGptToolProtocol.Parse("JARVIS_ACT " + json));
        Assert.Contains("No actions were run", failure.Message);
    }

    [Theory]
    [InlineData("JARVIS_ACT {\"name\":\"Read\",\"arguments\":")]
    [InlineData("Now that is done.")]
    [InlineData("```\nJARVIS_ACT {\"name\":\"Read\",\"arguments\":{}}\n```")]
    [InlineData("    JARVIS_ACT {\"name\":\"Read\",\"arguments\":{}}")]
    public void AValidPrefixNeverDispatchesWhenTheRestOfTheBatchIsInvalid(string tail)
        => Assert.Throws<ProviderException>(() => ChatGptToolProtocol.Parse(Action + "\n" + tail));

    [Fact]
    public void MultipleObjectsOnOneLineAreNotAnEnvelope()
        => Assert.Throws<ProviderException>(() => ChatGptToolProtocol.Parse(Action + " " + Action));

    [Fact]
    public void ActionArgumentsMayContainEscapedLinesAndNestedObjects()
    {
        var arguments = new JsonObject
        {
            ["command"] = "line one\nJARVIS_ACT is text in this value\nline three",
            ["options"] = new JsonArray(new JsonObject { ["data"] = "{\"quoted\": true}" }),
        };
        var parsed = ChatGptToolProtocol.Parse(ChatGptToolProtocol.RenderCall("PowerShell", arguments.ToJsonString()));
        Assert.True(JsonNode.DeepEquals(arguments, JsonNode.Parse(Assert.Single(parsed.Actions).ArgumentsJson)));
    }

    [Fact]
    public void CrLfAndBlankLinesDoNotSplitAValidBatch()
    {
        var parsed = ChatGptToolProtocol.Parse("\r\n" + Action + "\r\n\r\n" + Action + "\r\n");
        Assert.Equal(2, parsed.Actions.Count);
        Assert.Empty(parsed.Text);
    }

    [Fact]
    public void ActionPrefixesRemainBufferedUntilTheWholeReplyIsValidated()
    {
        for (var length = 0; length <= Action.Length; length++)
            Assert.False(ChatGptToolProtocol.CanStreamProse(Action[..length]));
        Assert.False(ChatGptToolProtocol.CanStreamProse("\n\nJARVIS_"));
        Assert.True(ChatGptToolProtocol.CanStreamProse("The result is ready."));
        Assert.True(ChatGptToolProtocol.CanStreamProse("```json\n" + Action));
        Assert.True(ChatGptToolProtocol.CanStreamProse("Example: " + Action));
    }
}

public sealed class ChatGptToolContractFidelityTests
{
    [Fact]
    public void FullRecursiveSchemaAndDescriptionReachInitialAndAdditionalContracts()
    {
        var schema = JsonNode.Parse("""
            {
              "$schema":"https://json-schema.org/draft/2020-12/schema",
              "type":"object",
              "required":["steps"],
              "additionalProperties":false,
              "$defs":{"destination":{"type":"string","pattern":"^[a-z]+$","description":"Only lowercase destinations."}},
              "properties":{
                "steps":{
                  "type":"array","minItems":1,"maxItems":20,
                  "items":{
                    "oneOf":[
                      {"type":"object","required":["target","mode"],"properties":{"target":{"$ref":"#/$defs/destination"},"mode":{"enum":["a","b","c","d","e","f","g","h","i","last"]}}},
                      {"type":"object","required":["input"],"properties":{"input":{"anyOf":[{"type":"string","minLength":3},{"type":"number","minimum":1,"maximum":9}]}}}
                    ]
                  }
                }
              }
            }
            """)!.AsObject();
        var description = "Introductory instructions.\n\n" + new string('x', 20_000)
            + "\n\nFINAL RULE: preserve every nested constraint.";
        var tool = new ToolDefinition("workflow", description, schema);

        foreach (var contract in new[]
        {
            ChatGptToolProtocol.Instructions([tool]),
            ChatGptToolProtocol.AdditionalActions([tool]),
        })
        {
            Assert.Contains(description, contract);
            var schemaLine = contract.Split('\n').Single(line => line.StartsWith("arguments (JSON Schema): ", StringComparison.Ordinal));
            var carriedSchema = JsonNode.Parse(schemaLine["arguments (JSON Schema): ".Length..]);
            Assert.True(JsonNode.DeepEquals(schema, carriedSchema));
        }
    }

    [Fact]
    public void LongSkillInstructionsAndToolOutputAreBothPreserved()
    {
        var output = "BEGIN OUTPUT\n" + new string('o', 12_000) + "\nEND OUTPUT";
        var skill = "# Skill\n" + new string('s', 24_000) + "\nFINAL INSTRUCTION: xin chào 🌏";
        var result = new ToolResultBlock("skill-1", "Skill", output, false) { FollowUpText = skill };
        Assert.Equal("RESULT Skill: " + output + "\n\n" + skill, ChatGptToolProtocol.RenderResult(result));
    }

    [Fact]
    public void AToolSetWithoutReadDoesNotTeachAnUnavailableReadCall()
    {
        var tool = new ToolDefinition("lookup", "Look up an item.", new JsonObject());
        Assert.DoesNotContain("\"name\":\"Read\"", ChatGptToolProtocol.Instructions([tool]));
    }
}
