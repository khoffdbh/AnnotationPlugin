---
title: "Annotate plugin: hand over to the built-in annotations of CodeAlta, then retire"
status: draft
created: 2026-10-11
summary: "Make the annotate plugin step aside when CodeAlta has built-in annotations: detect the feature, drop the agent tools, freeze the plugin's comments file so the one-time import stays the single truth, keep the old reviews readable, then document and retire the plugin."
---

# Annotate plugin: handover and retirement

CodeAlta builds this plugin's workflow in natively: spec `CodeAlta/doc/specs/annotations.md`, plan `CodeAlta/.alta/plans/2026-10-11-native-annotations.md`. This plan covers what the plugin itself must do so that the two never run as rival stores, and how it is retired.

## Why the plugin must change

The built-in feature imports `<checkout>/.alta/plugin-data/annotate/annotations.json` **once**, into `<checkout>/.alta/annotations/comments.json` (spec §5.3):
- ids and `nextId` are kept, so the same id (`a3`) then exists in both files;
- every imported comment becomes a `review_annotation` (only the user resolves), with `author = user`;
- the import does not run again while `comments.json` exists.

If the plugin keeps running unchanged next to the built-in feature:
1. **Wrong-store replies.** An agent that reads a native prompt about `[a3]` may call the plugin's `annotation_reply` tool instead of `alta annotation reply`, and the reply lands in the plugin file, invisible in the native review.
2. **Lost comments.** Comments written in the plugin's canvas after the import are never imported.
3. **Two of everything.** Two "Annotate changes" entries in the session menu, two review badges, two "Review · N comments" status items.

## Facts about the plugin API (CodeAlta source)

- Contributions are collected **once**, right after `InitializeAsync` (`CodeAlta/src/CodeAlta.Plugins/PluginRuntimeLifecycle.cs:246-247`). A decision taken in `InitializeAsync` therefore shapes `GetAgentTools`, `GetCommands` and `GetUiContributions`.
- The plugin sees the host version: `PluginHostInfo.Version` (`CodeAlta.Plugins.Abstractions/PluginContexts.cs:36`, exposed as `Host` at `:74`).
- `PluginMetadata.MaxCodeAltaVersion` is documented as a *hint* and nothing in CodeAlta enforces it (`PluginMetadata.cs:81-83`), so it cannot retire the plugin by itself.
- `Services.Alta.InvokeAsync` runs `alta` commands (already used for `session send`, `project show`).

## Steps

Before starting: this repository has uncommitted work (`plugin.cs`, `review-content.cs`, `ui/review.js`, `README.md`, the 2026-10-10 plan). Commit or finish it first, so the handover is its own change.

- [ ] **1. Detect the built-in feature.**
  - In `InitializeAsync`, set a `_builtIn` flag when the host has the `annotation` alta group: probe `Services.Alta.InvokeAsync(["annotation", "--help"])` and treat exit code 0 as present.
  - **Spike first:** confirm `Services.Alta` answers during `InitializeAsync`. If it does not, compare `Host.Version` with the first CodeAlta release that ships annotations (to fill in when it is released), and keep the probe for the canvas, which runs later.
  - Log one line with the outcome.
- [ ] **2. Hand-over mode when the feature is built in.**
  - `GetAgentTools`: no `annotation_list`, no `annotation_reply`. This removes the wrong-store replies.
  - `GetUiContributions`: no session-menu button, no status item.
  - `GetCommands`: keep `annotate`, `annotate-plan`, `annotate-file`, but they answer `PluginCommandResult.Message(…)`: "Annotations are built into CodeAlta: use **Annotate changes** or **Annotate a plan**, or comment in the editor's Preview." Check that the palette shows no duplicate of the native commands' labels; rename the plugin's labels ("Annotate (plugin): …") if it does.
  - The canvas still opens, **read-only**: a banner at the top says the comments were imported into CodeAlta's built-in review (`.alta/annotations/comments.json`) and that this view is kept to read the old ones. `ui/review.js` hides **+**, Edit, Resolve and Send; the canvas actions `add`, `send`, `resolve` and the RPC writers refuse with `PluginRpcException("built_in", …)`.
  - `OnAgentEventAsync` keeps refreshing open canvases (read-only views still follow file changes).
- [ ] **3. Freeze the comments file.**
  - In hand-over mode `NoteStore` never writes: `Save` is not called; `Reanchor` computes positions in memory only. The plugin file stays exactly what the import read.
  - When `.alta/annotations/comments.json` exists next to the plugin file, the banner says "Imported into CodeAlta"; when it does not (the project was not opened in the new version yet), it says the import runs the next time the project's review is shown.
- [ ] **4. Verify against the developer instance of CodeAlta** (with step 12 of the CodeAlta plan).
  1. In a test project with plugin comments (open, sent, resolved, on a diff, a plan and a Markdown file), start the CodeAlta developer instance.
  2. Check the native review: every comment imported once, as `review_annotation`, `author = user`, resolved ones with `resolvedBy = user`, ids kept.
  3. Check the plugin: `alta plugin status annotate` shows no agent tools; no session-menu button or status item; the commands answer the message; the canvas is read-only and refuses writes; the plugin file's bytes are unchanged after browsing the canvas.
  4. Delete `comments.json`, show the review again: the import runs again (spec Q10).
  5. In CodeAlta 1.6.0 (no built-in feature), the plugin works as before.
- [ ] **5. Document.**
  - `README.md`: a notice at the top: superseded by CodeAlta's built-in annotations from version `<x>`; what happens to existing comments (imported once, as review annotations); how to remove the plugin (delete the `~/.alta/plugins/annotate` junction, then `alta plugin refresh`).
  - Mark the 2026-10-10 plan as superseded by this one.
- [ ] **6. Retire.**
  - One CodeAlta release after the built-in feature ships: delete the plugin from `~/.alta/plugins/`, archive this repository (README notice kept).

## Out of scope

- Porting the spec's new semantics (kinds, per-checkout storage, author) into the plugin. The plugin only steps aside.
- Any change in CodeAlta for the handover (D5 of the CodeAlta plan: no notice from CodeAlta about the plugin).
- Enforcing `MaxCodeAltaVersion` in CodeAlta: it is a hint by design.
