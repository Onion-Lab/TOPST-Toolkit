using TopstToolkit.Core.Workspace;
using TopstToolkit.Engine;
using TopstToolkit.Engine.Commands;
using TopstToolkit.Engine.Helpers;
using TopstToolkit.Engine.Runner;

namespace TopstToolkit.Core.Pipeline;

/// <summary>The engine bound to one workspace: one runner (one tool at a time), the command builder and helpers.</summary>
public sealed class EngineSession
{
    public EngineSession(EngineLayout layout, WorkspaceRoot workspace)
    {
        var missing = layout.FindMissing();
        if (missing.Count > 0)
            throw new FileNotFoundException("The engine runtime is incomplete: " + string.Join(", ", missing));

        Layout = layout;
        Workspace = workspace.EnsureCreated();
        Runner = new ToolRunner(layout);
        Commands = new CommandBuilder(layout, workspace.EmptyModelConfigDir);
        Helpers = new HelperClient(layout, Runner);
    }

    public EngineLayout Layout { get; }
    public WorkspaceRoot Workspace { get; }
    public ToolRunner Runner { get; }
    public CommandBuilder Commands { get; }
    public HelperClient Helpers { get; }

    public StepRunner For(ProjectWorkspace project) => new(this, project);
}
