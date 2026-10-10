---
title: "Annotate plugin: review AI diffs and plans line by line, send the notes to the agent"
status: done
created: 2026-10-10
summary: "A CodeAlta plugin modelled on Orca's Annotate AI Diff: comment on lines of a session's code diff or of a plan file, then send all open comments to an agent as one line-anchored prompt."
---

# Annotate plugin

## Goal

Recreate [Orca's Annotate AI Diff](https://www.onorca.dev/docs/review/annotate-ai-diff) as a CodeAlta Desktop plugin, for **code diffs** and **plans** (`.alta/plans/*.md`):

1. Hover a line, click **+** (or press `c`), drag or `Shift`-click to cover a range, write a Markdown comment. `Enter`/`Ctrl+Enter` saves, `Shift+Enter` adds a new line, `Esc` cancels.
2. Comments stay pinned to their lines. When the agent changes the file, they move with the code.
3. **Send to agent** combines all open comments into one line-anchored prompt and sends it to the session you pick (by default the session the review belongs to), or to a new session.
4. Comments stay visible after the agent's revision. **Resolve** collapses one. Unresolved comments are included the next time you send.

The agent is the reader: it gets the batch as a prompt. It can also list the comments and reply to them through two agent tools.

## Constraints found in the plugin API (CodeAlta 1.6.0)

- The window's diff viewer and code editor are **not open to plugins**, and an HTML fragment runs no script. So the review is a **canvas** (a tab the plugin provides) drawn by a React module (`PluginScript.File("ui/review.js")`) that renders the diff itself with Blueprint. State lives in C#, and the script reaches it with `canvas.Rpc` (model: `samples/canvas-board`).
- `codealta` exports `Markdown`, `Code`, `FileLink` and `Icon`. There is no diff component, so lines are rendered as a table. Syntax highlighting is out of scope for v1.
- `Services.Sessions.SendPromptAsync` targets only the *selected* session. To send to a specific session, use `Services.Alta.InvokeAsync(["session","send",id,"--stdin","--queue-if-busy"], stdin: prompt)`.
- `PluginCanvasContext` has `ProjectId` but not the project's path. The path has to be resolved (Step 1 spike: `Services.Workspace.ProjectPaths` or `alta project list`). **Spike result:** `alta project show <projectId>` returns `projectPath`; the plugin caches it per project id. The plugin is a single-file build, so everything lives in `plugin.cs` plus `ui/review.js`.
- `Services.State` with `PluginStateScope.Project` keeps JSON in `<project>/.alta/plugin-data/`.

## Design

| Part | Decision |
|---|---|
| Plugin id / location | `annotate`, a global plugin (`~/.alta/plugins/annotate/`), so it works in every project. Source of truth: `AnnotationPlugin/annotate/` in this repo, linked with a directory junction (fallback: develop in place and copy back). |
| Canvas | `review`, `Scope = Session`. Key `diff` for the code review, `plan:<relative path>` for a plan. The session is the default target for **Send**. |
| Diff source | `git diff --no-color -U3 HEAD` plus untracked files (`git ls-files --others --exclude-standard`) shown as additions, run in the project path. Parsed into C# records `ReviewFile → Hunk → Line(oldNo, newNo, kind, text)`. Binary and very large files are listed but not rendered. |
| Plan source | `.alta/plans/*.md` of the project. The file is split into Markdown **blocks** (heading, paragraph, list item, fenced code, table), each with its source line range. Each block is rendered with `Markdown`, and its gutter **+** comments on that line range. |
| Annotation record | `Id, Kind(diff/plan), File, Side(new/old), StartLine, EndLine, Anchor(text of the lines), Body, Status(open/sent/resolved/outdated), Replies[{Author(user/agent), Body, At}], CreatedAt, SentAt` |
| Storage | `Services.State` (Project scope), key `annotations`: reviews keyed by session id. |
| Re-anchoring | On refresh, find `Anchor` in the new content, picking the match nearest the old line. If it isn't found, mark the comment `outdated`: it is listed in an "Outdated" section and can still be sent. |
| Refresh | A **Refresh** button. Also automatic when the tab becomes visible and when the review's session finishes a turn (`OnAgentEventAsync` → `PublishAsync("review.changed")`). |
| Send | Prompt below. Target menu (`PopoverNext` + `Menu`): this session, the other sessions of the project (`alta session list --project`), or **New session** (`alta session create` then `send`). Sent comments get status `sent`. |
| Agent tools | `annotation_list` (open and sent comments of the calling session's project) and `annotation_reply` (`id`, `text`, `addressed: bool`). A reply shows in the comment's thread. Only the user resolves. |
| Entry points | Command `/annotate` (session command, opens the diff review), `/annotate-plan` (pick a plan, open its review), a **SessionMenu** button "Annotate changes", and a status item showing the number of open comments (click opens the review). The shortcut is unbound by default, as in Orca. |
| Agent / test access | Canvas actions `add`, `list`, `send` and `resolve` with JSON schemas, plus `Describe`. Agents can use them through `alta canvas invoke`, and they make each step verifiable without the UI. |

Prompt sent to the agent:

````markdown
Review notes on your changes: 3 comments. Address each one; when done, call `annotation_reply` with its id and what you changed.

## src/Foo.cs

### [a1] lines 42-45 (new)
```diff
+    var x = Compute();
+    return x;
```
Don't swallow the exception here; let it surface.

## .alta/plans/2026-10-10-x.md

### [a3] lines 12-14 — "## Step 2"
> - [ ] Add the cache
This step must come after the migration.
````

## Steps

- [x] **1. Scaffold and spike.** Run `git init` in `AnnotationPlugin/`, add a `.gitignore`, and create a work branch (`feat/annotate-plugin`). `alta plugin create annotate`. Move the source to `AnnotationPlugin/annotate/` and junction `~/.alta/plugins/annotate` to it, then confirm `alta plugin reload annotate` follows the junction. Add the `review` canvas with a hello `ui/review.js` and one RPC (`review.context`) that returns the session id and the resolved project path. Verify with `alta canvas open review`, `alta ui activate` and a snapshot. **Done when:** the tab shows the right project path for the session.
- [x] **2. Read-only diff.** Add the git runner, the unified-diff parser and untracked files. Add RPC `review.get` (files, hunks, lines) and a stream `review.watch`. The script shows a file list on the left, a diff table on the right (old/new line numbers, +/- colours from `alta.theme`) and file names as `FileLink`s. **Done when:** a project with edits and a new file renders correctly. Test the parser on a sample diff through `alta canvas show`.
- [x] **3. Annotations.** Add the record, the state store and RPCs `note.add/update/delete/resolve`. In the UI: gutter **+** on hover, drag or `Shift`-click for a range limited to one hunk, inline `TextArea` editor with the Orca keys, `c` on the focused line, and thread cards under the last line of the range with Edit / Delete / Resolve. Add the canvas actions `add`, `list` and `resolve`. **Done when:** comments survive `alta plugin reload` and a CodeAlta restart.
- [x] **4. Re-anchoring.** Relocate comments by `Anchor` on refresh and mark the ones not found as `outdated`. Refresh automatically when the session goes idle. **Done when:** after inserting lines above a commented line, the comment moves with the line; after deleting the line, it shows as outdated.
- [x] **5. Send to agent.** Add the prompt composer, the target menu (this session / other sessions / new session) and the queue-if-busy send. Mark comments as `sent`, and add a toolbar badge with the open count and the status item. Add the canvas action `send`. **Done when:** the target session receives the prompt above, and a second send includes only unresolved comments.
- [x] **6. Agent tools.** Add `annotation_list` and `annotation_reply`, and show replies in threads (live through `review.changed`). **Done when:** an agent run started by **Send** replies to each id and the replies appear in the open tab.
- [x] **7. Plan annotation.** Add the plan picker (`/annotate-plan` and a selector in the canvas toolbar), the Markdown block splitter with line ranges, the block gutter, and `Kind = plan` comments that reuse steps 3–6. **Done when:** comments on a plan file are sent to its session, and the plan agent revises the file.
- [x] **8. Entry points and docs.** Add `/annotate`, the SessionMenu button, the status item and `annotate/README.md` (install, usage, keys). Run one full manual pass in the window: edit → annotate → send → agent replies → resolve.

## Deviations found while implementing

- **Storage path.** `Services.State` resolves `PluginStateScope.Project` from the *selected* project, and its `GetDirectory` fails in a canvas `Describe` call. A review or a tool call can belong to another project, so the store writes `<project>/.alta/plugin-data/annotate/annotations.json` itself (same folder convention as Decision 3).
- **No `review.watch` stream.** The script listens to the `review.changed` event (`refresh: true` re-reads the diff, `false` re-reads only the comments) and reads with calls. That covers every live update the plan needs.
- **Outdated is a flag, not a status.** `Outdated` sits beside `Status`, so a comment keeps `open`/`sent` while its lines are gone and re-attaches when the text comes back.
- **Send runs in the background.** `alta session send` returns only after the run it starts ends (a 26 s run took ~55 s), past the 30 s limit of a script call. The comments are marked `sent` at once, the prompt goes out through `Tasks.Run`, and a failed send restores their previous state and shows a notification.
- **Plan picker.** The toolbar selector opens the chosen review (`review.open`) in its own tab, since the canvas key decides which review a tab shows. **New session** from a plan review starts the session with `--prompt-id plan`.
- **Comments on files without changes.** After a commit or a revert, a file leaves the diff while its comments stay unresolved. The file list gets a **No longer changed** entry that lists them, so they can still be read and resolved.
- **Step 3 restart check.** Persistence was verified across `alta plugin reload` (in-memory state cleared, comments re-read from the file). A full CodeAlta restart cannot be run from inside the session; it reads the same file.

## Verification

- Each step: `alta plugin build annotate` → `alta plugin reload annotate` → `alta plugin status annotate` shows no callback errors.
- Behaviour without the UI: `alta canvas show review` and `alta canvas invoke review add|list|send --stdin`.
- UI: `alta ui activate`, `take_snapshot` / `take_screenshot` of the tab, and `evaluate_script` for gutter clicks.

## Out of scope for v1

- Syntax highlighting in the diff.
- Comparing against a base branch instead of the uncommitted state (`HEAD`). A base selector is the first follow-up.
- Comments on the session's per-turn diffs, as opposed to the working tree.
- CodeAlta TUI (canvases are Desktop only). The plugin sets `Frontends = PluginFrontends.Desktop`.

## Decisions (2026-10-10)

1. **Diff base:** uncommitted changes against `HEAD` are enough for v1. A base-branch selector stays a follow-up.
2. **Source location:** `~/.alta/plugins/annotate` is a directory junction to `AnnotationPlugin/annotate/`. Step 1 runs `git init` in `AnnotationPlugin/`. The `.gitignore` covers the files CodeAlta generates in the plugin folder (`bin/`, `obj/`, and CodeAlta's own `.gitignore` and `Directory.*` files when they turn up there). Commits go on a work branch, not on the default branch.
3. **Storage:** per project, `Services.State` with `PluginStateScope.Project` (`<project>/.alta/plugin-data/`).
