using Monitoredb.WorkerAgent;

namespace Monitoredb.Tests;

public class CliParserTests
{
    [Fact]
    public void Parse_Defaults_AreApplied()
    {
        var opts = CliParser.Parse([]);

        Assert.Equal("http://localhost:3000", opts.Server);
        Assert.Equal(30, opts.IntervalSeconds);
        Assert.Equal(1433, opts.SqlPort);
        Assert.False(opts.Once);
        Assert.Null(opts.AgentId);
        Assert.Null(opts.Token);
        Assert.Null(opts.SqlHost);
    }

    [Fact]
    public void Parse_AllArgs_AreMapped()
    {
        var opts = CliParser.Parse(
        [
            "--server", "http://server:3000",
            "--agent-id", "win-host",
            "--interval", "15",
            "--token", "abc123",
            "--sql-host", "sqlbox",
            "--sql-port", "1434",
            "--sql-user", "sa",
            "--sql-pass", "senha",
            "--once",
        ]);

        Assert.Equal("http://server:3000", opts.Server);
        Assert.Equal("win-host", opts.AgentId);
        Assert.Equal(15, opts.IntervalSeconds);
        Assert.Equal("abc123", opts.Token);
        Assert.Equal("sqlbox", opts.SqlHost);
        Assert.Equal(1434, opts.SqlPort);
        Assert.Equal("sa", opts.SqlUser);
        Assert.Equal("senha", opts.SqlPassword);
        Assert.True(opts.Once);
    }

    [Fact]
    public void Parse_InvalidInterval_FallsBackToDefault()
    {
        var opts = CliParser.Parse(["--interval", "abc"]);

        Assert.Equal(30, opts.IntervalSeconds);
    }

    [Fact]
    public void Parse_InvalidPort_FallsBackToDefault()
    {
        var opts = CliParser.Parse(["--sql-port", "xyz"]);

        Assert.Equal(1433, opts.SqlPort);
    }

    [Fact]
    public void Parse_MissingValue_ReturnsNull()
    {
        var opts = CliParser.Parse(["--agent-id"]);

        Assert.Null(opts.AgentId);
    }

    [Fact]
    public void Parse_ServerTrailingSlash_IsPreserved()
    {
        var opts = CliParser.Parse(["--server", "http://host:3000/"]);

        Assert.Equal("http://host:3000/", opts.Server);
    }
}