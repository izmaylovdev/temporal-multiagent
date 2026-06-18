using AgentSandbox.Core;
using Xunit;

namespace AgentSandbox.Tests;

public class AgentsTests
{
    [Fact]
    public void ExtractCode_Fenced()
    {
        var text = "Here you go:\n```python\nprint(1+1)\n```\nthanks";
        Assert.Equal("print(1+1)", Agents.ExtractCode(text));
    }

    [Fact]
    public void ExtractCode_Plain()
    {
        Assert.Equal("print(42)", Agents.ExtractCode("print(42)"));
    }

    [Fact]
    public void FilesBlock_Empty()
    {
        Assert.Contains("No input files", Agents.FilesBlock(Array.Empty<InputFile>()));
    }

    [Fact]
    public void FilesBlock_ListsFiles()
    {
        var block = Agents.FilesBlock(new[] { new InputFile("data.csv", 10, "x,y\n1,2") });
        Assert.Contains("/work/inputs/data.csv", block);
        Assert.Contains("preview", block);
    }
}

public class ModelsTests
{
    [Fact]
    public void RunResult_Defaults()
    {
        var r = new RunResult("r", "succeeded", "t", 1);
        Assert.Equal("", r.Explanation);
        Assert.Equal("", r.Error);
    }

    [Fact]
    public void ExecResult_HoldsValues()
    {
        var e = new ExecResult(0, "ok", "", 12, false, "runc", Array.Empty<string>());
        Assert.Equal("runc", e.Runtime);
        Assert.Empty(e.Artifacts);
    }
}
