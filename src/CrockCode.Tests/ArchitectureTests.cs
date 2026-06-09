using System;
using NetArchTest.Rules;
using Xunit;

namespace CrockCode.Tests;

public class ArchitectureTests
{
    [Fact]
    public void Core_ShouldNot_Reference_VendorAssemblies()
    {
        var result = Types.InAssembly(typeof(CrockCode.Core.Domain.TaskId).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny("Microsoft.Data.Sqlite", "Anthropic", "ModelContextProtocol")
            .GetResult();

        Assert.True(result.IsSuccessful, "Core assembly should not depend on vendor packages.");
    }

    [Fact]
    public void Engine_ShouldNot_Reference_VendorAssemblies()
    {
        var result = Types.InAssembly(typeof(CrockCode.Engine.WorkflowRunner).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny("Microsoft.Data.Sqlite", "Anthropic", "ModelContextProtocol")
            .GetResult();

        Assert.True(result.IsSuccessful, "Engine assembly should not depend on vendor packages.");
    }

    [Fact]
    public void Cli_ShouldNot_Reference_VendorAssemblies()
    {
        var result = Types.InAssembly(typeof(CrockCode.Cli.Program).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny("Microsoft.Data.Sqlite", "Anthropic", "ModelContextProtocol")
            .GetResult();

        Assert.True(result.IsSuccessful, "Cli assembly should not depend on vendor packages.");
    }
}
