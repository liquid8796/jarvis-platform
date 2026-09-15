using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace Jarvis.Server.Tests;
public sealed class ServerFixture : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin@example.test";
    public const string Password = "Jarvis-Test-Only-42!";
    public string Root { get; } = Path.Combine(Path.GetTempPath(),"jarvis-server-test-"+Guid.NewGuid());
    private readonly Dictionary<string,string?> _old = new();
    public ServerFixture()
    {
        Directory.CreateDirectory(Root);
        Set("ASPNETCORE_ENVIRONMENT","Development"); Set("Jarvis__DataDirectory",Root);
        Set("Jarvis__PublicOrigin","https://jarvis.test"); Set("Bootstrap__AdminEmail",AdminEmail); Set("Bootstrap__AdminPassword",Password);
        Set("Jarvis__OAuthRedirectUris__0","https://client.example/callback"); Set("JARVIS_CONFIG_PATH",null);
    }
    private void Set(string key,string? value){_old[key]=Environment.GetEnvironmentVariable(key);Environment.SetEnvironmentVariable(key,value);}
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");
    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect=false, BaseAddress=new Uri("https://localhost") });
    public static async Task Csrf(HttpClient client)
    { var token=await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf"); client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); client.DefaultRequestHeaders.Add("X-CSRF-TOKEN",token.GetProperty("token").GetString()); }
    public async Task<HttpClient> Admin()
    {
        var client=Client();await Csrf(client);
        (await client.PostAsJsonAsync("/api/auth/login",new { email=AdminEmail,password=Password })).EnsureSuccessStatusCode();
        await Csrf(client);return client;
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if(disposing){foreach(var entry in _old)Environment.SetEnvironmentVariable(entry.Key,entry.Value);try{Directory.Delete(Root,true);}catch(IOException){}}
    }
}
