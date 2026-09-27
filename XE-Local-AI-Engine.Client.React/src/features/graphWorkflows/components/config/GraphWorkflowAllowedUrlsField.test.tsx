// @vitest-environment jsdom

// A web_fetch Tool node's allow-list: shown only for the tool that reads it, locked behind a one-time prompt-injection
// notice the operator acknowledges through the per-user tutorial state, and validated the way the server's save gate is.

import { fireEvent, screen, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { describe, expect, it, vi } from "vitest";

import { GraphWorkflowToolConfigForm } from "@/features/graphWorkflows/components/config/GraphWorkflowToolConfigForm";
import { defaultNodeData, type GraphWorkflowCanvasNodeData } from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import type { GraphWorkflowToolResponse } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

// The wire key, spelled out: it is what the server stores per user, so a rename here would re-show the notice to everyone.
const allowlistNoticeKey = "web-graph-allowlist-notice";

type ToolNode = Extract<GraphWorkflowCanvasNodeData, { kind: "Tool" }>;

const tools: readonly GraphWorkflowToolResponse[] = [
	{
		name: "read_file",
		description: "Reads a text file.",
		parameterSchema: '{"type":"object","properties":{"path":{"type":"string"}}}',
		requiresAllowedUrls: false,
	},
	{
		name: "web_fetch",
		description: "Download one public web page.",
		parameterSchema: '{"type":"object","properties":{"url":{"type":"string"}}}',
		requiresAllowedUrls: true,
	},
];

function toolNode(patch: Partial<ToolNode>): ToolNode {
	return { ...(defaultNodeData("Tool", "fetch-1") as ToolNode), ...patch };
}

function tutorialState(entries: readonly { key: string; status: string }[]) {
	return http.get(localApiPath("tutorial-state"), () =>
		HttpResponse.json({ entries: entries.map((entry) => ({ ...entry, atUtc: "2026-09-27T00:00:00Z" })) }),
	);
}

function renderForm(node: ToolNode, onChange = vi.fn(), errorFor: (field: string) => string | undefined = () => undefined) {
	return renderWithProviders(
		<GraphWorkflowToolConfigForm node={node} onChange={onChange} errorFor={errorFor} onTouch={vi.fn()} tools={tools} />,
	);
}

describe("GraphWorkflowAllowedUrlsField", () => {
	it("is not shown for a tool that reads no allow-list", () => {
		renderForm(toolNode({ toolName: "read_file" }));

		expect(screen.queryByTestId("gw-node-config-allowed-urls")).toBeNull();
	});

	it("shows the prompt-injection notice once and records the acknowledgement before the list can be edited", async () => {
		const saved: unknown[] = [];
		server.use(
			tutorialState([]),
			http.put(localApiPath("tutorial-state"), async ({ request }) => {
				saved.push(await request.json());
				return new HttpResponse(null, { status: 204 });
			}),
		);
		const onChange = vi.fn();
		renderForm(toolNode({ toolName: "web_fetch" }), onChange);

		expect(await screen.findByText("Pages on these links reach the model unreviewed")).toBeTruthy();
		expect(
			screen.getByText(
				"A workflow run fetches these pages without asking anyone, and their text goes straight to the next model. A page can contain instructions written to mislead that model. List only sites you trust.",
			),
		).toBeTruthy();
		expect((screen.getByTestId("gw-node-config-allowed-url-add") as HTMLButtonElement).disabled).toBe(true);

		fireEvent.click(screen.getByRole("button", { name: "I understand" }));

		await waitFor(() => expect(saved).toEqual([{ key: allowlistNoticeKey, status: "completed" }]));
		expect(screen.queryByTestId("gw-node-config-allowlist-notice")).toBeNull();
		fireEvent.click(screen.getByRole("button", { name: "Add link" }));
		expect(onChange).toHaveBeenCalledWith({ allowedUrls: [""] });
	});

	it("skips the notice for an operator who already acknowledged it and edits a row in place", async () => {
		server.use(tutorialState([{ key: allowlistNoticeKey, status: "completed" }]));
		const onChange = vi.fn();
		renderForm(toolNode({ toolName: "web_fetch", allowedUrls: ["https://docs.example.com/"] }), onChange);

		const row = screen.getByTestId("gw-node-config-allowed-url-0") as HTMLInputElement;
		await waitFor(() => expect(row.disabled).toBe(false));
		expect(screen.queryByTestId("gw-node-config-allowlist-notice")).toBeNull();

		fireEvent.change(row, { target: { value: "https://docs.example.com/guide" } });

		expect(onChange).toHaveBeenCalledWith({ allowedUrls: ["https://docs.example.com/guide"] });
	});

	it("shows the save rule's message under the list", async () => {
		server.use(tutorialState([{ key: allowlistNoticeKey, status: "completed" }]));
		renderForm(toolNode({ toolName: "web_fetch" }), vi.fn(), (field) =>
			field === "allowedUrls"
				? "Add at least one allowed link. A web_fetch node fetches only pages under its allowed links."
				: undefined,
		);

		expect((await screen.findByTestId("gw-node-config-allowed-urls-error")).textContent).toBe(
			"Add at least one allowed link. A web_fetch node fetches only pages under its allowed links.",
		);
	});

	it("drops the list when the node switches to a tool that does not read it", async () => {
		server.use(tutorialState([{ key: allowlistNoticeKey, status: "completed" }]));
		const onChange = vi.fn();
		renderForm(toolNode({ toolName: "web_fetch", allowedUrls: ["https://docs.example.com/"] }), onChange);

		fireEvent.click(screen.getByTestId("gw-node-config-tool"));
		fireEvent.click(await screen.findByRole("option", { name: "read_file", hidden: true }));

		expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ toolName: "read_file", allowedUrls: [] }));
	});
});
