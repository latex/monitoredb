using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Monitoredb.Tests;

/// <summary>Testes que exigem estado vazio — usam factory própria isolada.</summary>
public class ApiEmptyStateTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ApiEmptyStateTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Agents_InitiallyEmpty()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/agents");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"count\":0", body);
    }

    [Fact]
    public async Task Latest_Empty_Returns404()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/latest");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}