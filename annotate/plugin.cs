using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

// Annotate: comment on lines of a session's uncommitted diff or of a plan file, then send all open comments to an agent as one
// line-anchored prompt. The review is a canvas drawn by ui/review.js; the comments, the diff and the plan blocks live here and
// reach the script through `canvas.Rpc`. Comments are kept per project in `<project>/.alta/plugin-data/annotate/annotations.json`.
[Plugin("annotate", DisplayName = "Annotate", Description = "Comment on lines of a session's diff or plan, then send the comments to an agent.", Frontends = PluginFrontends.Desktop)]
public sealed class AnnotatePlugin : PluginBase
{
    private const string CanvasId = "review";
    private const string DiffReview = "diff";
    private const string PlanPrefix = "plan:";

    private static readonly JsonSerializerOptions RpcJson = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions ToolJson = new(RpcJson) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ConcurrentDictionary<string, string> _projectPaths = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PluginCanvasContext> _open = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NoteStore> _stores = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _unresolvedBySession = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _loadingProjects = new(StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- Canvas

    public override IEnumerable<PluginCanvasContribution> GetCanvases()
    {
        yield return new PluginCanvasContribution
        {
            Id = CanvasId, Title = "Review", Icon = "message-square-text", Scope = PluginCanvasScope.Session,
            Description = "Line comments on the uncommitted diff (key `diff`) or on a plan file (key `plan:<relative path>`) of a session, sent to an agent as one prompt.",
            Open = (canvas, _) => ValueTask.FromResult(Open(canvas)),
            Closed = canvas =>
            {
                _open.TryRemove(canvas.InstanceId, out _);
                return ValueTask.CompletedTask;
            },
            Describe = async (canvas, ct) => await DescribeAsync(canvas, ct),
            Actions =
            [
                new PluginCanvasActionContribution
                {
                    Name = "add", Description = "Adds a comment on a line range. Input: file (project-relative; the plan path for a plan review), startLine, endLine, side (new or old; diff only), body.",
                    InputSchema = """{"type":"object","properties":{"file":{"type":"string"},"startLine":{"type":"integer","minimum":1},"endLine":{"type":"integer","minimum":1},"side":{"type":"string","enum":["new","old"]},"body":{"type":"string"}},"required":["file","startLine","body"]}""",
                    Handler = async (canvas, input, ct) =>
                    {
                        var request = Read<AddNote>(input) ?? throw new ArgumentException("No input.");
                        return Json(await AddNoteAsync(canvas, request, ct));
                    },
                },
                new PluginCanvasActionContribution
                {
                    Name = "list", Description = "Lists the comments of this review, after re-anchoring them on the current content.",
                    Handler = async (canvas, _, ct) => Json((await LoadReviewAsync(canvas, ct)).Notes),
                },
                new PluginCanvasActionContribution
                {
                    Name = "send", Description = "Sends every unresolved comment to an agent as one prompt. Input: target = `session` (the review's session, default), a session id, or `new`.",
                    InputSchema = """{"type":"object","properties":{"target":{"type":"string"}}}""",
                    Handler = async (canvas, input, ct) => Json(await SendAsync(canvas, Read<SendRequest>(input)?.Target, ct)),
                },
                new PluginCanvasActionContribution
                {
                    Name = "resolve", Description = "Resolves a comment, or reopens it with resolved = false. Input: id, resolved.",
                    InputSchema = """{"type":"object","properties":{"id":{"type":"string"},"resolved":{"type":"boolean"}},"required":["id"]}""",
                    Handler = async (canvas, input, ct) =>
                    {
                        var request = Read<ResolveNote>(input) ?? throw new ArgumentException("No input.");
                        return Json(await ResolveAsync(canvas, request, ct));
                    },
                },
            ],
        };
    }

    private PluginCanvasView Open(PluginCanvasContext canvas)
    {
        _open[canvas.InstanceId] = canvas;
        canvas.Closed.Register(() => _open.TryRemove(canvas.InstanceId, out _));
        var rpc = canvas.Rpc;
        rpc.JsonOptions = RpcJson;
        rpc.Handle<Empty, ReviewContext>("review.context", async (_, ct) =>
        {
            var root = await ProjectPathAsync(canvas.ProjectId, ct);
            return new ReviewContext(canvas.SessionId, canvas.ProjectId, root, ReviewOf(canvas), root is null ? null : DataDirectory(root));
        });
        rpc.Handle<Empty, ReviewView>("review.get", async (_, ct) => await LoadReviewAsync(canvas, ct));
        rpc.Handle<Empty, IReadOnlyList<Note>>("review.notes", async (_, ct) => await NotesAsync(canvas, ct));
        rpc.Handle<AddNote, Note>("note.add", async (request, ct) => await AddNoteAsync(canvas, request ?? throw Invalid("No input."), ct));
        rpc.Handle<UpdateNote, Note>("note.update", async (request, ct) => await UpdateAsync(canvas, request ?? throw Invalid("No input."), ct));
        rpc.Handle<NoteId, DeleteResult>("note.delete", async (request, ct) => await DeleteAsync(canvas, request ?? throw Invalid("No input."), ct));
        rpc.Handle<ResolveNote, Note>("note.resolve", async (request, ct) => await ResolveAsync(canvas, request ?? throw Invalid("No input."), ct));
        rpc.Handle<SendRequest, SendResult>("review.send", async (request, ct) => await SendAsync(canvas, request?.Target, ct));
        rpc.Handle<Empty, IReadOnlyList<SessionItem>>("review.sessions", async (_, ct) => await SessionsAsync(canvas, ct));
        rpc.Handle<Empty, IReadOnlyList<PlanItem>>("review.plans", async (_, ct) => PlanFiles(await RootAsync(canvas, ct)));
        rpc.Handle<OpenReview>("review.open", async (request, ct) =>
        {
            var review = request?.Review is { Length: > 0 } value ? value : DiffReview;
            await Services.Canvases.OpenAsync(CanvasId, new PluginCanvasOpenOptions { ProjectId = canvas.ProjectId, SessionId = canvas.SessionId, Key = review }, ct);
        });

        return PluginCanvasView.Html("<p class=\"alta-muted\">Loading the review…</p>") with
        {
            Script = PluginScript.File("ui/review.js"),
            Title = TitleOf(ReviewOf(canvas)),
        };
    }

    private static string ReviewOf(PluginCanvasContext canvas) => string.IsNullOrEmpty(canvas.Key) ? DiffReview : canvas.Key;

    private static string TitleOf(string review)
        => review.StartsWith(PlanPrefix, StringComparison.Ordinal) ? "Review · " + Path.GetFileNameWithoutExtension(review[PlanPrefix.Length..]) : "Review · changes";

    private async ValueTask<string?> DescribeAsync(PluginCanvasContext canvas, CancellationToken cancellationToken)
    {
        var notes = await NotesAsync(canvas, cancellationToken);
        var text = new StringBuilder($"# {TitleOf(ReviewOf(canvas))}\n\n");
        if (notes.Count == 0) return text.Append("No comments.").ToString();
        foreach (var note in notes)
            text.Append($"- [{note.Id}] `{note.File}` {Lines(note)}{(note.Kind == "diff" ? $" ({note.Side})" : "")} — {note.Status}{(note.Outdated ? ", outdated" : "")}{(note.Addressed ? ", addressed" : "")}: {FirstLine(note.Body)}\n");
        return text.ToString();
    }

    // ---------------------------------------------------------------- Review content

    private async Task<ReviewView> LoadReviewAsync(PluginCanvasContext canvas, CancellationToken cancellationToken)
    {
        var review = ReviewOf(canvas);
        var root = await ProjectPathAsync(canvas.ProjectId, cancellationToken);
        if (root is null) return new ReviewView(review, KindOf(review), TitleOf(review), canvas.SessionId, null, null, "The review has no project.", [], [], []);

        var content = await LoadContentAsync(root, review, cancellationToken);
        var store = await StoreAsync(root, cancellationToken);
        var notes = store.Reanchor(canvas.SessionId, review, content.LinesOf);
        UpdateCounts(store);
        return new ReviewView(review, content.Kind, TitleOf(review), canvas.SessionId, root, content.PlanPath, content.Error, content.Files, content.Blocks, notes);
    }

    private static string KindOf(string review) => review.StartsWith(PlanPrefix, StringComparison.Ordinal) ? "plan" : "diff";

    private static async Task<ReviewContent> LoadContentAsync(string root, string review, CancellationToken cancellationToken)
    {
        if (review.StartsWith(PlanPrefix, StringComparison.Ordinal))
        {
            var relative = review[PlanPrefix.Length..];
            var full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) return ReviewContent.Failed("plan", relative, "The plan is outside the project.");
            if (!File.Exists(full)) return ReviewContent.Failed("plan", relative, $"The plan `{relative}` does not exist.");
            var lines = ReadLines(full);
            return new ReviewContent("plan", relative, null, [], PlanSplitter.Split(lines), (file, _) => file == relative ? ReadLines(full) : null);
        }

        return await DiffSource.LoadAsync(root, cancellationToken);
    }

