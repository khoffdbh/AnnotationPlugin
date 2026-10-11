# Drive an agent session by annotations

- Status: **Draft**, for iteration. It becomes the contract once approved; until then, every section may change. Once implemented, it moves to `doc/annotations.md` and leaves `doc/specs/` ([Q1](#q1-spec-or-user-doc)).
- Plan: [`.alta/plans/2026-10-11-native-annotations.md`](../../.alta/plans/2026-10-11-native-annotations.md). The plan says *how* and in which steps; this spec says *what* the feature does and the contracts it keeps. Where they differ, settle the difference here first.
- Replaces: the external annotate plugin (`../AnnotationPlugin/annotate/`).

## 1. Scope

The user writes comments on lines of text, sends every unresolved comment to an agent as one prompt, and the agent answers each comment by its id. Comments follow their text when the file changes.

A comment is one of two kinds ([§3.1](#31-kinds)):

- **`annotation`**, the default: the agent resolves it when it has done what the comment asks. No human step.
- **`review_annotation`**: the agent answers, and only the user resolves. The user picks this kind for a comment before it is sent.

| Surface | What is commented | Unit |
|---|---|---|
| **Diff review** | The **Changes** tab, **Uncommitted changes** scope, of the project folder or of a worktree checkout. Both sides of the Monaco diff, one-file and All-files views. | A line or a range of lines of one side. |
| **Markdown review** | Any Markdown file of the project in the code editor, in **Preview** and in **Text** view, and the plan details of the **Work items** tab. | Preview: one whole block, or several adjacent blocks. Text view: a line or a range of lines. |

Agents reach comments through `alta annotation` only (desktop and TUI). The TUI has no annotation UI.

Not in v1: branch-since-base and commit scopes, the **Text** view of files that are not Markdown ([Q11](#q11-text-view-of-other-files)), the Ask file review's **Read** view, per-turn diffs of the timeline. See [§12](#12-out-of-scope).

## 2. Terms

| Term | Meaning |
|---|---|
| **Checkout** | The project folder, or a worktree of the same repository. Every comment belongs to exactly one checkout, and is stored in it ([§5.1](#51-file)). |
| **Review** | The set of comments on one surface of one checkout: `diff`, or `md:<checkout-relative path>`. |
| **Side** | `new` (the working tree) or `old` (`HEAD`). Markdown comments are always `new`. |
| **Anchor** | The text of the commented lines when the comment was written or last re-anchored. |
| **Thread** | A comment with its replies. |
| **Kind** | `annotation` (the agent resolves) or `review_annotation` (only the user resolves). Not to be confused with a *review*, the set of comments on a surface. |
| **Target session** | The session a review's Send goes to by default (`TargetSessionId`). |

## 3. Comment lifecycle

### 3.1 Kinds

| Kind | Resolved by | When to use |
|---|---|---|
| `annotation` (default) | The agent, by an addressed reply or `alta annotation resolve`; or the user. | Most comments: "change this", "rename that". The user trusts the agent to close it. |
| `review_annotation` | The user only. The agent's reply sets `Addressed` and leaves the comment unresolved. | The user wants to check the result before the comment closes. |

- A new comment is an `annotation`.
- The user switches the kind of a comment that **has never been sent** (`sentAt` is empty): with the **Needs my review** toggle while editing the comment, or with **Mark for review** / **No review needed** in the menu shown when hovering the card ([§4.3](#43-comment-card)).
- Once a comment has been sent, its kind is fixed, also after a reopen: the agent was told who resolves it. The toggle and the menu entry are disabled, with the tooltip "Already sent".
- `alta annotation add` creates an `annotation` unless `--kind review_annotation` is given.

### 3.2 Status

```text
             send                       resolve (see §3.1 for who)
  open ─────────────▶ sent ────────────────────────────────▶ resolved
    │                                                          ▲  │
    └──────────────────────── resolve ─────────────────────────┘  │
    ▲                                                             │
    └─────────────────────────────── reopen (user) ───────────────┘
```

- `open`: written, not sent yet, or reopened.
- `sent`: in at least one prompt. It stays visible and is sent again by the next Send, marked *sent before, still open*.
- `resolved`: set as [§3.1](#31-kinds) allows. `resolvedBy` (`user` or `agent`) and `resolvedAt` record it. A resolved card folds into one line; one resolved by the agent says **Resolved by agent** and keeps the agent's reply visible. **Reopen** (user only) sets `open`.

### 3.3 Flags

| Flag | Set when | Cleared when |
|---|---|---|
| `Outdated` | The anchor is found nowhere in the current text of `(File, Side)`. | The anchor is found again: the comment re-attaches there. |
| `Gone` | Diff only: the file is no longer in the checkout's changed set (committed, reverted). | The file is changed again. |
| `Addressed` | An agent replies (`reply` without `--not-addressed`). On an `annotation` this also resolves it ([Q8](#q8-who-may-resolve)). | The user replies, edits the body, or reopens ([Q5](#q5-addressed-reset)). A reopened `annotation` is resolved again by the agent's next addressed reply. |

Flags other than `Addressed` never change `Status`. An outdated or gone comment is still sent until it is resolved ([Q6](#q6-gone-comments-in-send)).

### 3.4 Editing

- The user adds, edits, deletes, resolves, reopens, replies, and switches the kind of a comment never sent.
- An agent replies, resolves an `annotation`, and adds comments ([Q4](#q4-agent-authored-comments)).
- Every comment records its `author` (`user` or `agent`) and, for an agent, `authorSessionId`. A comment written by an agent is sent like any other.
- Deleting removes the thread; ids are never reused.

## 4. User-facing behaviour

### 4.1 Diff review

- Shown in the **Changes** tab when the scope is **Uncommitted changes**, for the checkout the tab shows.
- The glyph margin shows a **+** on hover. A click comments on the line; a drag, or a click then Shift-click, comments on a range. A range stays on one side.
- A thread shows in a view zone under the last line of its range; several threads on one line stack in creation order.
- The file list shows a count of unresolved comments per file.
- A **review summary** between the file rows and the history shows: the count, the **Outdated** and **No longer changed** lists with a **Show** action each, and **Send to agent**.
- Inline diff mode (side-by-side off, or the editor narrower than 780 px): comments on the `old` side are listed in the summary, not drawn in the diff.
- Files that exist on one side only (added, deleted) are shown by the single editor and take comments on that side.

### 4.2 Markdown review

Comments are available when the file is in the checkout and is Markdown. The review belongs to the checkout the file is read from. In v1 the code editor and the Work items plan details read the project folder only, so their reviews are of the project folder; a worktree's Markdown comments are reached through `alta annotation --checkout`. One review, `md:<path>`, is shared by every view of the file in that checkout: a comment written in one view shows in the others. The same file in another worktree has its own comments.

**Preview** (code editor, and Work items plan details):
- A floating **+** in a left gutter column follows the innermost hovered block. Click: that block's lines. Shift-click: the union of the anchor block and this one. `c` on a focused block: same as click.
- Commented blocks get a marker style. A thread shows after the innermost block that contains its end line.
- A comment whose lines are in no block with lines (raw HTML) shows under the nearest preceding block.
- A comment whose range does not match block boundaries (written in Text view or by `alta`) marks every block it touches; its card shows after the block that contains its end line ([Q3](#q3-md-ranges-from-alta)).
- A block is one of: heading, paragraph, list item, table row, code block (fenced or indented), block quote, horizontal rule, front matter. Its lines come from the Markdown parser.

**Text view** (code editor, Monaco):
- The same line comments as the diff review ([§4.1](#41-diff-review)), on the one side `new`: a **+** in the glyph margin, a drag or Shift-click for a range, threads in view zones under their last line.
- Lines are not snapped to blocks.

**Both views:**
- While the editor document has **unsaved edits**, cards are shown but adding is disabled: "Save the file to comment on it". The host anchors on the saved file.
- The **review summary** (count, Outdated list, **Send to agent**) sits beside the Preview/Text switch.

**Work items:** the plan details take the same Preview comments for `md:<plan path>` while the plan is **not done** (`draft`, `approved`, `in-progress`, `blocked`). A done plan shows its details without the gutter, without cards, and without the **Review** action.

### 4.3 Comment card

- The body is Markdown, rendered by `MarkdownContent`.
- A `review_annotation` shows a **Review** tag in its header; an `annotation` shows none.
- A comment written by an agent shows an **Agent** badge with the name of its session.
- Actions, in the menu shown when hovering the card: **Edit**, **Delete**, **Resolve** / **Reopen**, **Reply**, and, for a comment never sent, **Mark for review** or **No review needed** ([§3.1](#31-kinds)). Resolved cards fold; **Show** unfolds.
- While editing, a **Needs my review** toggle under the text box sets the kind; it is disabled once the comment has been sent.
- Keys while editing: `Enter` or `Ctrl+Enter` save, `Shift+Enter` new line, `Esc` cancels (a new empty comment is discarded). `Ctrl+N` / `Ctrl+P` move to the next / previous thread. Keys inside a card do not reach the window's shortcuts (`data-comment-keys`).

### 4.4 Entry points

| From | Does |
|---|---|
| **Annotate changes** command (palette, category Session, no default key) | Opens the Changes tab on the selected session's checkout, Uncommitted scope. |
| **Annotate a plan** command | Picks a plan of `.alta/plans/` that is **not done**, opens it in the editor in Preview. |
| The code editor, **Preview** or **Text** view of a Markdown file | Comment directly ([§4.2](#42-markdown-review)); the review summary beside the Preview/Text switch sends. |
| **Review changes** in a session row menu | Same as **Annotate changes** for that session. Its badge counts the unresolved comments of the session's checkout whose target is that session. |
| **Review · N comments** in the composer status | Shown while the selected session is the target of unresolved comments of its checkout. A click opens its review. |
| **Review** on a plan card of Work items | Shown only for a plan that is **not done**. Opens the plan in the editor preview. |

### 4.5 Send to agent

**Send to agent** puts every unresolved comment of the review into one prompt, file by file ([§7](#7-prompt)). The menu offers:

1. the review's target session;
2. the other sessions of the project that work in the same checkout;
3. **New session**: a new session of the project in that checkout; in **Plan** mode when the file is under `.alta/plans/`, in the default mode otherwise.

Default target: the session selected when the comment was written. For a plan, the session that carries the plan, else the selected session.

On Send:
1. the comments become `sent`, with `SentAt` and `SentTo`, and their `TargetSessionId` becomes the chosen session;
2. the prompt is delivered with `session send <id> --stdin --queue-if-busy`: it queues when the session is busy and never steers;
3. on failure, the comments get back their status, `SentAt` and `SentTo`, and the error is shown.

A review with no unresolved comment has Send disabled.

### 4.6 Refresh

A review reads its files again when it is shown, when a session of the project ends a turn, when a Markdown file is saved, and when the `watch` channel reports a change (agent reply, another window, `alta`).

## 5. Storage

### 5.1 File

- `<checkout>/.alta/annotations/comments.json`: one file per checkout, holding every review of that checkout. The project folder has its own; each worktree has its own, inside the worktree ([Q2](#q2-storage-location-and-worktrees)).
- Comments never cross checkouts: a worktree does not see the project folder's comments, nor those of another worktree. Removing a worktree removes its comments with it.
- The file is machine state, best left ignored; CodeAlta writes no `.gitignore` entry. If it is committed, a worktree created from that branch starts with the comments of the branch.
- JSON, camelCase, written through a source-generated context (AOT-safe).

```json
{
  "version": 1,
  "nextId": 13,
  "importedPlugin": true,
  "comments": [
    {
      "id": "a12",
      "review": "diff",
      "file": "src/CodeAlta.Catalog/Foo.cs",
      "side": "new",
      "startLine": 12,
      "endLine": 14,
      "anchor": "line 12\nline 13\nline 14",
      "body": "Why not reuse `GitRunner` here?",
      "author": "user",
      "authorSessionId": null,
      "kind": "annotation",
      "status": "resolved",
      "resolvedBy": "agent",
      "resolvedAt": "2026-10-11T09:12:00Z",
      "outdated": false,
      "gone": false,
      "addressed": true,
      "replies": [
        { "author": "agent", "body": "Now uses GitRunner.", "at": "2026-10-11T09:12:00Z", "sessionId": "s-42" }
      ],
      "targetSessionId": "s-42",
      "createdAt": "2026-10-11T09:00:00Z",
      "sentAt": "2026-10-11T09:05:00Z",
      "sentTo": "s-42"
    }
  ]
}
```

- Ids are `a<n>`, unique in the file (so per checkout), from `nextId`, never reused. The same id may exist in two checkouts; every command and RPC call names its checkout.
- `file` is relative to the checkout, with `/` separators.
- An unknown `version` is not overwritten: the service reports it and refuses to write.
- `importedPlugin` records that the plugin import ran ([Q10](#q10-import-marker)). Deleting the file runs the import again.

### 5.2 Writes

- Every change is written at once: serialize, write `comments.json.tmp`, then `File.Move` with overwrite.
- A failed write leaves the in-memory state as it was and reports the error. Nothing is half-applied.
- One gate per checkout file serializes writers inside the process. Between processes and hand edits, the service reloads when the file's length or modification time differs from its cache before every read and write.
- A malformed file is not overwritten: list answers an error, writes are refused, the user fixes or deletes the file.

### 5.3 Plugin import

- Once per checkout, when its `comments.json` does not exist and its `.alta/plugin-data/annotate/annotations.json` does. In practice this is the project folder, where the plugin kept its file.
- Mapping: `plan:<p>` and `doc`/`md:<p>` reviews become `md:<p>`; `diff` stays `diff`; the plugin's `sessionId` becomes `targetSessionId`; `author` is `user`; ids and `nextId` are kept.
- Every imported comment is a `review_annotation`: the plugin told agents that only the user resolves, and imported comments may already have been sent with that promise. A resolved plugin comment gets `resolvedBy = user`.
- The plugin file is left in place. A malformed plugin file is skipped and reported once; the import is then marked done.

## 6. Anchoring and content

### 6.1 Content of a side

| Review | `new` | `old` |
|---|---|---|
| `diff` | The checkout's file, at most 1 MiB. | `git show HEAD:./<path>` run in the checkout. |
| `md:<path>` | The checkout's file, at most 1 MiB, read without git. | Not allowed. |

- A path is refused when it is rooted, contains `..`, or resolves outside the checkout.
- A worktree checkout must be a fully qualified path in the same repository as the project folder.
- The changed set of a checkout: `git status --porcelain=v1 -z --untracked-files=all`. A diff comment on a file outside it is `Gone`.
- A missing or too large file, or a non-text file, has no content: its comments are `Outdated`.

### 6.2 Re-anchoring

On every list, and before compose, for each comment of the review:

1. Split the anchor and the content into lines. Compare lines with trailing white space ignored, ordinal otherwise.
2. If the anchor matches at `startLine`, keep it and clear `Outdated`.
3. Otherwise take the match whose start is nearest the old `startLine` (the earlier one on a tie), move `startLine`/`endLine` there, and clear `Outdated`.
4. With no match, set `Outdated` and keep the old lines.

Changes are saved only when a line or a flag changed. Anchors are never kept in Monaco decorations or the rendered HTML; the host recomputes them each read.

## 7. Prompt

Layout, kept from the plugin. Files in ordinal order, comments by start line:

~~~~markdown
Review notes on your changes: 2 comments. Address each one; when done, run `alta annotation reply <id> --stdin` with what you changed. Your reply resolves a comment, except one marked *the user reviews it*: the user resolves that one after checking your change.

## src/CodeAlta.Catalog/Foo.cs

### [a3] lines 12-14 (new) — the user reviews it
```diff
 var git = new GitRunner();
+var result = await git.RunAsync("status");
+return result;
```
Why not reuse `GitWorktreeService.RunGitAsync`?

### [a5] line 40 (old) — sent before, still open
```diff
-    // Retry once.
```
Keep this comment.
> Your earlier reply: Removed because the retry is gone.
~~~~

- **Header:** `your changes` (diff), `your plan` (file under `.alta/plans/`), or `` `<path>` `` (other Markdown). The instruction is *Address each one* for a diff and *Revise the file for each one* for Markdown.
- **Header instruction about resolving:** the sentence on who resolves is written only for the kinds present: all `annotation` → *Your reply resolves each comment.*; all `review_annotation` → *The user resolves each comment after checking your change.*; mixed → the sentence of the example.
- **Comment heading:** `### [<id>] line <n>` or `lines <n>-<m>`; for a diff, ` (<side>)`; for Markdown, ` — "<enclosing heading>"` when there is one. Then ` — the user reviews it` for a `review_annotation` in a mixed prompt, ` — written by an agent` for an agent's comment, and the flags ` — outdated: these lines changed since the comment`, ` — file no longer changed`, ` — sent before, still open`.
- **Snippet:**
  - diff: a `diff` fence, longer than any backtick run inside. An `old` line is `-`. A `new` line is `+` when it is not in the `HEAD` text (computed from the two texts), else a space. An outdated comment shows its anchor with a space prefix.
  - Markdown: the anchor as a `>` quote.
- **Body**, trimmed, then each reply on one line: `> Your earlier reply: …` (agent) or `> Reply: …` (user).
- Size: if the prompt exceeds what `session send --stdin` accepts, Send is refused with a message that names the limit; nothing is marked sent and nothing is split ([Q7](#q7-prompt-size)). The limit is measured during step 3 of the plan and written here.

## 8. `alta annotation`

Registered when the host has an `AnnotationService` (desktop and TUI). The root `annotation` is reserved for built-ins: a plugin root with that name is refused.

```text
alta annotation list [--all] [--review diff|md:<path>] [--file <path>]
alta annotation show <id>
alta annotation reply <id> [--not-addressed] (--stdin | --content <text>)
alta annotation add --file <path> --line <n>[-<m>] [--side new|old] [--review diff|md:<path>] [--kind annotation|review_annotation] (--stdin | --content <text>)
alta annotation resolve <id> [--reopen]
alta annotation send [--review diff|md:<path>] [--to <session-id>|new]
```

Every command also takes `[--project <project>] [--checkout <path>]`.

- **Project:** the caller's project, or `--project`. **Checkout:** the caller's worktree when it has one, else the project folder; `--checkout` names another checkout of the project (a full worktree path, or `.` for the project folder). Every command reads and writes that checkout's comments only.
- `list`: open and sent comments, re-anchored; `--all` adds resolved ones. One `alta.annotation.item` per comment (id, review, checkout, file, side, lines, kind, author, status, resolvedBy, flags, body, replies, target), then `alta.annotation.summary` (counts by kind, status and flag).
- `show`: one `alta.annotation.item` with the anchor text.
- `reply`: appends a reply with `author = agent` and the caller's session id, and sets `Addressed` unless `--not-addressed`. On an `annotation`, an addressed reply also resolves it (`resolvedBy = agent`); on a `review_annotation` the status does not change. Record `alta.annotation.replied`, with `resolved: true|false`. A host caller (no session) replies as `user` and never resolves.
- `add`: review defaults to `diff`, kind to `annotation`. `author` is `agent` with the caller's session id, or `user` for a host caller. For `md:` the lines are taken as given, not snapped to blocks ([Q3](#q3-md-ranges-from-alta)). Target: the caller's session. Record `alta.annotation.added`.
- `resolve`: a session caller may resolve an `annotation` (`resolvedBy = agent`); it is refused `annotation.userOnly` on a `review_annotation` and for `--reopen`. A host caller (the user's own `alta`) may do both. Record `alta.annotation.changed`.
- `send`: same as [§4.5](#45-send-to-agent); `--to` defaults to the review's target session, `new` starts one. Record `alta.annotation.sent` (count, ids, session id, created).
- Policies: `list` and `show` read-only; the others mutating.

Errors (proposed codes, `<group>.<camelCase>` like `task.proposalsDisabled`):

| Code | When |
|---|---|
| `annotation.unavailable` | The host has no annotation service. |
| `annotation.notFound` | Unknown id. |
| `annotation.invalidPath` | Rooted path, `..`, outside the checkout, or `--side old` on a Markdown review. |
| `annotation.invalidCheckout` | `--checkout` is not the project folder or a worktree of the same repository. |
| `annotation.invalidLines` | A range outside the file, or `m < n`. |
| `annotation.limit` | A cap of [§10](#10-limits) is reached. |
| `annotation.nothingToSend` | The review has no unresolved comment. |
| `annotation.promptTooLarge` | See [§7](#7-prompt). |
| `annotation.storeUnreadable` | Malformed file or unknown version. |
| `annotation.writeFailed` | The file could not be written; nothing changed. |
| `annotation.userOnly` | A session caller resolves a `review_annotation`, or reopens any comment. |
| `annotation.kindLocked` | The kind of a comment already sent is changed. |

## 9. Desktop RPC `annotations` (version 1)

Project ids only, every call checked with the boot epoch (`Refuse(expectedEpoch)`), bounded answers. `checkout` is `null` for the project folder or the full path of a worktree; it scopes every call, ids included.

```text
list(projectId, checkout, review, all?)          -> { comments: Comment[], content: Content }
add(projectId, checkout, review, file, side, startLine, endLine, body, kind, targetSessionId?) -> Comment   (author = user)
update(projectId, checkout, id, body?, kind?)    -> Comment          (kind: refused once sent)
delete(projectId, checkout, id)                  -> { id, deleted }
resolve(projectId, checkout, id, resolved)       -> Comment
reply(projectId, checkout, id, body)             -> Comment          (author = user)
send(projectId, checkout, review, target)        -> { count, ids, sessionId, created }   (target: session id | "new")
counts(projectId, checkout)                      -> { [targetSessionId]: number }       (unresolved)
watch()                                          -> channel of { revision }              (1 slot, drop-write)

Content = { kind: "diff", changedFiles: string[] } | { kind: "md", exists: boolean }
```

`send` with `"new"` has a longer timeout, for starting the session.

## 10. Limits

| Limit | Value | When reached |
|---|---|---|
| Comments per checkout | 500 | `add` deletes the oldest resolved comments (by `resolvedAt`) to make room; it is refused with `annotation.limit` only when all 500 are unresolved ([Q9](#q9-full-store)). |
| Body | 16 KiB | Refused. |
| Replies per comment | 50 | Refused. |
| File read for content | 1 MiB | No content: comments outdated. |

## 11. Ownership and tests

| Area | Project | Tests |
|---|---|---|
| Model, store, anchoring, content, prompt, service | `src/CodeAlta.Catalog/Annotations/` | `src/CodeAlta.Catalog.Tests/AnnotationTests.cs` |
| `alta annotation` | `src/CodeAlta.LiveTool/BuiltInAltaCommandContributor.Annotations.cs` | `AltaLiveToolTests.cs` |
| Sender, Plan-mode start | `src/CodeAlta/Desktop/Annotations/` | `src/CodeAlta.Desktop.Tests/AnnotationSenderTests.cs` |
| RPC | `src/CodeAlta/Desktop/Rpc/AnnotationsRpc.cs` | `src/CodeAlta.Desktop.Tests/AnnotationHostTests.cs` |
| Line comments, hub, summary, cards | `src/CodeAlta/frontend/src/comments/` | `comments/*.test.ts`, `comments/*.browser.test.ts` |
| Diff surface | `frontend/src/changes/`, `frontend/src/monaco/` | `changes/*.test.ts`, `changes/allChanges.browser.test.ts` |
| Markdown source lines | `frontend/src/markdown.ts`, `markdownBoundary.ts`, `MarkdownContent.tsx` | `markdown.test.ts`, `markdownContent.browser.test.ts` |
| Editor surfaces (Preview, Text view of Markdown) | `frontend/src/editor/`, `frontend/src/comments/` | `editor/*.test.ts`, `comments/markdownComments.browser.test.ts` |

## 12. Out of scope

- Branch-since-base and commit scopes of the Changes tab.
- The editor's **Text** view of files that are not Markdown ([Q11](#q11-text-view-of-other-files)), and the Ask file review's **Read** view.
- Per-turn diffs in the timeline.
- Any TUI UI.
- Character or in-line selections in rendered Markdown.

## 13. Decisions

The record of what was asked and settled. Every question is decided, and the sections above apply every decision.

| # | Question | State |
|---|---|---|
| Q1 | Spec or user doc | Decided |
| Q2 | Storage location and worktrees | Decided |
| Q3 | md ranges from alta | Decided |
| Q4 | Agent-authored comments | Decided |
| Q5 | Addressed reset | Decided |
| Q6 | Gone comments in Send | Decided |
| Q7 | Prompt size | Decided |
| Q8 | Who may resolve | Decided |
| Q9 | Full store | Decided |
| Q10 | Import marker | Decided |
| Q11 | Text view of other files | Decided |
| Q12 | Default kind | Decided |

### Q1. Spec or user doc

**Decided:** once implemented, this spec becomes `doc/annotations.md` (step 10 of the plan) and is removed from `doc/specs/`, as `doc/specs/readme.md` asks.

### Q2. Storage location and worktrees

**Decided:** comments are always scoped to the project and the checkout. Each checkout (the project folder, each worktree) has its own `comments.json` inside it; ids are unique per checkout; a Markdown review reads the file of its own checkout. Applied in §2, §4.2, §4.4, §4.5, §5, §6.1, §8, §9, §10.

### Q3. md ranges from alta

**Decided:** the host has no Markdown parser, so ranges from `alta annotation add` on `md:` and from the editor's Text view are stored as given, not snapped to blocks. The preview shows the card after the block that contains the end line and marks every block the range touches. Applied in §4.2 and §8.

### Q4. Agent-authored comments

**Decided:** every comment has `author: user|agent` and `authorSessionId?`; the card shows an **Agent** badge; agent comments are sent like any other. Applied in §3.4, §4.3, §5.1, §7, §8.

### Q5. Addressed reset

**Decided:** a user reply, a body edit, or a reopen clears `Addressed`. A reopened `annotation` is resolved again by the agent's next addressed reply. Applied in §3.3.

### Q6. Gone comments in Send

**Decided:** gone comments are sent, flagged *file no longer changed*, until resolved, like outdated ones. Applied in §3.3 and §7.

### Q7. Prompt size

**Decided:** a prompt over the limit of `session send --stdin` is refused with the limit in the message; no splitting in v1. The limit is measured in step 3 of the plan. Applied in §7.

### Q8. Who may resolve

**Decided:** an agent resolves an `annotation`, only the user resolves a `review_annotation`, and only the user reopens. An agent's addressed reply resolves an `annotation` at once, so one call closes the loop. Applied in §3.1, §3.3, §8.

### Q9. Full store

**Decided:** when a checkout holds 500 comments, `add` first deletes the oldest resolved comments (by `resolvedAt`) to make room; it is refused only when all 500 are unresolved. Applied in §10.

### Q10. Import marker

**Decided:** `importedPlugin` in `comments.json` records that the import ran. Deleting the file runs the import again, which is acceptable. Applied in §5.1 and §5.3.

### Q11. Text view of other files

**Decided:** the Text view takes comments on Markdown files only in v1; other files are a follow-up. Applied in §1 and §12.

### Q12. Default kind

**Decided:** every new comment is an `annotation`; no setting in v1. A **Default kind of new comments** setting may follow if the toggle is used often. Applied in §3.1.
