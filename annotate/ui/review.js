// The review canvas: a diff (or a plan split into Markdown blocks) with line comments. Hover a line and click +, or press `c` on a
// focused line, drag or Shift-click for a range, write a comment: Enter saves, Shift+Enter adds a line, Esc cancels. "Send to agent"
// sends every unresolved comment as one prompt. The comments, the diff and the plan live in plugin.cs; this script reads them with
// `alta.rpc` and hears `review.changed` when they change (a comment, an agent's reply, a turn that ended).
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Button, Callout, Card, HTMLSelect, Menu, MenuDivider, MenuItem, NonIdealState, PopoverNext, Spinner, Tag, TextArea } from "@blueprintjs/core";
import { FileLink, Icon, Markdown, html, useAlta, useVisible } from "codealta";

const css = `
.canvas-panel:has(.ann-root) { overflow: hidden; }
.plugin-html:has(> .ann-root) { height: 100%; padding: 0; margin: 0; box-sizing: border-box; }
.ann-root { display: flex; flex-direction: column; height: 100%; min-height: 0; }
.ann-toolbar { display: flex; gap: 8px; align-items: center; padding: 8px 12px; border-bottom: 1px solid rgba(128,128,128,.25); flex-wrap: wrap; }
.ann-body { display: flex; flex: 1; min-height: 0; }
.ann-files { width: 260px; flex: none; overflow: auto; border-right: 1px solid rgba(128,128,128,.25); padding: 4px 0; }
.ann-file { display: flex; gap: 6px; align-items: center; padding: 3px 10px; cursor: pointer; white-space: nowrap; }
.ann-file:hover { background: rgba(128,128,128,.12); }
.ann-file.selected { background: rgba(45,114,210,.18); }
.ann-file-name { overflow: hidden; text-overflow: ellipsis; flex: 1; }
.ann-main { flex: 1; overflow: auto; padding: 8px 12px 48px; min-width: 0; }
.ann-table { border-collapse: collapse; width: 100%; font-family: var(--bp-typography-family-mono, ui-monospace, SFMono-Regular, Consolas, monospace); font-size: 12px; }
.ann-table td { padding: 0 6px; vertical-align: top; }
.ann-row { outline: none; }
.ann-row:focus-visible td, .ann-row.focused td { box-shadow: inset 0 1px 0 rgba(45,114,210,.6), inset 0 -1px 0 rgba(45,114,210,.6); }
.ann-num { width: 1%; text-align: right; opacity: .55; user-select: none; white-space: nowrap; }
.ann-gutter { width: 22px; padding: 0 !important; user-select: none; }
.ann-plus { opacity: 0; width: 20px; height: 18px; line-height: 16px; border: none; border-radius: 4px; background: #2d72d2; color: white; cursor: pointer; font-weight: bold; padding: 0; }
.ann-row:hover .ann-plus, .ann-row:focus .ann-plus, .ann-row.focused .ann-plus, .ann-plus:focus-visible { opacity: 1; }
.ann-code { white-space: pre-wrap; word-break: break-all; }
.ann-add { background: rgba(46,160,67,.16); }
.ann-del { background: rgba(248,81,73,.16); }
.ann-noted .ann-num { background: rgba(255,196,0,.28); opacity: 1; }
.ann-selected { background: rgba(45,114,210,.22) !important; }
.ann-hunk td { opacity: .6; padding: 4px 6px; background: rgba(128,128,128,.08); }
.ann-thread td { padding: 4px 6px 8px 6px; font-family: var(--bp-typography-family-sans, inherit); font-size: 13px; }
.ann-note { margin: 0; max-width: 900px; }
.ann-note-head { display: flex; gap: 6px; align-items: center; flex-wrap: wrap; margin-bottom: 4px; }
.ann-note-actions { margin-left: auto; display: flex; gap: 2px; }
.ann-reply { border-left: 3px solid rgba(45,114,210,.5); padding: 2px 8px; margin-top: 6px; }
.ann-table.ann-plan { font-family: inherit; font-size: 13px; }
.ann-plan .ann-num { font-family: var(--bp-typography-family-mono, ui-monospace, SFMono-Regular, Consolas, monospace); font-size: 12px; padding-top: 4px; }
.ann-plan .ann-gutter { padding-top: 2px !important; }
.ann-block td { padding: 2px 6px; }
.ann-block .ann-md h1, .ann-block .ann-md h2, .ann-block .ann-md h3, .ann-block .ann-md h4 { margin: 6px 0 2px; }
.ann-block .ann-md > :last-child { margin-bottom: 0; }
.ann-section { margin: 8px 0; }
.ann-hint { opacity: .6; font-size: 12px; }
`;

