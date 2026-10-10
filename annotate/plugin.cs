using System.Collections.Concurrent;
using System.Text.Json;
using CodeAlta.Plugins.Abstractions;

[Plugin("annotate", DisplayName = "Annotate", Description = "Comment on lines of a session's diff or plan, then send the comments to an agent.", Frontends = PluginFrontends.Desktop)]
public sealed class AnnotatePlugin : PluginBase
{
    private readonly ConcurrentDictionary<string, string> _projectPaths = new(StringComparer.Ordinal);

    public override IEnumerable<PluginCanvasContribution> GetCanvases()
    {
        yield return new PluginCanvasContribution
        {
            Id = "review", Title = "Review", Icon = "message-square-text", Scope = PluginCanvasScope.Session,
            Description = "Comments on lines of the uncommitted diff or of a plan file of a session, sent to an agent as one prompt.",
            Open = (canvas, _) =>
            {
                canvas.Rpc.Handle<Empty, ReviewContext>("review.context", async (_, ct) =>
                    new ReviewContext(canvas.SessionId, canvas.ProjectId, await ProjectPathAsync(canvas.ProjectId, ct), canvas.Key));
                return ValueTask.FromResult(PluginCanvasView.Html("<p class=\"alta-muted\">Loading…</p>") with { Script = PluginScript.File("ui/review.js") });
            },
        };
    }

    private async ValueTask<string?> ProjectPathAsync(string? projectId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(projectId)) return null;
        if (_projectPaths.TryGetValue(projectId, out var known)) return known;
        var result = await Services.Alta.InvokeAsync(["project", "show", projectId], cancellationToken: cancellationToken);
        foreach (var line in result.TranscriptJsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("projectPath", out var path) && path.GetString() is { Length: > 0 } value)
                return _projectPaths[projectId] = value;
        }

        return null;
    }

    public sealed record Empty;

    public sealed record ReviewContext(string? SessionId, string? ProjectId, string? ProjectPath, string? Key);
}
