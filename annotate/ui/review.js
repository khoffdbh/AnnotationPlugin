import { Callout } from "@blueprintjs/core";
import { html, useRpc } from "codealta";

export default function Review() {
    const context = useRpc("review.context");
    if (context.error) return html`<${Callout} intent="danger">${context.error.message}<//>`;
    if (!context.data) return html`<div style=${{ padding: 16 }}>Loading…</div>`;
    return html`<div style=${{ padding: 16 }}>
        <div data-testid="project-path">Project: ${context.data.projectPath ?? "(none)"}</div>
        <div>Session: ${context.data.sessionId ?? "(none)"}</div>
    </div>`;
}