export default function Review() {
    const alta = useAlta();
    const visible = useVisible();
    const [view, setView] = useState(null);
    const [notes, setNotes] = useState([]);
    const [error, setError] = useState(null);
    const [loading, setLoading] = useState(false);
    const [problem, setProblem] = useState(null);
    const [selectedFile, setSelectedFile] = useState(null);
    const [sessions, setSessions] = useState([]);
    const [plans, setPlans] = useState([]);
    const [sending, setSending] = useState(false);

    const call = useCallback(async (name, input) => {
        setProblem(null);
        try { return await alta.rpc.invoke(name, input ?? {}, { signal: alta.closed }); }
        catch (failure) { if (!alta.closed.aborted) setProblem(`${failure.code ?? "error"}: ${failure.message}`); return null; }
    }, [alta]);

    const load = useCallback(async () => {
        setLoading(true);
        try {
            const next = await alta.rpc.invoke("review.get", {}, { signal: alta.closed });
            setView(next);
            setNotes(next.notes ?? []);
            setError(null);
        } catch (failure) {
            if (!alta.closed.aborted) setError(failure);
        } finally {
            setLoading(false);
        }
    }, [alta]);

    const loadNotes = useCallback(async () => {
        try { setNotes(await alta.rpc.invoke("review.notes", {}, { signal: alta.closed }) ?? []); }
        catch { /* the next load shows the failure */ }
    }, [alta]);

    // Read the review when the tab is shown (and the first time), and the sessions and plans for the menus.
    useEffect(() => { if (visible) void load(); }, [visible, load]);
    useEffect(() => {
        if (!visible) return;
        alta.rpc.invoke("review.sessions", {}, { signal: alta.closed }).then(setSessions, () => {});
        alta.rpc.invoke("review.plans", {}, { signal: alta.closed }).then(setPlans, () => {});
    }, [visible, alta]);

    // A comment changed (here, in another tab, or an agent replied), or a session ended a turn and files may have changed.
    useEffect(() => {
        let stop = null;
        let ended = false;
        alta.rpc.subscribe("review.changed", change => { if (change?.refresh) void load(); else void loadNotes(); }).then(
            unsubscribe => { if (ended) unsubscribe(); else stop = unsubscribe; },
            () => {});
        return () => { ended = true; stop?.(); };
    }, [alta, load, loadNotes]);

    const unresolved = notes.filter(note => note.status !== "resolved");
    useEffect(() => { alta.host.setBadge(unresolved.length > 0 ? unresolved.length : null); }, [alta, unresolved.length]);

    const files = view?.files ?? [];
    useEffect(() => {
        if (view?.kind !== "diff") return;
        if (selectedFile === UNCHANGED) return;
        if (!selectedFile || !files.some(file => file.path === selectedFile)) setSelectedFile(files[0]?.path ?? null);
    }, [view, files, selectedFile]);

    const send = async target => {
        setSending(true);
        const result = await call("review.send", { target });
        setSending(false);
        if (!result) return;
        const name = sessions.find(session => session.sessionId === result.sessionId)?.title ?? (result.created ? "a new session" : result.sessionId);
        alta.host.notify(`Sent ${result.count} comment${result.count === 1 ? "" : "s"} to ${name}.`);
        if (result.created) alta.rpc.invoke("review.sessions", {}, { signal: alta.closed }).then(setSessions, () => {});
    };

    const mutations = useMemo(() => ({
        add: async request => { const note = await call("note.add", request); if (note) await loadNotes(); return note; },
        update: async (id, body) => { if (await call("note.update", { id, body })) await loadNotes(); },
        remove: async id => { if (await call("note.delete", { id })) await loadNotes(); },
        resolve: async (id, resolved) => { if (await call("note.resolve", { id, resolved })) await loadNotes(); },
    }), [call, loadNotes]);

    if (!view) {
        return html`<div style=${{ padding: 16 }}><style>${css}</style>
            ${error ? html`<${Callout} intent="danger" title="The review did not load">${error.message}<//>` : html`<${Spinner} size=${24} />`}
        </div>`;
    }

    const current = sessions.find(session => session.current);
    const others = sessions.filter(session => !session.current);
    const sendMenu = html`
        <${Menu}>
            <${MenuItem} icon="send-message" text=${`This session${current ? ` — ${current.title}` : ""}`} onClick=${() => send("session")} />
            ${others.length > 0 && html`<${MenuDivider} title="Other sessions of the project" />`}
            ${others.map(session => html`<${MenuItem} key=${session.sessionId} icon="chat" text=${session.title} label=${session.state ?? ""} onClick=${() => send(session.sessionId)} />`)}
            <${MenuDivider} />
            <${MenuItem} icon="add" text="New session" onClick=${() => send("new")} />
        <//>`;

    const reviewValue = view.review;
    return html`
        <div className="ann-root">
            <style>${css}</style>
            <div className="ann-toolbar">
                <${Icon} name="message-square-text" />
                <${HTMLSelect} minimal value=${reviewValue} aria-label="What to review"
                    onChange=${event => { if (event.target.value !== reviewValue) void call("review.open", { review: event.target.value }); }}>
                    <option value="diff">Uncommitted changes</option>
                    ${plans.map(plan => html`<option key=${plan.path} value=${"plan:" + plan.path}>Plan: ${plan.title}</option>`)}
                    ${reviewValue !== "diff" && !plans.some(plan => "plan:" + plan.path === reviewValue) && html`<option value=${reviewValue}>${reviewValue}</option>`}
                <//>
                <${Button} size="small" icon="refresh" loading=${loading} onClick=${load} title="Read the files again">Refresh<//>
                <${Tag} minimal round intent=${unresolved.length > 0 ? "primary" : "none"} data-testid="open-count">${unresolved.length} open<//>
                <span className="ann-hint">Hover a line and click +, or press c · drag or Shift-click for a range</span>
                <span style=${{ flex: 1 }} />
                <${PopoverNext} placement="bottom-end" content=${sendMenu} disabled=${unresolved.length === 0}>
                    <${Button} size="small" intent="primary" icon="send-message" endIcon="caret-down" loading=${sending} disabled=${unresolved.length === 0}>Send to agent<//>
                <//>
            </div>
            ${problem && html`<${Callout} intent="warning" icon="warning-sign" style=${{ margin: 8 }}>${problem}<//>`}
            ${view.error
                ? html`<div style=${{ padding: 16 }}><${Callout} intent="warning" title="Nothing to review">${view.error}<//></div>`
                : view.kind === "plan"
                    ? html`<${PlanReview} view=${view} notes=${notes} mutations=${mutations} />`
                    : html`<${DiffReview} view=${view} notes=${notes} mutations=${mutations} selectedFile=${selectedFile} onSelect=${setSelectedFile} />`}
        </div>`;
}

// ---------------------------------------------------------------- Diff

const sideOf = line => line.k === "-" ? "old" : "new";
const numberOf = (line, side) => side === "old" ? line.o : line.n;

// The entry of the file list for comments on files that have no changes any more (committed or reverted since).
const UNCHANGED = "\u0000unchanged";

function DiffReview({ view, notes, mutations, selectedFile, onSelect }) {
    const files = view.files;
    const unchanged = notes.filter(note => !files.some(file => file.path === note.file));
    const unchangedView = unchanged.length > 0 && html`
        <div className="ann-section">
            <div className="ann-hint" style=${{ marginBottom: 4 }}>Comments on files that no longer have uncommitted changes. They are still sent until resolved.</div>
            ${unchanged.map(note => html`<div key=${note.id} style=${{ marginBottom: 6 }}>
                <div className="ann-hint"><${FileLink} path=${note.file} line=${note.startLine}>${note.file}<//></div>
                <${NoteCard} note=${note} mutations=${mutations} />
            </div>`)}
        </div>`;
    if (files.length === 0) {
        return html`<div className="ann-main">
            <${NonIdealState} icon="tick-circle" title="No uncommitted changes" description="The working tree matches HEAD." />
            ${unchangedView}
        </div>`;
    }

    const file = files.find(candidate => candidate.path === selectedFile) ?? files[0];
    const showUnchanged = selectedFile === UNCHANGED && unchanged.length > 0;
    const unresolvedUnchanged = unchanged.filter(note => note.status !== "resolved").length;
    const countFor = path => notes.filter(note => note.file === path && note.status !== "resolved").length;
    return html`
        <div className="ann-body">
            <div className="ann-files" role="listbox" aria-label="Changed files">
                ${files.map(item => html`
                    <div key=${item.path} role="option" aria-selected=${!showUnchanged && item.path === file.path} title=${item.path}
                        className=${"ann-file" + (!showUnchanged && item.path === file.path ? " selected" : "")} onClick=${() => onSelect(item.path)}>
                        <${Tag} minimal intent=${statusIntent(item.status)}>${statusLetter(item.status)}<//>
                        <span className="ann-file-name">${item.path.split("/").pop()} <span className="ann-hint">${item.path.includes("/") ? item.path.slice(0, item.path.lastIndexOf("/")) : ""}</span></span>
                        ${countFor(item.path) > 0 && html`<${Tag} round intent="primary">${countFor(item.path)}<//>`}
                    </div>`)}
                ${unchanged.length > 0 && html`
                    <div role="option" aria-selected=${showUnchanged} className=${"ann-file" + (showUnchanged ? " selected" : "")} onClick=${() => onSelect(UNCHANGED)}>
                        <${Tag} minimal>·<//>
                        <span className="ann-file-name">No longer changed</span>
                        ${unresolvedUnchanged > 0 && html`<${Tag} round intent="primary">${unresolvedUnchanged}<//>`}
                    </div>`}
            </div>
            <div className="ann-main">
                ${showUnchanged
                    ? unchangedView
                    : html`<${DiffFileView} key=${file.path} file=${file} notes=${notes.filter(note => note.file === file.path)} mutations=${mutations} />`}
            </div>
        </div>`;
}

function statusLetter(status) {
    return { added: "A", deleted: "D", renamed: "R", untracked: "U" }[status] ?? "M";
}

function statusIntent(status) {
    return { added: "success", untracked: "success", deleted: "danger", renamed: "warning" }[status] ?? "primary";
}

function DiffFileView({ file, notes, mutations }) {
    // One row per diff line; a hunk header row between hunks. `group` keeps a range inside one hunk.
    const rows = useMemo(() => {
        const list = [];
        file.hunks.forEach((hunk, group) => hunk.lines.forEach(line => list.push({ index: list.length, group, line })));
        return list;
    }, [file]);

    const placement = useMemo(() => {
        const placed = new Map();
        const loose = [];
        for (const note of notes) {
            let at = -1;
            if (!note.outdated) rows.forEach(row => { if (numberOf(row.line, note.side) === note.endLine) at = row.index; });
            if (at < 0) loose.push(note);
            else placed.set(at, [...(placed.get(at) ?? []), note]);
        }
        return { placed, loose };
    }, [rows, notes]);

    const noted = useMemo(() => {
        const set = new Set();
        for (const note of notes) {
            if (note.status === "resolved" || note.outdated) continue;
            rows.forEach(row => { const number = numberOf(row.line, note.side); if (number != null && number >= note.startLine && number <= note.endLine) set.add(row.index); });
        }
        return set;
    }, [rows, notes]);

    // The range from row `a` to row `b`: its side is the side of the row the selection started on.
    const rangeOf = (a, b) => {
        const side = sideOf(rows[a].line);
        const [from, to] = a <= b ? [a, b] : [b, a];
        const numbers = rows.slice(from, to + 1).map(row => numberOf(row.line, side)).filter(number => number != null);
        const endLine = Math.max(...numbers);
        let last = to;
        for (let index = to; index >= from; index--) if (numberOf(rows[index].line, side) === endLine) { last = index; break; }
        return { side, startLine: Math.min(...numbers), endLine, from, to, last };
    };

    const header = html`
        <div style=${{ display: "flex", gap: 8, alignItems: "center", marginBottom: 8 }}>
            <strong><${FileLink} path=${file.path}>${file.path}<//></strong>
            ${file.oldPath && html`<span className="ann-hint">from ${file.oldPath}</span>`}
            <${Tag} minimal intent=${statusIntent(file.status)}>${file.status}<//>
            <span className="ann-hint">+${file.additions} −${file.deletions}</span>
        </div>`;

    if (file.binary || file.tooLarge) {
        return html`${header}<${Callout} icon="info-sign">${file.binary ? "A binary file: no lines to comment on." : "This file is too large to show here."}<//>
            <${LooseNotes} notes=${notes} mutations=${mutations} />`;
    }

    return html`
        ${header}
        <${LooseNotes} notes=${placement.loose} mutations=${mutations} />
        <${LineTable}
            rows=${rows}
            file=${file.path}
            rangeOf=${rangeOf}
            placed=${placement.placed}
            mutations=${mutations}
            renderRow=${(row, props, plus) => {
                const { line } = row;
                const first = row.index === 0 || rows[row.index - 1].group !== row.group;
                const kind = line.k === "+" ? " ann-add" : line.k === "-" ? " ann-del" : "";
                return html`
                    ${first && html`<tr className="ann-hunk"><td colSpan=${4}>${file.hunks[row.group].header}</td></tr>`}
                    <tr ...${props} className=${props.className + kind + (noted.has(row.index) ? " ann-noted" : "")}>
                        <td className="ann-num">${line.o ?? ""}</td>
                        <td className="ann-num">${line.n ?? ""}</td>
                        <td className="ann-gutter">${plus}</td>
                        <td className="ann-code">${line.k === " " ? " " : line.k}${line.t}</td>
                    </tr>`;
            }}
            columns=${4}
        />`;
}

// ---------------------------------------------------------------- Plan

function PlanReview({ view, notes, mutations }) {
    const rows = useMemo(() => view.blocks.map((block, index) => ({ index, group: 0, block })), [view]);
    const placement = useMemo(() => {
        const placed = new Map();
        const loose = [];
        for (const note of notes) {
            let at = -1;
            if (!note.outdated) rows.forEach(row => { if (row.block.start <= note.endLine) at = row.index; });
            if (at < 0) loose.push(note);
            else placed.set(at, [...(placed.get(at) ?? []), note]);
        }
        return { placed, loose };
    }, [rows, notes]);

    const noted = useMemo(() => {
        const set = new Set();
        for (const note of notes) {
            if (note.status === "resolved" || note.outdated) continue;
            rows.forEach(row => { if (row.block.end >= note.startLine && row.block.start <= note.endLine) set.add(row.index); });
        }
        return set;
    }, [rows, notes]);

    const rangeOf = (a, b) => {
        const [from, to] = a <= b ? [a, b] : [b, a];
        return { side: "new", startLine: rows[from].block.start, endLine: rows[to].block.end, from, to, last: to };
    };

    if (rows.length === 0) return html`<${NonIdealState} icon="document" title="The plan is empty" />`;
    return html`
        <div className="ann-main">
            <div style=${{ display: "flex", gap: 8, alignItems: "center", marginBottom: 8 }}>
                <strong><${FileLink} path=${view.planPath}>${view.planPath}<//></strong>
            </div>
            <${LooseNotes} notes=${placement.loose} mutations=${mutations} />
            <${LineTable}
                rows=${rows}
                file=${view.planPath}
                rangeOf=${rangeOf}
                placed=${placement.placed}
                mutations=${mutations}
                columns=${3}
                className="ann-plan"
                renderRow=${(row, props, plus) => html`
                    <tr ...${props} className=${props.className + " ann-block" + (noted.has(row.index) ? " ann-noted" : "")}>
                        <td className="ann-num">${row.block.start === row.block.end ? row.block.start : `${row.block.start}–${row.block.end}`}</td>
                        <td className="ann-gutter">${plus}</td>
                        <td className="ann-md">
                            ${row.block.kind === "frontmatter"
                                ? html`<pre style=${{ margin: 0, opacity: .7 }}>${row.block.text}</pre>`
                                : html`<${Markdown} source=${row.block.text} />`}
                        </td>
                    </tr>`}
            />
        </div>`;
}

// ---------------------------------------------------------------- Rows, selection and the editor

// A table of commentable rows: the + of a row starts a comment, a drag or a Shift-click makes it a range (inside one group), `c` on a
// focused row starts one, the arrows move between rows. Threads and the editor are drawn under the last line of their range.
function LineTable({ rows, file, rangeOf, placed, mutations, renderRow, columns, className }) {
    const [drag, setDrag] = useState(null);
    const [editor, setEditor] = useState(null);
    const [focused, setFocused] = useState(null);
    const anchor = useRef(null);
    const dragging = useRef(null);
    const tableRef = useRef(null);

    const open = (a, b) => {
        if (rows[a].group !== rows[b].group) b = a;
        setEditor(rangeOf(a, b));
    };

    // A drag starts on a +, grows over the rows of its group, and ends wherever the mouse is let go. The listener is added at once,
    // not in an effect, so a quick click that lets go before the next render still opens the editor.
    const startDrag = index => {
        dragging.current = { from: index, to: index };
        setDrag(dragging.current);
        window.addEventListener("mouseup", () => {
            const range = dragging.current;
            dragging.current = null;
            setDrag(null);
            if (range) open(range.from, range.to);
        }, { once: true });
    };

    const focusRow = index => tableRef.current?.querySelector(`tr[data-row="${index}"]`)?.focus();

    const selection = drag ? [Math.min(drag.from, drag.to), Math.max(drag.from, drag.to)] : editor ? [editor.from, editor.to] : null;

    const propsFor = row => ({
        key: "row-" + row.index,
        "data-row": row.index,
        tabIndex: 0,
        className: "ann-row" + (focused === row.index ? " focused" : "") + (selection && row.index >= selection[0] && row.index <= selection[1] ? " ann-selected" : ""),
        onFocus: () => { setFocused(row.index); anchor.current = row.index; },
        onMouseDown: () => { anchor.current = row.index; },
        onBlur: () => setFocused(current => current === row.index ? null : current),
        onMouseEnter: () => {
            if (!dragging.current || rows[dragging.current.from].group !== row.group) return;
            dragging.current = { ...dragging.current, to: row.index };
            setDrag(dragging.current);
        },
        onKeyDown: event => {
            if (event.target !== event.currentTarget) return;
            if (event.key === "c" && !event.ctrlKey && !event.metaKey && !event.altKey) { event.preventDefault(); open(row.index, row.index); }
            else if (event.key === "ArrowDown") { event.preventDefault(); focusRow(Math.min(rows.length - 1, row.index + 1)); }
            else if (event.key === "ArrowUp") { event.preventDefault(); focusRow(Math.max(0, row.index - 1)); }
        },
    });

    const plusFor = row => html`<button type="button" className="ann-plus" aria-label="Comment on this line" title="Comment (c) · drag or Shift-click for a range"
            onMouseDown=${event => {
                event.preventDefault();
                event.stopPropagation();
                if (event.shiftKey && anchor.current != null && rows[anchor.current]?.group === row.group) { open(anchor.current, row.index); return; }
                anchor.current = row.index;
                startDrag(row.index);
            }}
            onClick=${event => { if (event.detail === 0) open(row.index, row.index); /* Enter or Space on the button */ }}>+</button>`;

    return html`
        <table className=${"ann-table" + (className ? " " + className : "")} ref=${tableRef}>
            <tbody>
                ${rows.map(row => html`
                    ${renderRow(row, propsFor(row), plusFor(row))}
                    ${(placed.get(row.index) ?? []).map(note => html`
                        <tr key=${"note-" + note.id} className="ann-thread"><td colSpan=${columns}>
                            <${NoteCard} note=${note} mutations=${mutations} />
                        </td></tr>`)}
                    ${editor && editor.last === row.index && html`
                        <tr className="ann-thread"><td colSpan=${columns}>
                            <${Editor}
                                label=${`${file} · ${editor.startLine === editor.endLine ? `line ${editor.startLine}` : `lines ${editor.startLine}–${editor.endLine}`}${editor.side === "old" ? " (old)" : ""}`}
                                onCancel=${() => { setEditor(null); focusRow(row.index); }}
                                onSave=${async body => {
                                    const note = await mutations.add({ file, side: editor.side, startLine: editor.startLine, endLine: editor.endLine, body });
                                    if (note) { setEditor(null); focusRow(row.index); }
                                }} />
                        </td></tr>`}`)}
            </tbody>
        </table>`;
}

function Editor({ label, initial, onSave, onCancel, saveText }) {
    const [text, setText] = useState(initial ?? "");
    const [busy, setBusy] = useState(false);
    const ref = useRef(null);
    useEffect(() => { ref.current?.focus(); }, []);
    const save = async () => {
        if (!text.trim() || busy) return;
        setBusy(true);
        try { await onSave(text); } finally { setBusy(false); }
    };
    return html`
        <${Card} compact className="ann-note" style=${{ display: "flex", flexDirection: "column", gap: 6 }}>
            ${label && html`<div className="ann-hint">${label}</div>`}
            <${TextArea} inputRef=${ref} fill autoResize value=${text} placeholder="Write a comment (Markdown). Enter saves, Shift+Enter adds a line, Esc cancels."
                aria-label="Comment" onChange=${event => setText(event.target.value)}
                onKeyDown=${event => {
                    if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); onCancel(); }
                    else if (event.key === "Enter" && !event.shiftKey) { event.preventDefault(); void save(); }
                }} />
            <div style=${{ display: "flex", gap: 6 }}>
                <${Button} size="small" intent="primary" loading=${busy} disabled=${!text.trim()} onClick=${save}>${saveText ?? "Comment"}<//>
                <${Button} size="small" variant="minimal" onClick=${onCancel}>Cancel<//>
            </div>
        <//>`;
}

// ---------------------------------------------------------------- Threads

function NoteCard({ note, mutations }) {
    const [editing, setEditing] = useState(false);
    const [expanded, setExpanded] = useState(false);
    const lines = note.startLine === note.endLine ? `line ${note.startLine}` : `lines ${note.startLine}–${note.endLine}`;
    const resolved = note.status === "resolved";

    if (resolved && !expanded) {
        return html`
            <div className="ann-note" style=${{ display: "flex", gap: 6, alignItems: "center", opacity: .7 }} data-note=${note.id}>
                <${Icon} name="check" style=${{ flex: "none" }} />
                <${Tag} minimal style=${{ flex: "none" }}>${note.id}<//>
                <span className="ann-hint" style=${{ flex: 1, minWidth: 0, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>${lines} · resolved · ${firstLine(note.body)}</span>
                <${Button} size="small" variant="minimal" style=${{ flex: "none" }} onClick=${() => setExpanded(true)}>Show<//>
            </div>`;
    }

    if (editing) {
        return html`<${Editor} label=${`Edit [${note.id}] · ${lines}`} initial=${note.body} saveText="Save"
            onCancel=${() => setEditing(false)} onSave=${async body => { await mutations.update(note.id, body); setEditing(false); }} />`;
    }

    return html`
        <${Card} compact className="ann-note" data-note=${note.id}
            style=${{ borderLeft: `3px solid ${resolved ? "rgba(128,128,128,.5)" : note.outdated ? "#c87619" : "#2d72d2"}` }}>
            <div className="ann-note-head">
                <${Tag} minimal intent="primary">${note.id}<//>
                <span className="ann-hint">${lines}${note.kind === "diff" && note.side === "old" ? " (old)" : ""}</span>
                <${Tag} minimal intent=${note.status === "sent" ? "warning" : resolved ? "none" : "primary"}>${note.status}<//>
                ${note.outdated && html`<${Tag} minimal intent="warning" title="The commented lines changed or are gone">outdated<//>`}
                ${note.addressed && html`<${Tag} minimal intent="success">addressed<//>`}
                <div className="ann-note-actions">
                    ${!resolved && html`<${Button} size="small" variant="minimal" icon="edit" title="Edit" aria-label="Edit" onClick=${() => setEditing(true)} />`}
                    <${Button} size="small" variant="minimal" icon="trash" title="Delete" aria-label="Delete" onClick=${() => mutations.remove(note.id)} />
                    ${resolved
                        ? html`<${Button} size="small" variant="minimal" icon="undo" onClick=${() => { setExpanded(false); void mutations.resolve(note.id, false); }}>Reopen<//>`
                        : html`<${Button} size="small" variant="minimal" icon="tick" intent="success" onClick=${() => mutations.resolve(note.id, true)}>Resolve<//>`}
                </div>
            </div>
            ${note.outdated && html`<pre className="ann-hint" style=${{ margin: "0 0 4px", whiteSpace: "pre-wrap" }}>${note.anchor}</pre>`}
            <${Markdown} source=${note.body} />
            ${(note.replies ?? []).map((reply, index) => html`
                <div key=${index} className="ann-reply" data-reply=${note.id}>
                    <div className="ann-hint"><${Icon} name=${reply.author === "agent" ? "bot" : "user"} /> ${reply.author === "agent" ? "Agent" : "You"}</div>
                    <${Markdown} source=${reply.body} />
                </div>`)}
        <//>`;
}

// Comments whose lines are not in what is shown: outdated ones, and ones outside the hunks of the diff.
function LooseNotes({ notes, mutations }) {
    if (notes.length === 0) return null;
    const outdated = notes.filter(note => note.outdated);
    const outside = notes.filter(note => !note.outdated);
    return html`
        ${outdated.length > 0 && html`<div className="ann-section">
            <div className="ann-hint" style=${{ marginBottom: 4 }}>Outdated: the commented lines changed or are gone. They are still sent until resolved.</div>
            ${outdated.map(note => html`<div key=${note.id} style=${{ marginBottom: 6 }}><${NoteCard} note=${note} mutations=${mutations} /></div>`)}
        </div>`}
        ${outside.length > 0 && html`<div className="ann-section">
            <div className="ann-hint" style=${{ marginBottom: 4 }}>Not in the shown diff</div>
            ${outside.map(note => html`<div key=${note.id} style=${{ marginBottom: 6 }}><${NoteCard} note=${note} mutations=${mutations} /></div>`)}
        </div>`}`;
}

function firstLine(text) {
    const line = (text ?? "").split("\n")[0].trim();
    return line.length > 80 ? line.slice(0, 80) + "…" : line;
}
