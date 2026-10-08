// @vitest-environment jsdom

import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { afterEach, describe, expect, it } from "vitest";

import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { ContextUsagePopover } from "@/features/chat/components/ContextUsagePopover/ContextUsagePopover";
import type { ContextUsageModel } from "@/features/chat/models/ChatModels";
import type { ContextWindowSnapshot } from "@/features/chat/models/ContextWindowModels";
import { useChatSamplingPreferencesStore } from "@/features/chat/stores/ChatSamplingPreferencesStore";
import en from "@/locales/en.json";
import { jsonRoute, localApiPath, problemDetailsRoute } from "@/test/msw/Handlers";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

// The pre-send estimate runs against MSW through the shared generated client. A test that must not fetch declares no
// route, so a request would fail it as undeclared.
// The MCP server list is ambient here: a last round with an MCP tool asks for display names when it opens.
const server = setupMswServer(jsonRoute("get", "mcp/servers", { items: [] }));
const agentId = "5f0b6a0e-6a3c-4c51-9a3e-2a1f6c1d9b10";

function mcpServerDto(id: string, name: string, slug: string | null) {
	return {
		id,
		name,
		slug,
		description: null,
		transportKind: "Stdio",
		command: "/usr/bin/srv",
		arguments: [],
		workingDirectory: null,
		env: {},
		url: null,
		trustTier: "Sandboxed",
		headers: {},
		sessionScope: "Shared",
		enabled: true,
		version: 1,
		createdAtUtc: 1000,
		updatedAtUtc: 2000,
	};
}
const strings = en.pages.chat.contextUsage;

const lastRound: ContextWindowSnapshot = {
	kind: "LastRound",
	modelId: "qwen3-4b",
	windowTokens: 10_000,
	reservedOutputTokens: 1_000,
	usableWindowTokens: 8_000,
	safetyMarginTokens: 1_000,
	providerInputTokens: 3_000,
	providerOutputTokens: 120,
	estimated: {
		systemPromptTokens: 500,
		instructionsTokens: 0,
		toolSchemaTokens: 800,
		toolTemplatePreambleTokens: 200,
		knowledgeTokens: 0,
		attachmentTokens: 0,
		compactionTokens: 300,
		conversationTokens: 1_100,
		totalTokens: 2_900,
	},
	tools: [
		{ name: "web_search", tokens: 300 },
		{ name: "mcp__weather__forecast", tokens: 400 },
		{ name: "custom__ticket_lookup", tokens: 100 },
		{ name: "load_skill", tokens: 200 },
	],
	toolsWithheldCount: 7,
	trimmed: { messagesDropped: 4, toolResultsTruncated: 0, reasoningStripped: 2 },
};

const preSend: ContextWindowSnapshot = {
	kind: "PreSendEstimate",
	windowTokens: 10_000,
	reservedOutputTokens: 1_000,
	usableWindowTokens: 8_000,
	safetyMarginTokens: 1_000,
	providerInputTokens: null,
	estimated: {
		systemPromptTokens: 500,
		instructionsTokens: 0,
		toolSchemaTokens: 800,
		toolTemplatePreambleTokens: 0,
		knowledgeTokens: 0,
		attachmentTokens: 0,
		compactionTokens: 0,
		conversationTokens: 0,
		totalTokens: 1_300,
	},
	tools: [{ name: "web_search", tokens: 800 }],
	toolsWithheldCount: 0,
};

function usage(overrides: Partial<ContextUsageModel> = {}): ContextUsageModel {
	return {
		usedTokens: 3_120,
		maxTokens: 10_000,
		isAuthoritative: true,
		modelLabel: "Qwen3 4B",
		nodeLabel: "Local node",
		...overrides,
	};
}

function renderPopover(model: ContextUsageModel, agentId?: string) {
	return renderWithProviders(<ContextUsagePopover usage={model} modelName="qwen3-4b" agentId={agentId} useLocalTools={true} />);
}

async function open() {
	fireEvent.click(screen.getByTestId("context-usage-trigger"));
	return screen.findByTestId("context-usage-dropdown");
}

