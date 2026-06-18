using AgentSandbox.Core;
using Xunit;

namespace AgentSandbox.Tests;

public class HostPathTests
{
    public HostPathTests()
    {
        Environment.SetEnvironmentVariable("RUNS_DIR", "/data/runs");
        Environment.SetEnvironmentVariable("RUNS_DIR_HOST", "/host/abs/.runs");
    }

    [Fact]
    public void TranslatesNestedPath()
    {
        Assert.Equal("/host/abs/.runs/run-1/inputs", Sandbox.HostPath("/data/runs/run-1/inputs"));
    }

    [Fact]
    public void TranslatesBasePath()
    {
        Assert.Equal("/host/abs/.runs", Sandbox.HostPath("/data/runs"));
    }

    [Fact]
    public void PassesThroughUnrelatedPath()
    {
        Assert.Equal("/elsewhere/x", Sandbox.HostPath("/elsewhere/x"));
    }
}

public class TruncateTests
{
    [Fact]
    public void KeepsShortText() => Assert.Equal("abc", Sandbox.Truncate("abc", 100));

    [Fact]
    public void TruncatesLongText()
    {
        var outp = Sandbox.Truncate(new string('x', 50), 10);
        Assert.StartsWith(new string('x', 10), outp);
        Assert.Contains("truncated", outp);
    }
}
