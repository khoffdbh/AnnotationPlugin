---
title: "Drive an agent session by annotations: native line comments on the Changes diff and on Markdown files"
status: draft
created: 2026-10-11
summary: "Implementation plan for doc/specs/annotations.md: comments of two kinds (annotation, review_annotation) on the Changes tab's uncommitted diff and on Markdown files in the code editor (Preview and Text view) and Work items, stored per checkout, sent to a session as one prompt, and answered or resolved by agents through `alta annotation`."
---

# Drive an agent session by annotations

Spec: [`doc/specs/annotations.md`](../../doc/specs/annotations.md). The spec is the contract: behaviour, storage, prompt, `alta` and RPC. This plan holds the steps. When a step finds that the spec is wrong or incomplete, fix the spec first, in the same commit.

The external plugin is handed over and retired by its own plan: `../AnnotationPlugin/.alta/plans/2026-10-11-annotate-handover.md`.

## Goal

Build the annotate plugin's workflow (`../AnnotationPlugin/annotate/`) into CodeAlta Desktop, with the changes the spec settled:
- **Two kinds** of comment: `annotation` (default, the agent resolves it with its reply) and `review_annotation` (only the user resolves). The kind can be switched until the first send.
- **Two surfaces**: the **Changes** tab, **Uncommitted changes** scope (Monaco diff, both sides, one-file and All-files views); and Markdown files in the code editor (**Preview** by blocks, **Text** view by lines) and in the plan details of **Work items** (plans not done).
- **Per checkout**: the project folder and each worktree keep their own comments, in `<checkout>/.alta/annotations/comments.json`.
- **Agents** use `alta annotation` (desktop and TUI), and may write comments of their own.

## Decisions (from the spec)

| # | Decision | Spec |
|---|---|---|
| D1 | A native feature: Catalog service, `alta` group, desktop RPC, frontend in `frontend/src/comments/`. Not a bundled plugin, which would stay a canvas with untyped RPC and no access to the Changes tab or the editor. | — |
| D2 | Every comment belongs to one **checkout** (project folder or worktree). Reviews are `diff` or `md:<checkout-relative path>` within it. Each comment keeps `TargetSessionId`, the default Send target. | §2, Q2 |
| D3 | One file per checkout: `<checkout>/.alta/annotations/comments.json`. Ids `a<n>` are unique per checkout, so every command and RPC call names its checkout. The plugin file `<checkout>/.alta/plugin-data/annotate/annotations.json` is imported once (`importedPlugin`). | §5, Q2, Q10 |
| D4 | Agents use `alta annotation …` only; no native agent tools, so no schema is added to every session and no name collides with the plugin's `annotation_list`/`annotation_reply` (`PluginOrchestrationBridge.MergeTools` does not dedupe). | §8 |
| D5 | The `annotation` alta root is reserved for built-ins (`PluginAltaCommandContributor.ReservedRootCommands`). CodeAlta shows no notice about the plugin; the plugin detects the built-in feature itself (plugin plan). | §8 |
| D6 | v1: Uncommitted scope only; Text-view comments on Markdown files only. In inline diff mode (< 780 px), `old`-side comments are listed in the review summary instead of drawn. | §1, §4.1, Q11 |
| D7 | Preview comments cover whole blocks (`token.map`); Text-view and `alta` ranges are stored as given and mark every block they touch in Preview. | §4.2, Q3 |
| D8 | Kinds: an agent's addressed reply resolves an `annotation` at once; a `review_annotation` is resolved and any comment reopened by the user only. The kind is locked after the first send. New comments are `annotation`; no setting. | §3.1, Q8, Q12 |
| D9 | Every comment has `author` (`user`/`agent`) and `authorSessionId`. | §3.4, Q4 |
| D10 | The code editor reads the project folder only (`editor/ProjectEditor.tsx:246` passes `worktree: null`), so editor and Work items reviews are of the project folder's checkout in v1. Worktree Markdown reviews exist through `alta` only. | §4.2 |

## What exists and is reused