describe("ContextUsagePopover", () => {
	it("keeps the compact badge as a collapsed trigger", () => {
		renderPopover(usage({ contextWindow: lastRound }));

		const trigger = screen.getByTestId("context-usage-trigger");
		expect(trigger.getAttribute("aria-label")).toBe(strings.trigger);
		expect(trigger.getAttribute("aria-expanded")).toBe("false");
		expect(within(trigger).getByTestId("context-usage-badge-label").textContent).toContain("3.1k/10k 31%");
		expect(screen.queryByTestId("context-usage-dropdown")).toBeNull();
	});

	it("opens on click and closes on Escape and on an outside click", async () => {
		renderPopover(usage({ contextWindow: lastRound }));

		await open();
		expect(screen.getByTestId("context-usage-trigger").getAttribute("aria-expanded")).toBe("true");
		fireEvent.keyDown(screen.getByTestId("context-usage-dropdown"), { key: "Escape" });
		await waitFor(() => expect(screen.queryByTestId("context-usage-dropdown")).toBeNull());

		await open();
		fireEvent.mouseDown(document.body);
		await waitFor(() => expect(screen.queryByTestId("context-usage-dropdown")).toBeNull());
		expect(screen.getByTestId("context-usage-trigger").getAttribute("aria-expanded")).toBe("false");
	});

	it("bounds the breakdown in a scroll container that keeps the tool disclosure and Escape working", async () => {
		renderPopover(usage({ contextWindow: lastRound }));
		const dropdown = await open();

		const scroll = within(dropdown).getByTestId("context-usage-scroll");
		expect(within(scroll).getByTestId("context-usage-sections")).not.toBeNull();
		expect(within(scroll).getByTestId("context-usage-footer")).not.toBeNull();
		const tools = within(scroll).getByTestId("context-usage-tools") as HTMLDetailsElement;
		fireEvent.click(within(tools).getByText(strings.toolsSent.replace("{{count}}", "4")));
		expect(tools.open).toBe(true);

		fireEvent.keyDown(dropdown, { key: "Escape" });
		await waitFor(() => expect(screen.queryByTestId("context-usage-dropdown")).toBeNull());
	});

	it("renders the last round: sections, rows, grouped tools, withheld and trimmed counters", async () => {
		renderPopover(usage({ contextWindow: lastRound, agentLabel: "Researcher" }));
		const dropdown = await open();

		expect(within(dropdown).getByTestId("context-usage-model").textContent).toBe("qwen3-4b");
		expect(within(dropdown).getByTestId("context-usage-subtitle").textContent).toBe("Researcher · Qwen3 4B");
		expect(within(dropdown).getByTestId("context-usage-source").textContent).toContain(strings.source.reported);
		expect(within(dropdown).getByTestId("context-usage-window").textContent).toContain("10k");
		for (const key of ["systemPrompt", "tools", "compaction", "conversation", "reservedOutput", "safetyMargin", "free"]) {
			expect(within(dropdown).getByTestId(`context-usage-section-${key}`)).not.toBeNull();
		}
		expect(within(dropdown).queryByTestId("context-usage-section-knowledge")).toBeNull();
		expect(within(dropdown).getByTestId("context-usage-row-tools").textContent).toContain(`${strings.section.tools}1k · 10%`);
		expect(within(dropdown).getByTestId("context-usage-reported-input").textContent).toContain("3k");
		expect(within(dropdown).getByTestId("context-usage-remaining").textContent).toContain(`${strings.remaining}5k`);

		const tools = within(dropdown).getByTestId("context-usage-tools");
		expect(tools.textContent).toContain(strings.toolsSent.replace("{{count}}", "4"));
		expect(within(tools).getByTestId("context-usage-tool-group-builtIn").textContent).toContain("web_search");
		expect(within(tools).getByTestId("context-usage-tool-group-mcp").textContent).toContain(
			`${strings.toolGroup.mcp.replace("{{server}}", "weather")}400forecast400`,
		);
		expect(within(tools).getByTestId("context-usage-tool-group-custom").textContent).toContain("ticket_lookup");
		expect(within(tools).getByTestId("context-usage-tool-group-skills").textContent).toContain("load_skill");

		expect(within(dropdown).getByTestId("context-usage-withheld").textContent).toContain(
			strings.withheld.replace("{{count}}", "7"),
		);
		const trimmed = within(dropdown).getByTestId("context-usage-trimmed");
		expect(trimmed.textContent).toContain(strings.trimmed.messagesDropped.replace("{{count}}", "4"));
		expect(trimmed.textContent).toContain(strings.trimmed.reasoningStripped.replace("{{count}}", "2"));
		expect(trimmed.textContent).not.toContain(strings.trimmed.toolResultsTruncated.replace("{{count}}", "0"));
		expect(within(dropdown).getByTestId("context-usage-footer").textContent).toContain(strings.footer.reported);
	});

	it("loads the pre-send estimate for the model and agent and labels it estimated", async () => {
		let requested: URL | undefined;
		server.use(
			http.get(localApiPath("chat/context-estimate"), ({ request }) => {
				requested = new URL(request.url);
				return HttpResponse.json(preSend);
			}),
		);
		renderPopover(usage({ usedTokens: undefined, isAuthoritative: false }), agentId);
		const dropdown = await open();

		expect(await within(dropdown).findByTestId("context-usage-row-systemPrompt")).not.toBeNull();
		expect(requested?.searchParams.get("modelName")).toBe("qwen3-4b");
		expect(requested?.searchParams.get("agentId")).toBe(agentId);
		expect(requested?.searchParams.get("useLocalTools")).toBe("true");
		expect(within(dropdown).getByTestId("context-usage-source").textContent).toContain(strings.source.estimated);
		expect(within(dropdown).getByTestId("context-usage-remaining").textContent).toContain("6.7k");
		expect(within(dropdown).queryByTestId("context-usage-reported-input")).toBeNull();
		expect(within(dropdown).getByTestId("context-usage-footer").textContent).toContain(strings.footer.estimated);
	});

	describe("developer-mode overrides", () => {
		afterEach(() => {
			useDeveloperModeStore.getState().actions.setDeveloperMode(false);
			useChatSamplingPreferencesStore.getState().actions.reset();
		});

		async function requestedEstimate(): Promise<URL | undefined> {
			let requested: URL | undefined;
			server.use(
				http.get(localApiPath("chat/context-estimate"), ({ request }) => {
					requested = new URL(request.url);
					return HttpResponse.json(preSend);
				}),
			);
			useChatSamplingPreferencesStore.getState().actions.setField("maxOutputTokens", 2_048);
			useChatSamplingPreferencesStore.getState().actions.setField("numCtx", 16_384);
			renderPopover(usage({ usedTokens: undefined, isAuthoritative: false }));
			const dropdown = await open();
			// findBy* throws when the estimate never renders, so the request is known to have completed.
			await within(dropdown).findByTestId("context-usage-row-systemPrompt");
			return requested;
		}

		it("sends the output reserve and window overrides when developer mode is on", async () => {
			useDeveloperModeStore.getState().actions.setDeveloperMode(true);
			const requested = await requestedEstimate();

			expect(requested?.searchParams.get("maxOutputTokens")).toBe("2048");
			expect(requested?.searchParams.get("numCtx")).toBe("16384");
		});

		it("sends neither override when developer mode is off, as the send path does", async () => {
			const requested = await requestedEstimate();

			expect(requested).toBeDefined();
			expect(requested?.searchParams.has("maxOutputTokens")).toBe(false);
			expect(requested?.searchParams.has("numCtx")).toBe(false);
		});
	});

	it("asks for an estimate without local tools when the composer's toggle is off", async () => {
		let requested: URL | undefined;
		server.use(
			http.get(localApiPath("chat/context-estimate"), ({ request }) => {
				requested = new URL(request.url);
				// What the node answers for useLocalTools=false: no tool schema, no tools.
				return HttpResponse.json({
					...preSend,
					estimated: { ...preSend.estimated, toolSchemaTokens: 0, totalTokens: 500 },
					tools: [],
				});
			}),
		);
		renderWithProviders(
			<ContextUsagePopover
				usage={usage({ usedTokens: undefined, isAuthoritative: false })}
				modelName="qwen3-4b"
				useLocalTools={false}
			/>,
		);
		const dropdown = await open();

		expect(await within(dropdown).findByTestId("context-usage-row-systemPrompt")).not.toBeNull();
		expect(requested?.searchParams.get("useLocalTools")).toBe("false");
		expect(within(dropdown).queryByTestId("context-usage-row-tools")).toBeNull();
		expect(within(dropdown).queryByTestId("context-usage-section-tools")).toBeNull();
		expect(within(dropdown).queryByTestId("context-usage-tools")).toBeNull();
	});

	it("requests the estimate for the node default model without a model name", async () => {
		let requested: URL | undefined;
		server.use(
			http.get(localApiPath("chat/context-estimate"), ({ request }) => {
				requested = new URL(request.url);
				return HttpResponse.json(preSend);
			}),
		);
		renderWithProviders(
			<ContextUsagePopover usage={usage({ usedTokens: undefined, isAuthoritative: false })} modelName="" useLocalTools={true} />,
		);
		const dropdown = await open();

		expect(await within(dropdown).findByTestId("context-usage-row-systemPrompt")).not.toBeNull();
		expect(requested?.searchParams.has("modelName")).toBe(false);
		expect(requested?.searchParams.has("agentId")).toBe(false);
	});

	it("shows the no-data state with an unknown window when the node has no estimate", async () => {
		server.use(problemDetailsRoute("get", "chat/context-estimate", 404, { title: "Not Found" }));
		renderPopover(usage({ usedTokens: undefined, maxTokens: undefined, isAuthoritative: false }));
		const dropdown = await open();

		await waitFor(() =>
			expect(within(dropdown).getByTestId("context-usage-empty").textContent).toContain(strings.afterFirstResponse),
		);
		expect(within(dropdown).getByTestId("context-usage-window").textContent).toContain(strings.unknownValue);
		expect(within(dropdown).getByTestId("context-usage-source").textContent).toContain(strings.source.unknown);
	});

	it("renders an unknown capacity as unknown, never as zero", async () => {
		renderPopover(
			usage({
				maxTokens: undefined,
				contextWindow: { ...lastRound, windowTokens: 0, usableWindowTokens: 0, reservedOutputTokens: 0, safetyMarginTokens: 0 },
			}),
		);
		const dropdown = await open();

		expect(within(dropdown).getByTestId("context-usage-window").textContent).toContain(strings.unknownValue);
		expect(within(dropdown).getByTestId("context-usage-remaining").textContent).toContain(strings.unknownValue);
		expect(within(dropdown).queryByTestId("context-usage-sections")).toBeNull();
		expect(within(dropdown).getByTestId("context-usage-row-systemPrompt").textContent).toContain("500");
		expect(within(dropdown).getByTestId("context-usage-row-systemPrompt").textContent).not.toContain("%");
	});

	it("names an MCP group by its server when the slug matches, else by the slug", async () => {
		server.use(
			jsonRoute("get", "mcp/servers", {
				items: [
					mcpServerDto("6a1d2c3e-0000-4000-8000-000000000001", "Weather Service", "weather"),
					mcpServerDto("6a1d2c3e-0000-4000-8000-000000000002", "Never connected", null),
				],
			}),
		);
		renderPopover(
			usage({
				contextWindow: {
					...lastRound,
					tools: [
						{ name: "mcp__weather__forecast", tokens: 400 },
						{ name: "mcp__docs__search", tokens: 50 },
					],
				},
			}),
		);
		const dropdown = await open();

		const groups = within(dropdown).getAllByTestId("context-usage-tool-group-mcp");
		await waitFor(() =>
			expect(groups.map((group) => group.textContent)).toEqual([
				expect.stringContaining(strings.toolGroup.mcp.replace("{{server}}", "docs")),
				expect.stringContaining(strings.toolGroup.mcp.replace("{{server}}", "Weather Service")),
			]),
		);
	});

	it("shows the selected model's estimate when the last round belongs to another model", async () => {
		server.use(jsonRoute("get", "chat/context-estimate", { ...preSend, modelId: "llama-3-8b" }));
		renderWithProviders(
			<ContextUsagePopover
				usage={usage({ contextWindow: lastRound, modelLabel: "Llama 3 8B" })}
				modelName="llama-3-8b"
				useLocalTools={true}
			/>,
		);
		const dropdown = await open();

		await waitFor(() => expect(within(dropdown).getByTestId("context-usage-source").textContent).toBe(strings.source.estimated));
		expect(within(dropdown).getByTestId("context-usage-model").textContent).toBe("llama-3-8b");
		expect(within(dropdown).queryByTestId("context-usage-withheld")).toBeNull();
		expect(within(dropdown).getByTestId("context-usage-remaining").textContent).toContain("6.7k");
	});
});
