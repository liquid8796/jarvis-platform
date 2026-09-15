using System.Text.Json.Nodes;
using JarvisCode.Core.Mcp;
using JarvisCode.Core.Validation;

namespace JarvisCode.Core.Tests.Mcp;

public sealed class SchemaValidationTests
{
    [Theory]
    [InlineData("{\"type\":\"banana\"}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"nested\":{\"minLength\":-1}}}")]
    [InlineData("{\"required\":[\"x\",\"x\"]}")]
    [InlineData("{\"items\":[]}")]
    [InlineData("{\"$defs\":{\"bad\":{\"minimum\":\"zero\"}}}")]
    public void Invalid_schema_keywords_are_reported_including_nested_schemas(string json)
    {
        var result = JsonSchemaValidation.ValidateSchema(JsonNode.Parse(json));
        Assert.False(result.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void Draft202012_applicators_and_local_references_validate_instances()
    {
        var schema = JsonNode.Parse("""
            {"$defs":{"positive":{"type":"integer","minimum":1}},
             "type":"object","properties":{"values":{"type":"array","prefixItems":[{"const":"tag"}],
               "items":{"$ref":"#/$defs/positive"}}},"required":["values"],"unevaluatedProperties":false}
            """)!;
        Assert.True(JsonSchemaValidation.ValidateInstance(schema, JsonNode.Parse("{\"values\":[\"tag\",2,3]}")).IsValid);
        Assert.False(JsonSchemaValidation.ValidateInstance(schema, JsonNode.Parse("{\"values\":[\"tag\",0]}")).IsValid);
        Assert.False(JsonSchemaValidation.ValidateInstance(schema, JsonNode.Parse("{\"values\":[\"tag\"],\"extra\":true}")).IsValid);
    }

    [Fact]
    public void Schemas_with_the_same_id_do_not_overwrite_each_other()
    {
        var first = JsonNode.Parse("{\"$id\":\"https://schemas.example/value\",\"type\":\"integer\"}")!;
        var second = JsonNode.Parse("{\"$id\":\"https://schemas.example/value\",\"type\":\"string\"}")!;
        Assert.True(JsonSchemaValidation.ValidateInstance(first, JsonValue.Create(4)).IsValid);
        Assert.True(JsonSchemaValidation.ValidateInstance(second, JsonValue.Create("four")).IsValid);
        Assert.False(JsonSchemaValidation.ValidateInstance(first, JsonValue.Create("four")).IsValid);
    }

    [Fact]
    public void Unresolved_external_reference_does_not_accept_an_instance()
    {
        var schema = JsonNode.Parse("{\"$ref\":\"https://schemas.invalid/never-fetch\"}")!;
        Assert.False(JsonSchemaValidation.ValidateInstance(schema, new JsonObject()).IsValid);
    }

    [Fact]
    public void Mcp_meta_errors_use_the_reference_first_error_path_and_sentence()
    {
        var invalid = JsonNode.Parse("{\"type\":\"object\",\"required\":\"wrong\"}")!.AsObject();
        var result = McpToolSchemas.Validate(invalid);
        Assert.False(result.Valid);
        Assert.Equal("meta", result.Check);
        Assert.Equal("schema/required must be array", result.Detail);
        Assert.True(McpToolSchemas.Validate(new JsonObject()).Valid);
    }

    [Fact]
    public void Server_gates_follow_exact_domain_or_subdomain_and_wildcard_rules()
    {
        var config = new McpServerConfig("remote", "", [], new Dictionary<string, string>())
            { Url = "https://sub.example.test/mcp" };
        var policy = new McpSchemaPolicy { NormalizeHosts = ["EXAMPLE.TEST"], DropInvalidHosts = [] };
        Assert.True(policy.Normalize(config));
        Assert.False(policy.DropInvalid(config));
        Assert.False(policy.Normalize(config with { Url = "https://otherexample.test/mcp" }));
        Assert.False(policy.Normalize(config with { Url = null }));
        Assert.True(new McpSchemaPolicy().Normalize(config with { Url = null }));
        Assert.True(new McpSchemaPolicy().DropInvalid(config));
    }

    [Fact]
    public void Mcp_dialect_and_null_keyword_rules_follow_the_installed_cli()
    {
        Assert.True(McpToolSchemas.Validate(JsonNode.Parse("{\"type\":\"object\",\"required\":null}")!.AsObject()).Valid);
        Assert.True(McpToolSchemas.Validate(JsonNode.Parse("{\"$schema\":\"http://json-schema.org/draft-07/schema#\",\"items\":[]}")!.AsObject()).Valid);
        Assert.False(McpToolSchemas.Validate(JsonNode.Parse("{\"$schema\":\"https://json-schema.org/draft/2020-12/schema#\",\"items\":[]}")!.AsObject()).Valid);
        var combinator = JsonNode.Parse("{\"anyOf\":[{\"properties\":{\"x\":{\"type\":\"string\"}}}]}")!.AsObject();
        Assert.Null(McpToolSchemas.Apply(combinator, McpServerScope.Operator, out _, normalizeCombinators: false));
        Assert.NotNull(McpToolSchemas.Apply(combinator, McpServerScope.Operator, out _, normalizeCombinators: true));
    }

    [Fact]
    public void Repo_scope_drops_invalid_schema_but_user_scope_preserves_operator_choice()
    {
        var schema = JsonNode.Parse("{\"type\":\"object\",\"required\":\"wrong\"}")!.AsObject();
        Assert.Null(McpToolSchemas.Apply(schema, McpServerScope.Repo, out var reason));
        Assert.NotNull(reason);
        Assert.NotNull(McpToolSchemas.Apply(schema, McpServerScope.Operator, out _));
    }
}