| Need | Reused | Notes |
|---|---|---|
| The uncommitted diff | `changes/ProjectChangesPanel.tsx`, `changes/AllChanges.tsx`, `monaco/DiffEditor.tsx`, `monaco/CodeEditor.tsx` | `DiffEditor` hides its editors and has `glyphMargin: false` (`:44-46`). It gets opt-in props. |
| The editor's Text view | `editor/EditorSurface.tsx` (one Monaco editor, several document models; `ProjectEditor.tsx:803`), `mode === "source"` (`ProjectEditor.tsx:335-344`) | Comment zones must follow the active model: rebuilt when `active` changes. |
| The editor's Preview | `ProjectEditor.tsx:826-830` (`MarkdownContent` with `document`), the Preview/Text switch (`:707-712`) | |
| Comment boxes in Monaco | The view-zone, portal, glyph-**+** and `ResizeObserver` code of `AskFileReview.tsx` (`:31`, `:64-66`, `:126-129`, `:145-156`, `:173-239`, `:315-352`) | Single-line, in memory, one editor. Ranges, threads, kinds and two editors are new. **No UI test covers it today.** |
| Markdown rendering | `markdown.ts` (markdown-it), `markdownBoundary.ts` (DOMPurify), `MarkdownContent.tsx` | `token.map` is dropped today and `data-*` is not allowed. Line ranges get a renderer-owned mark, like `data-file-link` (`markdownBoundary.ts:21-29`). |
| Work items plan details | `workItems/WorkItemCards.tsx:48` (`MarkdownContent`), the item `status` (`:44`) | Comments and **Review** only when the status is not `done`. |
| Project service pattern | `CodeAlta.Catalog/WorkItems/WorkItemService` (`Changed` event, `ProjectDescriptor` arguments) and `WorkItemLinkStore` (gate, temp file, `File.Move` overwrite) | |
| `alta` group pattern | `BuiltInAltaCommandContributor.WorkItems.cs` (`Group`/`Leaf`, `--stdin`, JSONL records, `ResolveWorkItemProjectAsync`, `PoliciesFor`), `CallerWorktree` (`BuiltInAltaCommandContributor.cs:446`) | |
| Git | `GitRunner` / `GitWorktreeService.RunGitAsync` / `GitRun` / `SameRepository` (`Catalog/Worktrees/GitWorktreeService.cs:12,30,626,759`) | The desktop `ProjectGitChanges` is **not** moved. |
| Sending to a session | The dispatcher of `DesktopAltaTools.Attach` running `session send <id> --stdin --queue-if-busy`, as `AltaReminderService.DeliverAsync` does (`:579-597`) | Queues when busy, never steers. |
| A new session (Plan mode for a plan) | `SessionStarter.StartAsync` with `SessionStartModel`, plus a new `AgentPromptId` (`SessionStartChoice.cs:9`, `SessionStarter.cs:79`) | Started in the review's checkout. |
| Desktop RPC pattern | `Desktop/Rpc/WorkItemsRpc.cs` (`Refuse`, `ProjectAsync`, bounded records, 1-slot DropWrite `watch`); `DesktopJsonContext` (`BootRpc.cs:320/472`); the stub at `DesktopApplication.cs:890`; the fake in `demo-api.ts:133` | |
| Window wiring | `commandRegistry.ts`; `runCommand`/`commandAvailable` (`main.tsx:1966/1996`); `showChanges` (`main.tsx:952`); `sessionCanvasEntries` (`main.tsx:1107`, `SessionMenuEntry.endNode`); composer `status` (`main.tsx:3511`); `noteRunActivity` (`main.tsx:324`); `sessionWorktree()` (`worktrees/worktrees.ts:62`) | |

## Steps

Branch `feat/native-annotations`. Each step builds, keeps the existing .NET and frontend tests green, adds its own tests, and is committed with a dotnet-releaser prefix (`Add …`, `Enhance …`). Choose `Debug` or `Release` as AGENTS.md "Debug or Release" says before the first build. Steps 1–4 are useful without any UI, so the work can land as two pull requests: 1–4, then 5–12.

### Backend

