using Jarvis.McpServer.Domain;
using Jarvis.McpServer.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Server.Tests;

public sealed class DatabaseMigrationTests
{
    [Fact]
    public async Task Schema_v1_tool_availability_migrates_to_auto_published_and_hidden_without_recreating_data()
    {
        var root = Path.Combine(Path.GetTempPath(), "jarvis-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "legacy.db");
        try
        {
            await using (var connection = new SqliteConnection("Data Source=" + path))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE Schema (Id INTEGER NOT NULL PRIMARY KEY, Version INTEGER NOT NULL);
                    INSERT INTO Schema (Id, Version) VALUES (1, 1);
                    CREATE TABLE Tools (
                        Id TEXT NOT NULL PRIMARY KEY,
                        Name TEXT NOT NULL,
                        AgentToolId TEXT NOT NULL,
                        Description TEXT NOT NULL,
                        Category TEXT NOT NULL,
                        Enabled INTEGER NOT NULL,
                        Revision TEXT NOT NULL
                    );
                    CREATE TABLE Audit (
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        Action TEXT NOT NULL,
                        Outcome TEXT NOT NULL
                    );
                    INSERT INTO Tools VALUES ('auto-id','auto_tool','auto.tool','imported disabled','fixture',0,'r1');
                    INSERT INTO Tools VALUES ('hidden-id','hidden_tool','hidden.tool','explicitly disabled','fixture',0,'r2');
                    INSERT INTO Tools VALUES ('published-id','published_tool','published.tool','enabled tool','fixture',1,'r3');
                    INSERT INTO Audit (Action, Outcome) VALUES ('catalog.bulk-disable','hidden_tool');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite("Data Source=" + path)
                .Options;
            await using (var db = new AppDbContext(options))
                await DatabaseBootstrap.MigrateSchemaAsync(db);

            await using var verify = new SqliteConnection("Data Source=" + path);
            await verify.OpenAsync();

            var schema = verify.CreateCommand();
            schema.CommandText = "SELECT Version FROM Schema WHERE Id=1;";
            Assert.Equal(DatabaseBootstrap.CurrentSchemaVersion, Convert.ToInt32(await schema.ExecuteScalarAsync()));

            var rows = new Dictionary<string, (string Mode, long Enabled)>(StringComparer.Ordinal);
            var tools = verify.CreateCommand();
            tools.CommandText = "SELECT AgentToolId, PublicationMode, Enabled FROM Tools ORDER BY AgentToolId;";
            await using var reader = await tools.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows[reader.GetString(0)] = (reader.GetString(1), reader.GetInt64(2));

            Assert.Equal((ToolPublicationMode.Auto.ToString(), 1L), rows["auto.tool"]);
            Assert.Equal((ToolPublicationMode.Hidden.ToString(), 0L), rows["hidden.tool"]);
            Assert.Equal((ToolPublicationMode.Published.ToString(), 1L), rows["published.tool"]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