    private static List<string> ReadLines(string path)
    {
        var text = File.ReadAllText(path);
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static IReadOnlyList<PlanItem> PlanFiles(string root)
    {
        var folder = Path.Combine(root, ".alta", "plans");
        if (!Directory.Exists(folder)) return [];
        return [.. Directory.EnumerateFiles(folder, "*.md")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Select(path => new PlanItem(Path.GetRelativePath(root, path).Replace('\\', '/'), PlanTitle(path)))];
    }

    private static string PlanTitle(string path)
    {
        foreach (var line in File.ReadLines(path).Take(40))
        {
            var match = Regex.Match(line, "^title:\\s*\"?(.*?)\"?\\s*$");
            if (match.Success && match.Groups[1].Value.Length > 0) return match.Groups[1].Value;
            if (line.StartsWith("# ", StringComparison.Ordinal)) return line[2..].Trim();
        }

        return Path.GetFileNameWithoutExtension(path);
    }

    // ---------------------------------------------------------------- Comments

    private async Task<IReadOnlyList<Note>> NotesAsync(PluginCanvasContext canvas, CancellationToken cancellationToken)
    {
        var store = await StoreAsync(await RootAsync(canvas, cancellationToken), cancellationToken);
        return store.Notes(canvas.SessionId, ReviewOf(canvas));
    }

    private async Task<Note> AddNoteAsync(PluginCanvasContext canvas, AddNote request, CancellationToken cancellationToken)
    {
        var review = ReviewOf(canvas);
        var root = await RootAsync(canvas, cancellationToken);
        var body = request.Body?.Trim() ?? "";
        if (body.Length == 0) throw Invalid("A comment needs a text.");
        var file = (request.File ?? "").Replace('\\', '/').Trim();
        var side = review.StartsWith(PlanPrefix, StringComparison.Ordinal) ? "new" : request.Side is "old" ? "old" : "new";
        if (review.StartsWith(PlanPrefix, StringComparison.Ordinal) && file.Length == 0) file = review[PlanPrefix.Length..];
        var start = request.StartLine;
        var end = request.EndLine is { } last && last >= start ? last : start;

        var content = await LoadContentAsync(root, review, cancellationToken);
        var lines = content.LinesOf(file, side) ?? throw new PluginRpcException("not_found", $"`{file}` has no {side} content in this review.");
        if (start < 1 || end > lines.Count) throw new PluginRpcException("invalid_range", $"Lines {start}-{end} are outside `{file}` ({lines.Count} lines).");

        var store = await StoreAsync(root, cancellationToken);
        var note = store.Add(new Note
        {
            SessionId = canvas.SessionId ?? "", Review = review, Kind = KindOf(review), File = file, Side = side,
            StartLine = start, EndLine = end, Anchor = string.Join('\n', lines.Skip(start - 1).Take(end - start + 1)), Body = body,
        });
        await ChangedAsync(store, refresh: false);
        return note;
    }

    private async Task<Note> UpdateAsync(PluginCanvasContext canvas, UpdateNote request, CancellationToken cancellationToken)
    {
        var body = request.Body?.Trim() ?? "";
        if (body.Length == 0) throw Invalid("A comment needs a text.");
        var store = await StoreAsync(await RootAsync(canvas, cancellationToken), cancellationToken);
        var note = store.Change(request.Id, note => note.Body = body) ?? throw NotFound(request.Id);
        await ChangedAsync(store, refresh: false);
        return note;
    }

    private async Task<DeleteResult> DeleteAsync(PluginCanvasContext canvas, NoteId request, CancellationToken cancellationToken)
    {
        var store = await StoreAsync(await RootAsync(canvas, cancellationToken), cancellationToken);
        if (!store.Delete(request.Id)) throw NotFound(request.Id);
        await ChangedAsync(store, refresh: false);
        return new DeleteResult(request.Id, true);
    }

    private async Task<Note> ResolveAsync(PluginCanvasContext canvas, ResolveNote request, CancellationToken cancellationToken)
    {
        var store = await StoreAsync(await RootAsync(canvas, cancellationToken), cancellationToken);
        var note = store.Change(request.Id, note =>
            note.Status = request.Resolved ?? true ? "resolved" : note.SentAt is null ? "open" : "sent") ?? throw NotFound(request.Id);
        await ChangedAsync(store, refresh: false);
        return note;
    }

    // Tells the open reviews of the project that the comments (or, with refresh, the files) changed, and the buttons and status items to read again.
    private async Task ChangedAsync(NoteStore store, bool refresh)
    {
        UpdateCounts(store);
        Services.Ui.InvalidateButtons();
        foreach (var canvas in _open.Values)
        {
            if (!canvas.IsOpen) continue;
            if (canvas.ProjectId is { } id && _projectPaths.TryGetValue(id, out var path) && !SamePath(path, store.Root)) continue;
            try { await canvas.Rpc.PublishAsync("review.changed", new ReviewChanged(refresh)); }
            catch (Exception error) { Logger.Warn($"annotate: could not tell a review of a change: {error.Message}"); }
        }
    }

    private void UpdateCounts(NoteStore store)
    {
        foreach (var (session, count) in store.UnresolvedBySession()) _unresolvedBySession[session] = count;
    }

    private static PluginRpcException NotFound(string? id) => new("not_found", $"No comment has the id `{id}`.");

    private static PluginRpcException Invalid(string message) => new("invalid_input", message);

    // ---------------------------------------------------------------- Send

    private async Task<SendResult> SendAsync(PluginCanvasContext canvas, string? target, CancellationToken cancellationToken)
    {
        var view = await LoadReviewAsync(canvas, cancellationToken);
        if (view.ProjectPath is null) throw new PluginRpcException("no_project", "The review has no project.");
        var pending = view.Notes.Where(note => note.Status != "resolved").ToList();
        if (pending.Count == 0) throw new PluginRpcException("nothing_to_send", "There is no unresolved comment to send.");

        var prompt = PromptComposer.Compose(view, pending);
        var created = false;
        string sessionId;
        if (target is "new")
        {
            var create = new List<string> { "session", "create", "--project", canvas.ProjectId ?? view.ProjectPath, "--title", view.Kind == "plan" ? "Plan review notes" : "Review notes", "--no-parent" };
            if (view.Kind == "plan") create.AddRange(["--prompt-id", "plan"]);
            if (canvas.SessionId is { Length: > 0 } same) create.AddRange(["--same-model-as", same]);
            var result = await Services.Alta.InvokeAsync(create, cancellationToken: cancellationToken);
            sessionId = Field(result.TranscriptJsonl, "sessionId") ?? throw new PluginRpcException("send_failed", result.Error ?? "The new session was not created.");
            created = true;
        }
        else
        {
            sessionId = target is null or "" or "session" ? canvas.SessionId ?? throw new PluginRpcException("no_session", "The review has no session.") : target;
        }

        // `alta session send` answers when the run it starts is over, which outlasts a call of the script (30 s). The comments are
        // marked sent at once and the prompt goes out as background work of the plugin; a send that fails puts them back as they were.
        var store = await StoreAsync(view.ProjectPath, cancellationToken);
        var before = store.MarkSent(pending.Select(note => note.Id), sessionId);
        await ChangedAsync(store, refresh: false);
        Tasks.Run("annotate-send", async token =>
        {
            string? failure;
            try
            {
                var send = await Services.Alta.InvokeAsync(["session", "send", sessionId, "--stdin", "--queue-if-busy"], prompt, cancellationToken: token);
                failure = send.ExitCode == 0 ? null : send.Error ?? Field(send.TranscriptJsonl, "message") ?? $"alta session send ended with {send.ExitCode}.";
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failure = error.Message;
            }

            if (failure is null) return;
            Logger.Warn($"annotate: the review notes were not sent to {sessionId}: {failure}");
            store.Restore(before);
            await ChangedAsync(store, refresh: false);
            await Services.Ui.NotifyAsync($"The review notes were not sent: {failure}", token);
        });
        return new SendResult(pending.Count, sessionId, created, prompt);
    }

    private async Task<IReadOnlyList<SessionItem>> SessionsAsync(PluginCanvasContext canvas, CancellationToken cancellationToken)
    {
        if (canvas.ProjectId is null) return [];
        var result = await Services.Alta.InvokeAsync(["session", "list", "--project", canvas.ProjectId, "--limit", "30"], cancellationToken: cancellationToken);
        var sessions = new List<SessionItem>();
        foreach (var record in Records(result.TranscriptJsonl))
        {
            if (record.TryGetProperty("type", out var type) && type.GetString() != "alta.session.item") continue;
            if (Text(record, "sessionId") is not { } id) continue;
            sessions.Add(new SessionItem(id, Text(record, "title") ?? id, Text(record, "state"), id == canvas.SessionId));
        }

        return sessions;
    }

    // ---------------------------------------------------------------- Agent tools

    public override IEnumerable<PluginAgentToolContribution> GetAgentTools()
    {
        var listSchema = JsonDocument.Parse("""
            { "type": "object", "properties": { "all": { "type": "boolean", "description": "Include resolved comments too. Default false." } }, "additionalProperties": false }
            """).RootElement.Clone();
        yield return AgentTool.Create(new AgentToolDefinition(
            new AgentToolSpec("annotation_list", "Lists the review comments the user left on lines of the diff or of a plan in this project (open and sent ones), with their ids, file, lines, text and replies. Use it to see what the user asked you to change.", listSchema),
            async (invocation, cancellationToken) =>
            {
                var root = await SessionRootAsync(invocation.SessionId, cancellationToken);
                var store = await StoreAsync(root, cancellationToken);
                var all = invocation.Arguments.ValueKind == JsonValueKind.Object && invocation.Arguments.TryGetProperty("all", out var flag) && flag.ValueKind == JsonValueKind.True;
                var notes = store.All().Where(note => all || note.Status != "resolved").Select(note => new
                {
                    note.Id, note.Kind, note.File, note.Side, lines = Lines(note), note.Status, note.Outdated, note.Addressed, note.Body,
                    replies = note.Replies.Select(reply => new { reply.Author, reply.Body }),
                });
                return new AgentToolResult(true, [new AgentToolResultItem.Text(JsonSerializer.Serialize(new { project = root, comments = notes }, ToolJson))]);
            }));

        var replySchema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "id": { "type": "string", "description": "The id of the comment, as in [a1]." },
                "text": { "type": "string", "description": "What you changed, or why you did not." },
                "addressed": { "type": "boolean", "description": "true when the comment is addressed. Default true." }
              },
              "required": ["id", "text"],
              "additionalProperties": false
            }
            """).RootElement.Clone();
        yield return AgentTool.Create(new AgentToolDefinition(
            new AgentToolSpec("annotation_reply", "Replies to a review comment of the user (an id such as a1 from a review prompt or from annotation_list): say what you changed for it. The reply shows under the comment in the user's review. Only the user resolves comments.", replySchema),
            async (invocation, cancellationToken) =>
            {
                var arguments = invocation.Arguments;
                var id = arguments.TryGetProperty("id", out var idValue) ? idValue.GetString()?.Trim().Trim('[', ']') : null;
                var text = arguments.TryGetProperty("text", out var textValue) ? textValue.GetString()?.Trim() : null;
                var addressed = !arguments.TryGetProperty("addressed", out var flag) || flag.ValueKind != JsonValueKind.False;
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(text)) return Failed("annotation_reply needs an id and a text.");

                var root = await SessionRootAsync(invocation.SessionId, cancellationToken);
                var store = await StoreAsync(root, cancellationToken);
                var note = store.Change(id, note =>
                {
                    note.Replies.Add(new Reply { Author = "agent", Body = text, At = DateTimeOffset.UtcNow, SessionId = invocation.SessionId });
                    if (addressed) note.Addressed = true;
                });
                if (note is null) return Failed($"No comment has the id `{id}` in {root}. Call annotation_list to see the ids.");
                await ChangedAsync(store, refresh: false);
                return new AgentToolResult(true, [new AgentToolResultItem.Text($"Replied to [{id}]. The user resolves it.")]);
            }));
    }

    private static AgentToolResult Failed(string message) => new(false, [new AgentToolResultItem.Text(message)], message);

    // The project of the session that calls a tool: its catalog project, else the selected project.
    private async Task<string> SessionRootAsync(string? sessionId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(sessionId))
        {
            var info = await Services.Alta.InvokeAsync(["session", "info", sessionId], cancellationToken: cancellationToken);
            if (await ProjectPathAsync(Field(info.TranscriptJsonl, "projectId"), cancellationToken) is { } root) return root;
            if (Field(info.TranscriptJsonl, "workingDirectory") is { } directory && Directory.Exists(directory)) return directory;
        }

        return Services.Workspace.SelectedProjectPath ?? Environment.CurrentDirectory;
    }

    // ---------------------------------------------------------------- Agent events

    // A session that finished a turn may have changed files: the open reviews read the diff again.
    public override async ValueTask OnAgentEventAsync(PluginAgentEventContext context, CancellationToken cancellationToken = default)
    {
        if (context.Event is not AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle }) return;
        foreach (var canvas in _open.Values)
        {
            if (!canvas.IsOpen) continue;
            try { await canvas.Rpc.PublishAsync("review.changed", new ReviewChanged(true), cancellationToken); }
            catch (Exception error) { Logger.Warn($"annotate: could not ask a review to refresh: {error.Message}"); }
        }
    }

    // ---------------------------------------------------------------- Entry points

    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        yield return Command.Session("annotate", "Opens the review of the session's uncommitted changes: comment on lines, then send the comments to an agent.", async (context, cancellationToken) =>
        {
            await Services.Canvases.OpenAsync(CanvasId, new PluginCanvasOpenOptions { ProjectId = context.ProjectId, SessionId = context.SessionId, Key = DiffReview }, cancellationToken);
            return PluginCommandResult.Handled;
        }) with { Label = "Annotate: review the changes" };

        yield return Command.Session("annotate-plan", "Opens the review of a plan of the project (.alta/plans): comment on its lines, then send the comments to an agent.", async (context, cancellationToken) =>
        {
            var root = context.ProjectPath ?? await ProjectPathAsync(context.ProjectId, cancellationToken);
            if (root is null) return PluginCommandResult.Message("This session has no project.");
            var plans = PlanFiles(root);
            if (plans.Count == 0) return PluginCommandResult.Message("The project has no plan in .alta/plans.");
            var chosen = plans.Count == 1 ? plans[0].Path : await context.Ui.SelectAsync("Plan to review",
                [.. plans.Select(plan => new PluginSelectItem<string> { Label = plan.Title, Value = plan.Path, Description = plan.Path })], cancellationToken);
            if (chosen is null) return PluginCommandResult.Cancelled;
            await Services.Canvases.OpenAsync(CanvasId, new PluginCanvasOpenOptions { ProjectId = context.ProjectId, SessionId = context.SessionId, Key = PlanPrefix + chosen }, cancellationToken);
            return PluginCommandResult.Handled;
        }) with { Label = "Annotate: review a plan" };
    }

    public override IEnumerable<PluginUiContribution> GetUiContributions()
    {
        yield return PluginUi.Button(PluginButtonPlace.SessionMenu, "annotate-changes", icon: "message-square-text", label: "Annotate changes") with
        {
            Command = "annotate",
            GetState = button => new PluginButtonState { Badge = Unresolved(button.SessionId) },
        };

        yield return new PluginStatusContribution
        {
            Region = PluginUiRegion.SessionStatus,
            Name = "annotate-open",
            GetStatus = context =>
            {
                if (context.SessionId is null) return null;
                if (context.ProjectPath is { } root) EnsureLoaded(root);
                var count = Unresolved(context.SessionId);
                return count == 0 ? null : new PluginStatusItem { Label = "Review", Text = count == 1 ? "1 comment" : $"{count} comments", Tone = PluginStatusTone.Info, Command = "annotate" };
            },
        };
    }

    private int Unresolved(string? sessionId) => sessionId is not null && _unresolvedBySession.TryGetValue(sessionId, out var count) ? count : 0;

    // Status callbacks do no I/O: the comments of a project are read by background work the first time it is asked about.
    private void EnsureLoaded(string root)
    {
        if (_stores.ContainsKey(Normalize(root)) || !_loadingProjects.TryAdd(Normalize(root), 0)) return;
        Tasks.Run("annotate-load", async token =>
        {
            var store = await StoreAsync(root, token);
            UpdateCounts(store);
            Services.Ui.InvalidateButtons();
        });
    }

    // ---------------------------------------------------------------- Projects and storage

    private async Task<string> RootAsync(PluginCanvasContext canvas, CancellationToken cancellationToken)
        => await ProjectPathAsync(canvas.ProjectId, cancellationToken) ?? throw new PluginRpcException("no_project", "The review has no project.");

    private async ValueTask<string?> ProjectPathAsync(string? projectId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(projectId)) return null;
        if (_projectPaths.TryGetValue(projectId, out var known)) return known;
        var result = await Services.Alta.InvokeAsync(["project", "show", projectId], cancellationToken: cancellationToken);
        return Field(result.TranscriptJsonl, "projectPath") is { Length: > 0 } path ? _projectPaths[projectId] = path : null;
    }

    private async Task<NoteStore> StoreAsync(string root, CancellationToken cancellationToken)
    {
        var key = Normalize(root);
        if (_stores.TryGetValue(key, out var store)) return store;
        var file = Path.Combine(DataDirectory(root), "annotations.json");
        StoreFile? saved = null;
        if (File.Exists(file))
        {
            await using var stream = File.OpenRead(file);
            saved = await JsonSerializer.DeserializeAsync<StoreFile>(stream, FileJson, cancellationToken);
        }

        return _stores.GetOrAdd(key, _ => new NoteStore(root, file, saved ?? new StoreFile()));
    }

    // The folder of the plugin's data in a project, beside what PluginStateScope.Project uses: Services.State resolves that scope
    // from the selected project, and a review or a tool call can belong to another one, so the store writes its file itself.
    private static string DataDirectory(string root) => Path.Combine(root, ".alta", "plugin-data", "annotate");

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    private static bool SamePath(string left, string right) => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- Helpers

    internal static string Lines(Note note) => note.StartLine == note.EndLine ? $"line {note.StartLine}" : $"lines {note.StartLine}-{note.EndLine}";

    private static string FirstLine(string text) => text.Split('\n')[0].Trim() is var line && line.Length > 80 ? line[..80] + "…" : text.Split('\n')[0].Trim();

    private static T? Read<T>(JsonElement? input) => input is { ValueKind: JsonValueKind.Object } value ? value.Deserialize<T>(RpcJson) : default;

    private static JsonElement? Json<T>(T value) => JsonSerializer.SerializeToElement(value, RpcJson);

    private static IEnumerable<JsonElement> Records(string jsonl)
    {
        foreach (var line in jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonElement record;
            try { record = JsonDocument.Parse(line).RootElement.Clone(); }
            catch (JsonException) { continue; }
            if (record.ValueKind == JsonValueKind.Object) yield return record;
        }
    }

    private static string? Text(JsonElement record, string name)
        => record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Field(string jsonl, string name) => Records(jsonl).Select(record => Text(record, name)).FirstOrDefault(value => value is not null);

    // ---------------------------------------------------------------- Records

    public sealed record Empty;

    public sealed record ReviewContext(string? SessionId, string? ProjectId, string? ProjectPath, string Review, string? DataDirectory);

    public sealed record ReviewView(string Review, string Kind, string Title, string? SessionId, string? ProjectPath, string? PlanPath, string? Error,
        IReadOnlyList<DiffFile> Files, IReadOnlyList<PlanBlock> Blocks, IReadOnlyList<Note> Notes);

    public sealed record ReviewChanged(bool Refresh);

    public sealed record AddNote(string? File, int StartLine, int? EndLine, string? Side, string? Body);

    public sealed record UpdateNote(string Id, string? Body);

    public sealed record NoteId(string Id);

    public sealed record ResolveNote(string Id, bool? Resolved);

    public sealed record DeleteResult(string Id, bool Deleted);

    public sealed record SendRequest(string? Target);

    public sealed record SendResult(int Count, string SessionId, bool Created, string Prompt);

    public sealed record SessionItem(string SessionId, string Title, string? State, bool Current);

    public sealed record PlanItem(string Path, string Title);

    public sealed record OpenReview(string? Review);
}

// ---------------------------------------------------------------- Comments and their file

public sealed class Note
{
    public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Review { get; set; } = "diff";
    public string Kind { get; set; } = "diff";
    public string File { get; set; } = "";
    public string Side { get; set; } = "new";
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string Anchor { get; set; } = "";
    public string Body { get; set; } = "";
    public string Status { get; set; } = "open";
    public bool Outdated { get; set; }
    public bool Addressed { get; set; }
    public List<Reply> Replies { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string? SentTo { get; set; }

    public Note Copy()
    {
        var copy = (Note)MemberwiseClone();
        copy.Replies = [.. Replies.Select(reply => new Reply { Author = reply.Author, Body = reply.Body, At = reply.At, SessionId = reply.SessionId })];
        return copy;
    }
}

public sealed class Reply
{
    public string Author { get; set; } = "agent";
    public string Body { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string? SessionId { get; set; }
}

public sealed class StoreFile
{
    public int NextId { get; set; } = 1;
    public List<Note> Notes { get; set; } = [];
}

// The comments of one project, kept in one JSON file. Every change is written at once.
public sealed class NoteStore(string root, string file, StoreFile data)
{
    private static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Lock _gate = new();
    private readonly HashSet<string> _sessions = new(StringComparer.Ordinal);

    public string Root { get; } = root;

    public IReadOnlyList<Note> All()
    {
        lock (_gate) return [.. data.Notes.Select(note => note.Copy())];
    }

    public IReadOnlyList<Note> Notes(string? sessionId, string review)
    {
        lock (_gate) return [.. data.Notes.Where(note => note.SessionId == (sessionId ?? "") && note.Review == review).Select(note => note.Copy())];
    }

    // Every session that had comments here, with its count of unresolved ones: 0 for a session whose last comment was deleted.
    public Dictionary<string, int> UnresolvedBySession()
    {
        lock (_gate)
        {
            foreach (var note in data.Notes) _sessions.Add(note.SessionId);
            return _sessions.ToDictionary(session => session, session => data.Notes.Count(note => note.SessionId == session && note.Status != "resolved"));
        }
    }

    public Note Add(Note note)
    {
        lock (_gate)
        {
            note.Id = "a" + data.NextId++;
            note.CreatedAt = DateTimeOffset.UtcNow;
            data.Notes.Add(note);
            Save();
            return note.Copy();
        }
    }

    public Note? Change(string? id, Action<Note> change)
    {
        lock (_gate)
        {
            var note = data.Notes.Find(candidate => candidate.Id == id);
            if (note is null) return null;
            change(note);
            Save();
            return note.Copy();
        }
    }

    public bool Delete(string? id)
    {
        lock (_gate)
        {
            foreach (var note in data.Notes) _sessions.Add(note.SessionId);
            if (data.Notes.RemoveAll(note => note.Id == id) == 0) return false;
            Save();
            return true;
        }
    }

    // Marks comments sent and returns them as they were, for Restore when the send fails.
    public IReadOnlyList<Note> MarkSent(IEnumerable<string> ids, string sessionId)
    {
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            var before = new List<Note>();
            foreach (var note in data.Notes.Where(note => wanted.Contains(note.Id)))
            {
                before.Add(note.Copy());
                if (note.Status == "open") note.Status = "sent";
                note.SentAt = DateTimeOffset.UtcNow;
                note.SentTo = sessionId;
            }

            Save();
            return before;
        }
    }

    public void Restore(IReadOnlyList<Note> before)
    {
        lock (_gate)
        {
            foreach (var old in before)
            {
                var note = data.Notes.Find(candidate => candidate.Id == old.Id);
                if (note is null) continue;
                (note.Status, note.SentAt, note.SentTo) = (note.Status == "sent" ? old.Status : note.Status, old.SentAt, old.SentTo);
            }

            Save();
        }
    }

    // Moves the comments of a review with their lines: a comment whose anchor text moved follows it (the match nearest the old
    // line wins), and one whose text is gone is marked outdated. Returns the comments of the review.
    public IReadOnlyList<Note> Reanchor(string? sessionId, string review, Func<string, string, IReadOnlyList<string>?> linesOf)
    {
        lock (_gate)
        {
            var changed = false;
            var cache = new Dictionary<(string, string), IReadOnlyList<string>?>();
            foreach (var note in data.Notes.Where(note => note.SessionId == (sessionId ?? "") && note.Review == review))
            {
                if (!cache.TryGetValue((note.File, note.Side), out var lines)) cache[(note.File, note.Side)] = lines = linesOf(note.File, note.Side);
                changed |= Relocate(note, lines);
            }

            if (changed) Save();
            return [.. data.Notes.Where(note => note.SessionId == (sessionId ?? "") && note.Review == review).Select(note => note.Copy())];
        }
    }

    internal static bool Relocate(Note note, IReadOnlyList<string>? lines)
    {
        var (start, end, outdated) = (note.StartLine, note.EndLine, note.Outdated);
        if (lines is null)
        {
            note.Outdated = true;
        }
        else
        {
            var anchor = note.Anchor.Split('\n');
            bool At(int first) => first >= 1 && first + anchor.Length - 1 <= lines.Count
                && anchor.Select((text, offset) => string.Equals(lines[first - 1 + offset].TrimEnd(), text.TrimEnd(), StringComparison.Ordinal)).All(same => same);

            if (At(note.StartLine))
            {
                note.Outdated = false;
            }
            else
            {
                var best = -1;
                for (var first = 1; first + anchor.Length - 1 <= lines.Count; first++)
                    if (At(first) && (best < 0 || Math.Abs(first - note.StartLine) < Math.Abs(best - note.StartLine))) best = first;
                if (best > 0)
                {
                    note.StartLine = best;
                    note.EndLine = best + anchor.Length - 1;
                    note.Outdated = false;
                }
                else
                {
                    note.Outdated = true;
                }
            }
        }

        return start != note.StartLine || end != note.EndLine || outdated != note.Outdated;
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".tmp";
        System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(data, FileJson));
        System.IO.File.Move(temporary, file, overwrite: true);
    }
}

// ---------------------------------------------------------------- Diff

public sealed record DiffLine(string K, int? O, int? N, string T);

public sealed record DiffHunk(string Header, IReadOnlyList<DiffLine> Lines);

public sealed record DiffFile(string Path, string? OldPath, string Status, bool Binary, bool TooLarge, int Additions, int Deletions, IReadOnlyList<DiffHunk> Hunks);

public sealed record ReviewContent(string Kind, string? PlanPath, string? Error, IReadOnlyList<DiffFile> Files, IReadOnlyList<PlanBlock> Blocks, Func<string, string, IReadOnlyList<string>?> LinesOf)
{
    public static ReviewContent Failed(string kind, string? planPath, string error) => new(kind, planPath, error, [], [], (_, _) => null);
}

public static class DiffSource
{
    private const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
    private const int MaxFileLines = 3000;
    private const int MaxFileBytes = 512 * 1024;
    private const int Budget = 700_000;

    // The uncommitted state against HEAD (staged and not), plus the untracked files shown as additions.
    public static async Task<ReviewContent> LoadAsync(string root, CancellationToken cancellationToken)
    {
        var inside = await GitAsync(root, cancellationToken, "rev-parse", "--is-inside-work-tree");
        if (inside.Exit != 0) return ReviewContent.Failed("diff", null, "The project is not a git repository.");
        var hasHead = (await GitAsync(root, cancellationToken, "rev-parse", "--verify", "-q", "HEAD")).Exit == 0;
        var diff = await GitAsync(root, cancellationToken, "-c", "core.quotepath=false", "diff", "--no-color", "--no-ext-diff", "-U3", hasHead ? "HEAD" : EmptyTree, "--");
        if (diff.Exit != 0) return ReviewContent.Failed("diff", null, "git diff failed: " + diff.Err.Trim());

        var files = DiffParser.Parse(diff.Out);
        var untracked = await GitAsync(root, cancellationToken, "-c", "core.quotepath=false", "ls-files", "--others", "--exclude-standard", "-z");
        foreach (var path in untracked.Out.Split('\0', StringSplitOptions.RemoveEmptyEntries)) files.Add(Untracked(root, path));
        files = Limit(files);

        var oldCache = new ConcurrentDictionary<string, IReadOnlyList<string>?>(StringComparer.Ordinal);
        IReadOnlyList<string>? LinesOf(string file, string side)
        {
            if (side == "old")
                return hasHead ? oldCache.GetOrAdd(file, path => Show(root, path)) : null;
            var full = Path.Combine(root, file);
            return File.Exists(full) ? SplitLines(File.ReadAllText(full)) : null;
        }

        return new ReviewContent("diff", null, null, files, [], LinesOf);
    }

    private static IReadOnlyList<string>? Show(string root, string path)
    {
        var shown = GitAsync(root, CancellationToken.None, "show", "HEAD:" + path).GetAwaiter().GetResult();
        return shown.Exit == 0 ? SplitLines(shown.Out) : null;
    }

    internal static List<string> SplitLines(string text)
    {
        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static DiffFile Untracked(string root, string path)
    {
        var full = Path.Combine(root, path);
        var info = new FileInfo(full);
        if (!info.Exists) return new DiffFile(path, null, "untracked", false, false, 0, 0, []);
        if (info.Length > MaxFileBytes) return new DiffFile(path, null, "untracked", false, true, 0, 0, []);
        var bytes = File.ReadAllBytes(full);
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).IndexOf((byte)0) >= 0) return new DiffFile(path, null, "untracked", true, false, 0, 0, []);
        var lines = SplitLines(Encoding.UTF8.GetString(bytes));
        var hunk = new DiffHunk($"@@ -0,0 +1,{lines.Count} @@", [.. lines.Select((text, index) => new DiffLine("+", null, index + 1, text))]);
        return new DiffFile(path, null, "untracked", false, false, lines.Count, 0, lines.Count == 0 ? [] : [hunk]);
    }

    // An RPC frame is at most 1 MiB: very long files, and files past the budget, are listed without their lines.
    private static List<DiffFile> Limit(List<DiffFile> files)
    {
        var used = 0;
        var limited = new List<DiffFile>(files.Count);
        foreach (var file in files)
        {
            var count = file.Hunks.Sum(hunk => hunk.Lines.Count);
            var size = file.Hunks.Sum(hunk => hunk.Lines.Sum(line => line.T.Length + 32));
            if (count > MaxFileLines || used + size > Budget) { limited.Add(file with { TooLarge = true, Hunks = [] }); continue; }
            used += size;
            limited.Add(file);
        }

        return limited;
    }

    internal static async Task<(int Exit, string Out, string Err)> GitAsync(string root, CancellationToken cancellationToken, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        using var registration = cancellationToken.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await output, await error);
    }
}

// Reads `git diff` output: files, hunks and lines with their old and new numbers.
public static class DiffParser
{
    private static readonly Regex HunkHeader = new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.Compiled);

    public static List<DiffFile> Parse(string text)
    {
        var files = new List<DiffFile>();
        string? path = null, oldPath = null, status = null;
        var binary = false;
        List<DiffHunk>? hunks = null;
        List<DiffLine>? lines = null;
        string header = "";
        int oldNo = 0, newNo = 0;

        void EndHunk()
        {
            if (lines is not null) hunks!.Add(new DiffHunk(header, lines));
            lines = null;
        }

        void EndFile()
        {
            EndHunk();
            if (path is null || hunks is null) return;
            var all = hunks.SelectMany(hunk => hunk.Lines).ToList();
            files.Add(new DiffFile(path, oldPath != path ? oldPath : null, status ?? "modified", binary, false, all.Count(line => line.K == "+"), all.Count(line => line.K == "-"), hunks));
            path = null;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                EndFile();
                (oldPath, path) = GitHeaderPaths(line);
                status = null;
                binary = false;
                hunks = [];
                continue;
            }

            if (hunks is null) continue;
            var hunk = HunkHeader.Match(line);
            if (hunk.Success)
            {
                EndHunk();
                header = line;
                oldNo = int.Parse(hunk.Groups[1].Value);
                newNo = int.Parse(hunk.Groups[3].Value);
                lines = [];
                continue;
            }

            if (lines is null)
            {
                if (line.StartsWith("new file mode", StringComparison.Ordinal)) status = "added";
                else if (line.StartsWith("deleted file mode", StringComparison.Ordinal)) status = "deleted";
                else if (line.StartsWith("rename from ", StringComparison.Ordinal)) { oldPath = Unquote(line[12..]); status = "renamed"; }
                else if (line.StartsWith("rename to ", StringComparison.Ordinal)) path = Unquote(line[10..]);
                else if (line.StartsWith("Binary files ", StringComparison.Ordinal)) binary = true;
                else if (line.StartsWith("--- ", StringComparison.Ordinal) && StripPrefix(line[4..]) is { } from) oldPath = from;
                else if (line.StartsWith("+++ ", StringComparison.Ordinal) && StripPrefix(line[4..]) is { } to) path = to;
                continue;
            }

            if (line.Length == 0) continue;
            switch (line[0])
            {
                case ' ': lines.Add(new DiffLine(" ", oldNo++, newNo++, line[1..])); break;
                case '+': lines.Add(new DiffLine("+", null, newNo++, line[1..])); break;
                case '-': lines.Add(new DiffLine("-", oldNo++, null, line[1..])); break;
            }
        }

        EndFile();
        return files;
    }

    // `diff --git a/x b/x`: the two paths, used until the --- and +++ lines say better (binary files and pure renames have none).
    private static (string? Old, string? New) GitHeaderPaths(string line)
    {
        var rest = line["diff --git ".Length..];
        if (rest.StartsWith('"'))
        {
            var parts = Regex.Matches(rest, "\"(?:[^\"\\\\]|\\\\.)*\"|\\S+").Select(match => match.Value).ToList();
            return parts.Count >= 2 ? (StripPrefix(parts[0]), StripPrefix(parts[1])) : (null, null);
        }

        var middle = rest.Length / 2;
        if (rest.Length % 2 == 1 && rest[middle] == ' ' && rest[..middle][2..] == rest[(middle + 1)..][2..]) return (StripPrefix(rest[..middle]), StripPrefix(rest[(middle + 1)..]));
        var split = rest.LastIndexOf(" b/", StringComparison.Ordinal);
        return split > 0 ? (StripPrefix(rest[..split]), StripPrefix(rest[(split + 1)..])) : (null, null);
    }

    private static string? StripPrefix(string value)
    {
        value = Unquote(value.TrimEnd('\t'));
        if (value == "/dev/null") return null;
        return value.Length > 2 && (value.StartsWith("a/", StringComparison.Ordinal) || value.StartsWith("b/", StringComparison.Ordinal)) ? value[2..] : value;
    }

    private static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"') return value;
        var bytes = new List<byte>();
        for (var index = 1; index < value.Length - 1; index++)
        {
            var c = value[index];
            if (c != '\\') { bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString())); continue; }
            var next = value[++index];
            if (next is >= '0' and <= '7' && index + 2 < value.Length - 1)
            {
                bytes.Add(Convert.ToByte(value.Substring(index, 3), 8));
                index += 2;
                continue;
            }

            bytes.Add((byte)(next switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => next }));
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }
}

// ---------------------------------------------------------------- Plan

public sealed record PlanBlock(int Start, int End, string Kind, string Text, string? Section);

// Splits a Markdown file into blocks (front matter, heading, paragraph, list item, fenced code, table, quote) with their 1-based line range.
public static class PlanSplitter
{
    private static readonly Regex Heading = new(@"^#{1,6}\s", RegexOptions.Compiled);
    private static readonly Regex Fence = new(@"^\s*(`{3,}|~{3,})", RegexOptions.Compiled);
    private static readonly Regex Item = new(@"^\s*([-*+]|\d+[.)])\s+", RegexOptions.Compiled);
    private static readonly Regex Table = new(@"^\s*\|", RegexOptions.Compiled);
    private static readonly Regex Quote = new(@"^\s*>", RegexOptions.Compiled);
    private static readonly Regex Rule = new(@"^\s*([-*_])(\s*\1){2,}\s*$", RegexOptions.Compiled);

    public static IReadOnlyList<PlanBlock> Split(IReadOnlyList<string> lines)
    {
        var blocks = new List<PlanBlock>();
        string? section = null;
        var index = 0;

        void Add(int from, int to, string kind)
        {
            var slice = lines.Skip(from).Take(to - from + 1).ToList();
            blocks.Add(new PlanBlock(from + 1, to + 1, kind, kind is "code" or "frontmatter" ? string.Join('\n', slice) : Dedent(slice), section));
        }

        if (lines.Count > 0 && lines[0].Trim() == "---")
        {
            var close = Enumerable.Range(1, Math.Max(0, lines.Count - 1)).FirstOrDefault(at => lines[at].Trim() == "---", -1);
            if (close > 0) { Add(0, close, "frontmatter"); index = close + 1; }
        }

        while (index < lines.Count)
        {
            var line = lines[index];
            if (line.Trim().Length == 0) { index++; continue; }
            var start = index;
            var fence = Fence.Match(line);
            if (fence.Success)
            {
                var marker = fence.Groups[1].Value;
                index++;
                while (index < lines.Count && !lines[index].TrimStart().StartsWith(marker, StringComparison.Ordinal)) index++;
                Add(start, Math.Min(index, lines.Count - 1), "code");
                index++;
            }
            else if (Heading.IsMatch(line))
            {
                section = line.Trim();
                Add(start, start, "heading");
                index++;
            }
            else if (Table.IsMatch(line))
            {
                while (index + 1 < lines.Count && Table.IsMatch(lines[index + 1])) index++;
                Add(start, index++, "table");
            }
            else if (Quote.IsMatch(line))
            {
                while (index + 1 < lines.Count && Quote.IsMatch(lines[index + 1])) index++;
                Add(start, index++, "quote");
            }
            else if (Rule.IsMatch(line))
            {
                Add(start, index++, "rule");
            }
            else if (Item.IsMatch(line))
            {
                while (index + 1 < lines.Count && Continues(lines[index + 1])) index++;
                Add(start, index++, "item");
            }
            else
            {
                while (index + 1 < lines.Count && Continues(lines[index + 1])) index++;
                Add(start, index++, "paragraph");
            }
        }

        return blocks;
    }

    private static bool Continues(string line)
        => line.Trim().Length > 0 && !Item.IsMatch(line) && !Heading.IsMatch(line) && !Fence.IsMatch(line) && !Table.IsMatch(line) && !Quote.IsMatch(line);

    private static string Dedent(List<string> lines)
    {
        var indent = lines.Where(line => line.Trim().Length > 0).Select(line => line.Length - line.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join('\n', lines.Select(line => line.Length >= indent ? line[indent..] : line.TrimStart()));
    }

    // The heading a line is under, for the prompt: the nearest heading above it.
    public static string? SectionOf(IReadOnlyList<PlanBlock> blocks, int line)
        => blocks.LastOrDefault(block => block.Start <= line) is { } block ? block.Kind == "heading" ? block.Text.Trim() : block.Section : null;
}

// ---------------------------------------------------------------- Prompt

// Writes the comments of a review as one line-anchored prompt, file by file.
public static class PromptComposer
{
    public static string Compose(AnnotatePlugin.ReviewView view, IReadOnlyList<Note> notes)
    {
        var text = new StringBuilder();
        var what = view.Kind == "plan" ? "your plan" : "your changes";
        text.Append($"Review notes on {what}: {(notes.Count == 1 ? "1 comment" : $"{notes.Count} comments")}. ");
        text.Append(view.Kind == "plan"
            ? "Revise the plan file for each one; when done, call `annotation_reply` with its id and what you changed.\n"
            : "Address each one; when done, call `annotation_reply` with its id and what you changed.\n");

        foreach (var group in notes.GroupBy(note => note.File).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            text.Append($"\n## {group.Key}\n");
            foreach (var note in group.OrderBy(note => note.StartLine))
            {
                var lines = AnnotatePlugin.Lines(note);
                if (note.Kind == "plan")
                {
                    var section = PlanSplitter.SectionOf(view.Blocks, note.StartLine);
                    text.Append($"\n### [{note.Id}] {lines}{(section is null ? "" : $" — \"{section}\"")}{Flags(note)}\n");
                    foreach (var line in note.Anchor.Split('\n')) text.Append(line.Length == 0 ? ">\n" : $"> {line}\n");
                }
                else
                {
                    text.Append($"\n### [{note.Id}] {lines} ({note.Side}){Flags(note)}\n");
                    var snippet = Snippet(view, note);
                    var fence = new string('`', Math.Max(3, LongestRun(snippet, '`') + 1));
                    text.Append($"{fence}diff\n{snippet}\n{fence}\n");
                }

                text.Append(note.Body.Trim()).Append('\n');
                foreach (var reply in note.Replies) text.Append($"> {(reply.Author == "agent" ? "Your earlier reply" : "Reply")}: {reply.Body.Replace("\n", " ")}\n");
            }
        }

        return text.ToString();
    }

    private static string Flags(Note note)
        => (note.Outdated ? " — outdated: these lines changed since the comment" : "") + (note.Status == "sent" ? " — sent before, still open" : "");

    // The commented lines as they stand in the diff, with their +, - or space; the anchor text when they are no longer in it.
    private static string Snippet(AnnotatePlugin.ReviewView view, Note note)
    {
        if (!note.Outdated && view.Files.FirstOrDefault(file => file.Path == note.File) is { } file)
        {
            var rows = file.Hunks.SelectMany(hunk => hunk.Lines)
                .Where(line => (note.Side == "old" ? line.O : line.N) is { } number && number >= note.StartLine && number <= note.EndLine)
                .Select(line => line.K + line.T).ToList();
            if (rows.Count == note.EndLine - note.StartLine + 1) return string.Join('\n', rows);
        }

        return string.Join('\n', note.Anchor.Split('\n').Select(line => " " + line));
    }

    private static int LongestRun(string text, char c)
    {
        int best = 0, run = 0;
        foreach (var ch in text) { run = ch == c ? run + 1 : 0; best = Math.Max(best, run); }
        return best;
    }
}