- [ ] **1. Catalog annotations service** (spec §3, §5, §6, §7, §10).
  - New in `src/CodeAlta.Catalog/Annotations/`:
    - `ReviewComment.cs`: `ReviewComment` (fields of spec §5.1: `Kind`, `Author`, `AuthorSessionId`, `Status`, `ResolvedBy`, `ResolvedAt`, flags, …), `ReviewReply`, `CommentsFile` (`Version`, `NextId`, `ImportedPlugin`), enums `CommentKind`, `CommentStatus`, `CommentAuthor`;
    - `AnnotationFiles.cs`: path of a checkout's file, load, atomic save that *reports* failure, refusal on an unknown version or a malformed file, plugin import (`plan:`/`doc`/`md:` → `md:`, `sessionId` → `targetSessionId`, every comment `review_annotation` with `author = user`), `AnnotationJsonContext`;
    - `AnnotationAnchoring.cs` (ported from the plugin's `NoteStore.Relocate`), `AnnotationContent.cs` (optional `GitRunner`, default `GitWorktreeService.RunGitAsync`), `AnnotationPromptComposer.cs`;
    - `AnnotationService.cs`: `sealed`, takes `TimeProvider? time, GitRunner? git`; an instance-owned `Lock` per checkout file; a cache keyed by path, length and mtime; `event Action? Changed`.
  - API: every method takes a `ProjectDescriptor` and a `checkout` (`null` = project folder; a worktree must pass `SameRepository`):
    - `ListAsync(project, checkout, review, all)` re-anchors and returns comments plus content state (diff: changed files; md: exists);
    - `AddAsync` (kind, author), `UpdateAsync` (body, kind: `KindLocked` once `SentAt` is set), `DeleteAsync`;
    - `ResolveAsync(id, resolved, by)`: `by = agent` only on an `annotation` and never to reopen (`UserOnly`);
    - `ReplyAsync(id, author, text, addressed, sessionId)`: an agent's addressed reply sets `Addressed` and resolves an `annotation`; a user reply clears `Addressed`;
    - `ComposeAsync(project, checkout, review)` returns the prompt and the ids; `MarkSentAsync` / `RestoreAsync`;
    - `CountsAsync(project, checkout)` returns unresolved comments per `TargetSessionId`.
  - Rules: body edit and reopen clear `Addressed`; `Gone` from `git status --porcelain=v1 -z --untracked-files=all`; 500 comments per checkout, pruning the oldest resolved (by `ResolvedAt`) before refusing; 16 KiB per body; 50 replies.
  - Failures are typed (`AnnotationException` with a code matching spec §8: `notFound`, `invalidPath`, `invalidLines`, `invalidCheckout`, `limit`, `userOnly`, `kindLocked`, `storeUnreadable`, `writeFailed`).
  - Public XML docs, with thrown exceptions documented.
  - Tests in `src/CodeAlta.Catalog.Tests/AnnotationTests.cs`:
    - CRUD and ids; two checkouts keep separate ids and files;
    - kinds: agent reply resolves an `annotation`, not a `review_annotation`; agent resolve refused on `review_annotation`; agent reopen refused; kind change refused after send;
    - `Addressed` cleared by user reply, edit, reopen;
    - anchoring: moved, deleted, returned, nearest of two matches, old side;
    - `Gone` after a commit;
    - path rejection (rooted, `..`, outside the checkout), invalid checkout;
    - plugin import: mapping, kinds, run once, rerun after the file is deleted, malformed plugin file;
    - prompt text: diff, Markdown, mixed kinds, agent-written, outdated, gone, sent before;
    - cap: pruning of resolved comments, refusal at 500 unresolved;
    - a write failure leaves the state unchanged; unknown version is not overwritten; two concurrent writers;
    - one integration test on a real `git init` repository with a worktree.
- [ ] **2. `alta annotation` command group** (spec §8).
  - New `src/CodeAlta.LiveTool/BuiltInAltaCommandContributor.Annotations.cs`:
    - `list [--all] [--review diff|md:<path>] [--file <path>]`
    - `show <id>`
    - `reply <id> [--not-addressed] (--stdin | --content <text>)`
    - `add --file <path> --line <n>[-<m>] [--side new|old] [--review …] [--kind annotation|review_annotation] (--stdin | --content <text>)`
    - `resolve <id> [--reopen]`
    - `send [--review …] [--to <session-id>|new]`
    - every command: `[--project <project>] [--checkout <path>]`.
  - Project: `ResolveWorkItemProjectAsync`. Checkout: `CallerWorktree`, else the project folder; `--checkout .` is the project folder.
  - Caller: a session caller is `agent` (author, reply, resolve rules); a host caller is `user`.
  - Records `alta.annotation.item|summary|replied|added|changed|sent`; `replied` carries `resolved`. Errors map the service codes to `annotation.*` and add `annotation.unavailable`, `annotation.nothingToSend`, `annotation.promptTooLarge`.
  - Policies: `list`/`show` Read; the others Mutating.
  - `send` runs `session send … --queue-if-busy` through the same dispatcher (step 3's sender).
  - Register when the service exists (`BuiltInAltaCommandContributor.cs:178`, `PoliciesFor` `:205-236`); reserve the root in `PluginAltaCommandContributor.cs:15`.
  - Composition: desktop creates the service next to `workItems` (`DesktopApplication.cs:579`) and passes it through new `AnnotationService? annotations` parameters of `DesktopAltaTools.Attach`/`Compose` (`DesktopAltaTools.cs:57-119`); TUI adds it in `CodeAltaFrontendComposition.cs:135`.
  - Tests in `AltaLiveToolTests.cs`, after the fixture at `:2189-2233`: list, add as agent (author), reply as agent resolves an `annotation` and not a `review_annotation`, resolve refused on `review_annotation` for a session caller and allowed for a host caller, `--checkout` isolation and `annotation.invalidCheckout`, unavailable without the service, a plugin root `annotation` is refused.
- [ ] **3. Sender and Plan-mode start** (spec §4.5, §7).
  - Add `string? AgentPromptId = null` to `SessionStartModel` (`SessionStartChoice.cs:9`) and use `model?.AgentPromptId ?? AgentPromptCatalog.DefaultPromptName` in `SessionStarter.cs:79`. The interface does not change.
  - New `src/CodeAlta/Desktop/Annotations/AnnotationSender.cs`, which:
    1. composes the prompt;
    2. marks the comments `sent` and sets their `TargetSessionId`;
    3. delivers through a `deliver(sessionId, text)` delegate (desktop: the `altaCommands` dispatcher with a `host` caller), or starts a new session in the review's checkout through `ISessionStarter` (origin `annotation`; `AgentPromptId = "plan"` when the file is under `.alta/plans/`);
    4. restores the comments and reports the error on failure.
  - Measure the prompt limit of `session send --stdin`; refuse above it with `annotation.promptTooLarge` and the limit in the message, before anything is marked. **Write the measured limit into spec §7.**
  - Tests in `src/CodeAlta.Desktop.Tests/AnnotationSenderTests.cs`: idle, busy (queued), failure restores, too large refused unmarked, new session in Plan mode in a worktree checkout (the `RecordingStarter` pattern at `WorkItemHostTests.cs:198`).
- [ ] **4. Desktop RPC `annotations`** (spec §9).
  - New `src/CodeAlta/Desktop/Rpc/AnnotationsRpc.cs`, `[NeoRpcService("annotations", Version = 1)]`, with `list`, `add` (kind), `update` (body?, kind?), `delete`, `resolve`, `reply` (user), `send` (`TimeoutMilliseconds` for a new session), `counts(projectId, checkout)`, and `watch` (one 1-slot DropWrite channel with the revision, raised by `Changed`). Every call takes `checkout`.
  - Conventions: `Refuse(expectedEpoch)`, project ids only, `Identifier` checks, bounded answers.
  - Registration: `builder.AddAnnotationsService(…)` next to `DesktopApplication.cs:665`, a stub next to `:890`, records in `DesktopJsonContext` (`BootRpc.cs`), a fake in `frontend/src/demo-api.ts`.
  - No test counts the 35 channels per page (`DesktopApplication.cs:610`). Add one that counts `NeoRpcChannel` methods (16 with this one).
  - Tests: `src/CodeAlta.Desktop.Tests/AnnotationHostTests.cs` (the `WorkItemHostTests.Fixture` pattern): RPC and `alta` on the same file, watch fires on an agent reply, kind locked after send, stale epoch, stub.

### Frontend: shared parts

- [ ] **5. Safety net, then the shared Monaco comment module and card** (spec §4.3).
  1. **Before any change**, add `src/comments/lineComments.browser.test.ts` + `lineComments.mount.tsx`. They mount `AskFileReview` with a fake `api` and check: a glyph click adds a card, typing, `Esc` finishes, `Ctrl+D` deletes, the zone grows with the text, `Ctrl+N`/`Ctrl+P` move between comments.
  2. Add the new modules:
     - `src/comments/commentThreads.ts` (+ `.test.ts`): ranges, ordering, neighbours, grouping per end line, folded state.
     - `src/comments/useLineCommentZones.tsx`: options `editor, side, threads, readOnly, folded, renderThread, onAdd, onMoved?`; returns `portals, focusThread, revealThread, addAtCursor`. One view zone per thread (`ordinal` orders several on one line), a range decoration, drag or Shift-click for a range, a zone frame that observes its size. Zones are rebuilt when the editor's model changes (needed by `EditorSurface`).
     - `src/comments/CommentThreadCard.tsx`: body through `MarkdownContent`; replies; **Review** tag for a `review_annotation`; **Agent** badge with the session name; hover menu with Edit/Delete/Resolve/Reopen/Reply and, while never sent, **Mark for review** / **No review needed**; the **Needs my review** toggle while editing (disabled with "Already sent"); **Resolved by agent** on folded cards; the editing keys of the plugin.
     - `src/comments/commentKeys.ts`: `data-comment-keys`.
  3. Move `AskFileReview` onto the hook and keep its Ask-specific code (`addComment`, `commentsFit`, snapshot).
  4. Rename the generic CSS (`style.css:1797-1809` → `.line-comment-*`).
  5. In `main.tsx`, add an early return for `[data-comment-keys]` next to `:2086`.
  6. Add labels to `localization.ts` (5 translations each; `localization.test.ts:91` checks them).
  - **Done when** the new browser test passes before and after the move, and a card test covers the kind toggle, the hover menu and the locked state.
- [ ] **6. Annotations hub, review summary, status and menu** (spec §4.1, §4.4, §4.5).
  - New `src/comments/annotationsHub.ts`, on the `workItems/workItemsHub.ts` pattern: `connect(epoch)`, `useSyncExternalStore`, one `watch` per page; cached lists per `(project, checkout, review)`; `counts` per `(project, checkout)`.
  - New `src/comments/ReviewSummary.tsx`: the count, Outdated and No-longer-changed lists with **Show**; a **Send to agent** menu with the target session, the other sessions of the project in the same checkout (workspace snapshot filtered by project and `sessionWorktree()`), and **New session**; Send disabled with nothing unresolved.
  - New `src/comments/ReviewStatus.tsx`: "Review · N comments" in the composer `status` (`main.tsx:3511`) for the selected session, counted in its checkout. A click opens that checkout's Changes review.
  - Session row menu: "Review changes" with an `endNode` badge, through `sessionCanvasEntries` (`main.tsx:1107`), which covers both menus.
  - Refresh in `noteRunActivity` (`main.tsx:324`) through a `refreshAnnotations` ref.
  - Tests: `annotationsHub.test.ts`; extend `explorer/explorerSessionMenu.browser.test.ts` with the badge entry.

### Frontend: the surfaces

- [ ] **7. Diff review in the Changes tab** (spec §4.1).
  - `monaco/DiffEditor.tsx`: props `comments?: boolean` (`glyphMargin: comments` in the options effect at `:108-112`); `onEditors?(editors | null)`; `onInline?(inline)` (`!sideBySide || hostWidth < 780`, `ResizeObserver`); `handle.reveal(side, line)`, which disables `hideUnchangedRegions` for the duration when the line is folded.
  - `monaco/CodeEditor.tsx`: `onEditor`, `comments` (files that exist on one side only).
  - `changes/ProjectChangesPanel.tsx`: review state when `scope.kind === "head"`, for the shown `checkout`; comment counts in `FileRow` (`:43-57`); `<ReviewSummary>` between the rows and `historyRows` (`:505-506`); `request` extended with `line`, `side`, `commentId`, `review`; list reads in the `visible` effect (`:236-244`) plus the hub; `panelKeyDown` ignores `[data-comment-keys]`.
  - `changes/AllChanges.tsx`: threads passed down to `Section`, so zones are rebuilt when an editor mounts.
  - New `changes/reviewComments.ts` (+ test): comments of a file and side, inline-mode split, jump targets.
  - The `annotate` command (**Annotate changes**): `commandRegistry.ts`, category Session, no keys; `commandAvailable`/`runCommand`; computes the session's checkout as in `main.tsx:3492-3506` and calls `showChanges(project, null, checkout)` with `review: true`, which forces the Uncommitted scope.
  - Tests: extend `lineComments.browser.test.ts` with a `DiffEditor` that has comments (zones on both sides, a range, inline mode lists `old`-side comments, revealing a folded line); extend `changes/allChanges.browser.test.ts` to check that the section height includes zones.
  - Check in the developer instance, both themes and a narrow window.
- [ ] **8. Source line ranges in the Markdown renderer** (spec §4.2).
  - `markdown.ts`: `createMarkdownParser` option `sourceLineMark?: string` and a `lineOffset` per render; a core rule after `block` writes `data-source-lines="<mark>:<start>-<end>"` (1-based, inclusive, offset added) on `paragraph_open`, `heading_open`, `list_item_open`, `blockquote_open`, `tr_open`, `hr`, and on `<pre>` in the custom `fence`/`code_block` renderers; `html_block` stays without lines.
  - `splitFrontMatter` also returns `bodyLine`, the 1-based line where the body starts.
  - `markdownBoundary.ts`: render option `sourceLines?: boolean`; in `uponSanitizeAttribute`, a `data-source-lines` of `<mark>:<n>-<m>` keeps only `n-m` (`forceKeepAttr`), any other value is dropped, so raw HTML cannot fake a range; the front-matter table gets `1-(bodyLine-1)`.
  - `MarkdownContent.tsx`: prop `sourceLines?: boolean`.
  - Tests: `markdown.test.ts` (every block kind, nested list items, table rows, fences, front-matter offset, CRLF, BOM); `markdownContent.browser.test.ts` (the attribute survives the sanitizer, a raw `<p data-source-lines="1-2">` is stripped, rendering without `sourceLines` is unchanged).
- [ ] **9. Comments on the Markdown preview and in Work items** (spec §4.2, §4.4).
  - New `src/comments/useMarkdownComments.tsx`, run on the `MarkdownContent` container after each render: index `[data-source-lines]` blocks; one floating **+** in a left gutter column over the innermost hovered block, not inserted into the HTML; click for the block's range, Shift-click for the union with the anchor block, `c` on a focused block; `.md-commented` on every block a thread's range touches; a slot `<div>` after the innermost block that contains a thread's end line, holding a `CommentThreadCard` through a portal, re-inserted when the HTML changes; a range in no block with lines goes under the nearest preceding block.
  - `MarkdownContent.tsx`: optional `comments` prop (threads plus callbacks) that turns on `sourceLines` and the hook. Without it, nothing changes.
  - **Editor Preview** (`ProjectEditor.tsx:826-830`): comments when the file is on disk in the project and Markdown; review `md:<path>` of the project folder (D10); `<ReviewSummary>` beside the Preview/Text switch (`:707-712`); adding disabled with "Save the file to comment on it" while the document is dirty; refresh on save and through the hub.
  - **Work items** (`workItems/WorkItemCards.tsx:48`): the same `comments` prop for `md:<plan path>` and a **Review** action that opens the plan in the editor Preview, both only when the plan's status is not `done`.
  - The `annotate_plan` command (**Annotate a plan**): pick a plan of `.alta/plans/` that is not done (the work-items list), then open it in the editor in **Preview** through an open request with `mode: "preview"` that sets `modes` in `ProjectEditor`. Send from a plan defaults to the session that carries it, else the selected session; **New session** starts in Plan mode.
  - Tests: `comments/markdownComments.test.ts` (block lookup, range union, blocks touched by an unaligned range, slot placement); `comments/markdownComments.browser.test.ts` + mount (hover **+**, add on a list item, Shift-click a range, card under the right block, re-render keeps cards, dirty file disables adding); `editor` tests for the preview open request; a Work items test that a done plan shows no gutter and no **Review**; `commandRegistry.test.ts` (new commands are checked automatically).
- [ ] **10. Comments in the editor's Text view of Markdown files** (spec §4.2).
  - `editor/EditorSurface.tsx`: `onEditor?(editor | null)` and an opt-in `comments` prop that turns on `glyphMargin`.
  - `ProjectEditor.tsx`: when `mode === "source"`, the file is Markdown and on disk, run `useLineCommentZones` on the surface's editor for `md:<path>`, side `new`, with the same hub list as the Preview. Zones follow the active model: rebuilt when `active` changes, removed for other documents. Adding disabled while dirty, as in Preview.
  - The review summary stays beside the Preview/Text switch in both views.
  - Tests: extend `lineComments.browser.test.ts` with an `EditorSurface` mount: add on a line and a range, switch to another document and back (zones follow the model), a comment written in Text view appears in Preview on the touched blocks.

### Docs and finish

- [ ] **11. Docs** (spec Q1).
  - Move `doc/specs/annotations.md` to `doc/annotations.md` as the implemented contract (status line and plan link removed), link it from `doc/readme.md`, and remove the draft entry from `doc/specs/readme.md`.
  - `doc/desktop.md`: the Changes review, editor Preview and Text view comments, Work items, kinds, entry points, keys.
  - `doc/live-tool.md`: the group table at `:79`, a section `## Annotation commands` after `:787`, `annotation` in the reserved roots at `:911`.
  - `site/docs/review-comments.md` + `site/docs/menu.yml` (read `site/AGENTS.md` first; `ShippedUserGuideTests`).
  - A line in `readme.md`.
  - A rule in `doc/development-guide.md`: line comments use `comments/`, Markdown source lines use the renderer mark.
  - `lunet build` passes.
- [ ] **12. End-to-end pass and plugin handover.**
  1. Build, start `alta.exe --dev` and drive it through `codealta-dev`. Check:
     - edit a file; comment on a new line, a removed line and a range, in one-file and All-files views;
     - one `annotation` and one `review_annotation`; switch a kind before sending, check it is locked after;
     - send to the selected session; the agent's `alta annotation reply` resolves the `annotation` and leaves the `review_annotation` open with Addressed; resolve it as the user;
     - an agent-written comment (`alta annotation add`) shows the Agent badge;
     - move the text, remove it (Outdated), commit (No longer changed);
     - in a worktree checkout: its comments are separate from the project folder's.
  2. The same on a plan in the editor Preview with **New session** (Plan mode), in the Text view, on a `README.md`, and in Work items (not on a done plan).
  3. Run the plugin plan's handover check against the developer instance: comments imported once as `review_annotation`, the plugin in hand-over mode.
  4. Run all of it: `dotnet test`, `npm test`, `lunet build`.

## Verification

- Per step: `dotnet build` + `dotnet test` in the chosen configuration. From step 5, `node node_modules/typescript/bin/tsc --noEmit` and `npm test` in `src/CodeAlta/frontend`.
- Without the UI: `alta annotation list|add|reply|resolve|send` against the developer instance (`codealta-dev` `alta` tool), with and without `--checkout`.
- UI: `take_snapshot`/`take_screenshot`, `evaluate_script` for gutter clicks, `resize_page` for inline mode and a narrow window, both themes.
- TUI: builds, its tests pass, and `alta annotation list` works from a TUI session.

## Risks

- **Refactor without UI coverage.** The `AskFileReview` refactor has no UI test, so step 5 writes one first.
- **Monaco behaviour not verified in its source.** A reveal from code inside `hideUnchangedRegions`, and side-by-side padding of zones on one side. Both are covered by browser tests in step 7 before they are relied on.
- **One Monaco editor, many models.** `EditorSurface` swaps models in one editor; view zones belong to the editor, not the model, so they must be removed and rebuilt on each switch (step 10 test).
- **Re-anchoring.** `DiffEditor` replaces its text on each read and the preview HTML is rebuilt, so anchors are matched by content on the host each time and never kept in Monaco decorations.
- **Markdown slots inside injected HTML.** They disappear whenever `__html` changes; the hook re-inserts them in a layout effect keyed on the HTML (`MarkdownContent` already memoizes the markup).
- **Markdown in a scrolling editor pane.** The gutter is an overlay column, and only the slots add height, so search and links keep their layout.
- **Unsaved edits.** Preview and Text view show the editor text while anchors use the saved file, so adding is blocked while the document is dirty.
- **Agent auto-resolve.** An agent can close an `annotation` the user wanted to check. Mitigated by the visible **Resolved by agent** card, Reopen, and the `review_annotation` kind.
- **Same ids in two stores.** Import keeps the plugin's ids, so until the plugin is in hand-over mode, `a3` may exist in both files. The plugin plan removes the plugin's agent tools when the built-in feature exists.
- **Concurrent writers.** The UI, several sessions through `alta`, hand edits. Mitigated by the per-checkout gate, atomic replace, and the mtime check.
- **Channels.** One new `watch` makes 16 of 35. If a later feature makes the budget tight, fold it into `workItems.watch`.
- **Size.** About the work-items stack: ~2,000 backend lines, a frontend folder, three test suites, docs. The two-part landing keeps each review manageable.

## Out of scope (follow-ups)

- Comments in the branch-since-base and commit scopes of the Changes tab.
- Text-view comments on files that are not Markdown (spec Q11).
- Editor and Work items reviews of a worktree's files (D10), until the editor opens worktree files.
- Comments in the Ask file review's **Read** view, and on per-turn diffs in the timeline.
- A setting for the default kind (spec Q12).
- Any TUI UI (the TUI only gets the `alta` group).
