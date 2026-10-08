// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import i18next from "i18next";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import {
	NodeSettingsFieldsCard,
	type NodeSettingsFieldsCardProps,
} from "@/features/node-settings/components/NodeSettingsFieldsCard";
import { NodeSettingsNumberField } from "@/features/node-settings/components/NodeSettingsNumberField";
import {
	type NodeSettingsFieldsForm,
	toNodeSettingsFieldBounds,
	toNodeSettingsFieldsForm,
	type UsageRateRow,
} from "@/features/node-settings/models/NodeSettingsFieldsModel";
import type { NodeSettingsSectionId } from "@/features/node-settings/models/NodeSettingsSections";
import { nonEnglishLocales } from "@/test/Locales";
import { testMantineTheme } from "@/test/MantineTestRender";

// Deterministic i18n: t returns the supplied default (with {{var}} interpolation applied) so the human copy is
// asserted, not the raw key — this doubles as the i18n-keys-resolve check (the card never renders a bare dotted key).
vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (_key: string, fallback?: string, vars?: Record<string, unknown>) => {
			const text = fallback ?? _key;
			if (vars === undefined) {
				return text;
			}
			return Object.entries(vars).reduce(
				(acc, [name, value]) => acc.replace(new RegExp(`{{${name}}}`, "g"), String(value)),
				text,
			);
		},
	}),
}));

function installJsdomEnvironmentMocks(): void {
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn().mockImplementation((query: string) => ({
			matches: false,
			media: query,
			onchange: null,
			addEventListener: vi.fn(),
			removeEventListener: vi.fn(),
			dispatchEvent: vi.fn(),
		})),
	});
	Object.defineProperty(window, "ResizeObserver", {
		writable: true,
		value: class ResizeObserverMock {
			observe = vi.fn();

			unobserve = vi.fn();

			disconnect = vi.fn();
		},
	});
}

interface RenderOverrides {
	section?: NodeSettingsSectionId;
	onDownloadRecommendedReranker?: () => void;
	isDownloadRecommendedRerankerPending?: boolean;
	isRecommendedRerankerInFlight?: boolean;
	onDownloadRecommendedEmbedding?: () => void;
	isDownloadRecommendedEmbeddingPending?: boolean;
	isRecommendedEmbeddingInFlight?: boolean;
	form?: NodeSettingsFieldsForm;
	onChange?: ReturnType<typeof vi.fn>;
	errors?: Record<string, string>;
	keepWarmModelOptions?: NodeSettingsFieldsCardProps["keepWarmModelOptions"];
	ollamaRuntimeDisabled?: boolean;
	autoEffortFastModelOptions?: NodeSettingsFieldsCardProps["autoEffortFastModelOptions"];
	backgroundModelOptions?: NodeSettingsFieldsCardProps["backgroundModelOptions"];
}

function renderCard(overrides: RenderOverrides = {}): {
	onDownload: () => void;
	onDownloadEmbedding: () => void;
	onChange: ReturnType<typeof vi.fn>;
} {
	const onDownload = overrides.onDownloadRecommendedReranker ?? vi.fn();
	const onDownloadEmbedding = overrides.onDownloadRecommendedEmbedding ?? vi.fn();
	const onChange = overrides.onChange ?? vi.fn();
	render(
		<MantineProvider env="test" theme={testMantineTheme}>
			<NodeSettingsFieldsCard
				section={overrides.section ?? "chat"}
				form={overrides.form ?? toNodeSettingsFieldsForm(undefined)}
				bounds={toNodeSettingsFieldBounds(undefined)}
				errors={overrides.errors ?? {}}
				onChange={onChange as unknown as NodeSettingsFieldsCardProps["onChange"]}
				onApplyPreset={vi.fn()}
				showDeveloperFields={false}
				draftModelOptions={[]}
				keepWarmModelOptions={overrides.keepWarmModelOptions ?? []}
				autoEffortFastModelOptions={overrides.autoEffortFastModelOptions ?? []}
				backgroundModelOptions={overrides.backgroundModelOptions ?? []}
				rerankerModelOptions={[]}
				onDownloadRecommendedReranker={onDownload}
				isDownloadRecommendedRerankerPending={overrides.isDownloadRecommendedRerankerPending ?? false}
				isRecommendedRerankerInFlight={overrides.isRecommendedRerankerInFlight ?? false}
				onDownloadRecommendedEmbedding={onDownloadEmbedding}
				isDownloadRecommendedEmbeddingPending={overrides.isDownloadRecommendedEmbeddingPending ?? false}
				isRecommendedEmbeddingInFlight={overrides.isRecommendedEmbeddingInFlight ?? false}
				ollamaRuntimeDisabled={overrides.ollamaRuntimeDisabled ?? false}
			/>
		</MantineProvider>,
	);
	return { onDownload, onDownloadEmbedding, onChange };
}

describe("NodeSettingsFieldsCard — fast model for automatic reasoning effort", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	it("defaults to Off and offers only the llama.cpp chat models it was given", () => {
		renderCard({ section: "chat", autoEffortFastModelOptions: [{ value: "qwen3-1.7b", label: "qwen3-1.7b" }] });

		const select = screen.getByTestId("node-settings-auto-effort-fast-model") as HTMLInputElement;
		expect(select.value).toBe("Off");

		fireEvent.click(select);
		// Scoped to this select's own listbox: the reranker select on the same card also offers an "Off" entry.
		const listbox = screen.getByRole("listbox", { name: "Fast model for automatic reasoning effort", hidden: true });
		expect(within(listbox).getByRole("option", { name: "Off", hidden: true })).toBeTruthy();
		expect(within(listbox).getByRole("option", { name: "qwen3-1.7b", hidden: true })).toBeTruthy();
	});

	it("edits the selection through the generic onChange", () => {
		const { onChange } = renderCard({
			section: "chat",
			autoEffortFastModelOptions: [{ value: "qwen3-1.7b", label: "qwen3-1.7b" }],
		});

		fireEvent.click(screen.getByTestId("node-settings-auto-effort-fast-model"));
		fireEvent.click(screen.getByText("qwen3-1.7b"));

		expect(onChange).toHaveBeenCalledWith("autoEffortFastModelName", "qwen3-1.7b");
	});

	it("keeps a stored model selectable after it was uninstalled", () => {
		// Without the synthetic entry the select would silently read "Off" for a node that is still configured.
		renderCard({
			section: "chat",
			form: { ...toNodeSettingsFieldsForm(undefined), autoEffortFastModelName: "deleted-model" },
			autoEffortFastModelOptions: [],
		});

		fireEvent.click(screen.getByTestId("node-settings-auto-effort-fast-model"));
		expect(screen.getByRole("option", { name: "deleted-model", hidden: true })).toBeTruthy();
	});
});

describe("NodeSettingsFieldsCard — tool-relevance switch", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	it("renders off by default", () => {
		// That the field is NOT restart-gated is pinned in NodeSettingsFieldsModel.test.ts against
		// restartGatedNodeSettingsFields; a queryByTestId for the hint here could not fail, so it is not asserted.
		renderCard({ section: "chat" });

		const toggle = screen.getByTestId("node-settings-tool-relevance-enabled") as HTMLInputElement;
		expect(toggle.checked).toBe(false);
	});

	it("renders on when the form says so", () => {
		renderCard({ section: "chat", form: { ...toNodeSettingsFieldsForm(undefined), toolRelevanceEnabled: true } });

		expect((screen.getByTestId("node-settings-tool-relevance-enabled") as HTMLInputElement).checked).toBe(true);
	});

	it("reports a click through the generic onChange", () => {
		const { onChange } = renderCard({ section: "chat" });

		fireEvent.click(screen.getByTestId("node-settings-tool-relevance-enabled"));

		expect(onChange).toHaveBeenCalledWith("toolRelevanceEnabled", true);
	});
});

describe("NodeSettingsFieldsCard — keep model warm", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	it("renders the live toggle and disables the model and interval controls while off", () => {
		renderCard({ section: "runtime" });

		expect(screen.getByTestId("node-settings-keep-model-warm-enabled")).toBeTruthy();
		expect((screen.getByTestId("node-settings-keep-model-warm-model") as HTMLInputElement).disabled).toBe(true);
		expect((screen.getByTestId("node-settings-keep-model-warm-interval") as HTMLInputElement).disabled).toBe(true);
	});

	it("edits the toggle, llama.cpp model, and interval through the generic onChange", () => {
		const form = { ...toNodeSettingsFieldsForm(undefined), keepModelWarmEnabled: true };
		const { onChange } = renderCard({
			section: "runtime",
			form,
			keepWarmModelOptions: [{ value: "qwen3:8b", label: "qwen3:8b" }],
		});

		fireEvent.click(screen.getByTestId("node-settings-keep-model-warm-enabled"));
		fireEvent.click(screen.getByTestId("node-settings-keep-model-warm-model"));
		fireEvent.click(screen.getByText("qwen3:8b"));
		fireEvent.change(screen.getByTestId("node-settings-keep-model-warm-interval"), { target: { value: "120" } });

		expect(onChange).toHaveBeenCalledWith("keepModelWarmEnabled", false);
		expect(onChange).toHaveBeenCalledWith("keepModelWarmModelName", "qwen3:8b");
		expect(onChange).toHaveBeenCalledWith("keepModelWarmIntervalSeconds", 120);
	});

	it("surfaces the VRAM, live MaxLoadedProcesses capacity, and idle-TTL caveats", () => {
		renderCard({ section: "runtime", form: { ...toNodeSettingsFieldsForm(undefined), llamaMaxLoadedProcesses: 7 } });

		const help = screen.getByTestId("node-settings-keep-model-warm-help").textContent ?? "";
		expect(help).toContain("VRAM");
		expect(help).toContain("one of the configured 7 MaxLoadedProcesses slots");
		expect(help).toContain("below the idle TTL");
	});

	it("marks a stale selected model as unavailable", () => {
		renderCard({
			section: "runtime",
			form: {
				...toNodeSettingsFieldsForm(undefined),
				keepModelWarmEnabled: true,
				keepModelWarmModelName: "deleted-model",
			},
			keepWarmModelOptions: [{ value: "deleted-model", label: "deleted-model (not installed)" }],
			errors: { keepModelWarmModelName: "unavailableKeepWarmModel" },
		});

		expect(screen.getByText("The selected model deleted-model is no longer installed.")).toBeTruthy();
		const listbox = screen.getByRole("listbox", { name: "Model to keep warm", hidden: true });
		expect(screen.getByRole("option", { name: "deleted-model (not installed)", hidden: true })).toBeTruthy();
		expect(listbox).toBeTruthy();
	});
});

describe("NodeSettingsFieldsCard — recommended reranker download", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	it("renders the download button and the recommended-model helper line", () => {
		renderCard({ section: "knowledge" });

		expect(screen.getByTestId("node-settings-reranker-download-recommended")).toBeTruthy();
		// The helper names the recommended model + its extra model server (human copy resolves — no bare i18n key).
		expect(screen.getByText(/bge-reranker-v2-m3/)).toBeTruthy();
	});

	it("invokes the download handler once when the button is clicked", () => {
		const { onDownload } = renderCard({ section: "knowledge" });

		fireEvent.click(screen.getByTestId("node-settings-reranker-download-recommended"));

		expect(onDownload).toHaveBeenCalledTimes(1);
	});

	it("disables the button while the recommended reranker download is in flight (duplicate-guard)", () => {
		renderCard({ section: "knowledge", isRecommendedRerankerInFlight: true });

		expect((screen.getByTestId("node-settings-reranker-download-recommended") as HTMLButtonElement).disabled).toBe(true);
	});

	it("disables the button while the download request is pending", () => {
		renderCard({ section: "knowledge", isDownloadRecommendedRerankerPending: true });

		expect((screen.getByTestId("node-settings-reranker-download-recommended") as HTMLButtonElement).disabled).toBe(true);
	});
});

describe("NodeSettingsFieldsCard — recommended embedding model download", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	it("renders the download button and the recommended-model helper line", () => {
		renderCard({ section: "knowledge" });

		expect(screen.getByTestId("node-settings-embedding-download-recommended")).toBeTruthy();
		// The helper names the recommended model + explains why it's required (human copy resolves — no bare i18n key).
		expect(screen.getByText(/nomic-embed-text-v1\.5/)).toBeTruthy();
	});

	it("invokes the download handler once when the button is clicked", () => {
		const { onDownloadEmbedding } = renderCard({ section: "knowledge" });

		fireEvent.click(screen.getByTestId("node-settings-embedding-download-recommended"));

		expect(onDownloadEmbedding).toHaveBeenCalledTimes(1);
	});

	it("disables the button while the recommended embedding download is in flight (duplicate-guard)", () => {
		renderCard({ section: "knowledge", isRecommendedEmbeddingInFlight: true });

		expect((screen.getByTestId("node-settings-embedding-download-recommended") as HTMLButtonElement).disabled).toBe(true);
	});

	it("disables the button while the download request is pending", () => {
		renderCard({ section: "knowledge", isDownloadRecommendedEmbeddingPending: true });

		expect((screen.getByTestId("node-settings-embedding-download-recommended") as HTMLButtonElement).disabled).toBe(true);
	});
});

describe("NodeSettingsFieldsCard — usage rate editor", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	function formWithRates(rows: NodeSettingsFieldsForm["usageRates"]): NodeSettingsFieldsForm {
		return { ...toNodeSettingsFieldsForm(undefined), usageRates: rows };
	}

	it("renders the empty state and the add affordance when no rates are configured", () => {
		renderCard({ section: "usage" });

		expect(screen.getByTestId("node-settings-usage-rates-card")).toBeTruthy();
		expect(screen.getByTestId("node-settings-usage-rates-empty")).toBeTruthy();
		expect(screen.getByTestId("node-settings-usage-rate-add")).toBeTruthy();
		expect(screen.queryByTestId("node-settings-usage-rate-row")).toBeNull();
	});

	it("appends a blank row when Add rate is clicked", () => {
		const { onChange } = renderCard({ section: "usage" });

		fireEvent.click(screen.getByTestId("node-settings-usage-rate-add"));

		expect(onChange).toHaveBeenCalledTimes(1);
		const call = onChange.mock.calls[0];
		expect(call).toBeDefined();
		const [field, value] = call as [string, UsageRateRow[]];
		expect(field).toBe("usageRates");
		expect(value).toHaveLength(1);
		expect(value[0]).toMatchObject({ modelName: "", inputPer1M: "", outputPer1M: "" });
		expect(typeof value[0]?.id).toBe("string");
	});

	it("edits a row's model name through the generic onChange (whole-array replace)", () => {
		const { onChange } = renderCard({
			section: "usage",
			form: formWithRates([{ id: "a", modelName: "gpt", inputPer1M: 1, outputPer1M: 2 }]),
		});

		fireEvent.change(screen.getByTestId("node-settings-usage-rate-model"), { target: { value: "gpt-5" } });

		expect(onChange).toHaveBeenCalledWith("usageRates", [{ id: "a", modelName: "gpt-5", inputPer1M: 1, outputPer1M: 2 }]);
	});

	it("edits a row's input rate through the number input", () => {
		const { onChange } = renderCard({
			section: "usage",
			form: formWithRates([{ id: "a", modelName: "gpt-5", inputPer1M: 1, outputPer1M: 2 }]),
		});

		fireEvent.change(screen.getByTestId("node-settings-usage-rate-input"), { target: { value: "5" } });

		expect(onChange).toHaveBeenCalledWith("usageRates", [{ id: "a", modelName: "gpt-5", inputPer1M: 5, outputPer1M: 2 }]);
	});

	it("removes a row, sending the reduced array", () => {
		const { onChange } = renderCard({
			section: "usage",
			form: formWithRates([
				{ id: "a", modelName: "gpt-5", inputPer1M: 1, outputPer1M: 2 },
				{ id: "b", modelName: "claude", inputPer1M: 3, outputPer1M: 4 },
			]),
		});

		const removeButton = screen.getAllByTestId("node-settings-usage-rate-remove")[0];
		expect(removeButton).toBeDefined();
		fireEvent.click(removeButton as HTMLElement);

		expect(onChange).toHaveBeenCalledWith("usageRates", [{ id: "b", modelName: "claude", inputPer1M: 3, outputPer1M: 4 }]);
	});

	it("surfaces the rate validation error when the page passes one", () => {
		renderCard({
			section: "usage",
			form: formWithRates([{ id: "a", modelName: "gpt-5", inputPer1M: -1, outputPer1M: 2 }]),
			errors: { usageRates: "rate" },
		});

		// The error block renders whenever the page passes a usageRates error code (the card resolves every field error
		// through the shared errors.<code> i18n lookup, whose test-time fallback is the generic "Invalid value.").
		expect(screen.getByTestId("node-settings-usage-rates-error").textContent).toBe("Invalid value.");
	});
});

describe("NodeSettingsFieldsCard — restart-required badge", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	it("marks a restart-gated field so the operator knows a Save is not live", () => {
		renderCard({ section: "runtime" });

		// chatCacheReuse is seeded once into LlamaServerSupervisorOptions at composition — a save needs a node restart.
		const badge = screen.getByTestId("node-settings-restart-badge-chatCacheReuse");
		expect(badge.textContent?.trim()).toBe("Needs restart");
		expect(badge.getAttribute("title")).toBe("Takes effect after the node restarts.");
		cleanup();

		renderCard({ section: "chat" });
		expect(screen.getByTestId("node-settings-restart-badge-defaultModelName")).toBeTruthy();
		cleanup();

		renderCard({ section: "knowledge" });
		expect(screen.getByTestId("node-settings-restart-badge-rerankerModelName")).toBeTruthy();
	});

	it("does not mark a field that is read live on every call", () => {
		renderCard({ section: "chat" });

		// toolCapableModels is re-read per invocation (OrchestrationResolver) — labelling it would be a lie.
		expect(screen.queryByTestId("node-settings-restart-badge-toolCapableModels")).toBeNull();
		expect(screen.queryByTestId("node-settings-restart-badge-enableTools")).toBeNull();
		cleanup();

		renderCard({ section: "runtime" });
		expect(screen.queryByTestId("node-settings-restart-badge-keepModelWarmIntervalSeconds")).toBeNull();
	});

	it("badges the draft-model fields only once the mode that uses them is selected", () => {
		renderCard({ section: "runtime", form: { ...toNodeSettingsFieldsForm(undefined), speculativeMode: "draft-simple" } });

		expect(screen.getByTestId("node-settings-restart-badge-speculativeDraftModelName")).toBeTruthy();
		expect(screen.getByTestId("node-settings-restart-badge-speculativeDraftMaxTokens")).toBeTruthy();
	});

	it("renders the KV cache type picker with a restart badge", () => {
		renderCard({ section: "runtime" });

		expect(screen.getByTestId("node-settings-kv-cache-type")).toBeTruthy();
		// LlamaServerLaunchPolicyOptions is seeded once at host build, so the operator must be told it needs a restart.
		expect(screen.getByTestId("node-settings-restart-badge-kvCacheType")).toBeTruthy();
	});

	it.each(["draft-dflash", "draft-dspark"])("treats %s as an external-draft mode and shows its draft-model fields", (mode) => {
		renderCard({ section: "runtime", form: { ...toNodeSettingsFieldsForm(undefined), speculativeMode: mode } });

		// Both load a second GGUF, so the draft-model picker and the draft-tokens input must appear for them.
		expect(screen.getByTestId("node-settings-speculative-draft-model")).toBeTruthy();
		expect(screen.getByTestId("node-settings-speculative-draft-max-tokens")).toBeTruthy();
	});
});

describe("NodeSettingsFieldsCard — section split", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	it("renders only the cards of the requested section", () => {
		renderCard({ section: "chat" });

		expect(screen.getByTestId("node-settings-local-chat-card")).toBeTruthy();
		expect(screen.getByTestId("node-settings-auto-effort-fast-model")).toBeTruthy();
		// Runtime tuning and the Ollama endpoint live in their own sections now.
		expect(screen.queryByTestId("node-settings-runtime-card")).toBeNull();
		expect(screen.queryByTestId("node-settings-ollama-endpoint")).toBeNull();
	});

	it("tells the operator how to reach developer-only workspace limits", () => {
		renderCard({ section: "workspaces" });

		expect(screen.getByTestId("node-settings-developer-only-note")).toBeTruthy();
		expect(screen.queryByTestId("node-settings-agent-workspaces-card")).toBeNull();
		// The workflow, session and development limits are not developer settings.
		expect(screen.getByTestId("node-settings-workspace-limits-card")).toBeTruthy();
		expect(screen.getByTestId("node-settings-restart-badge-workSessionMaxConcurrentSessions")).toBeTruthy();
	});
});

describe("NodeSettingsFieldsCard — Ollama gate pass-through", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	it("hides the Ollama endpoint input when the page reports the runtime gated off", () => {
		renderCard({ section: "runtimes", ollamaRuntimeDisabled: true });

		// Proves the boolean survives the card group, not only the runtime card that renders the branch.
		expect(screen.queryByTestId("node-settings-ollama-endpoint")).toBeNull();
		expect(screen.getByTestId("node-settings-ollama-disabled")).toBeTruthy();
	});

	it("renders the Ollama endpoint input when the runtime is not gated off", () => {
		renderCard({ section: "runtimes", ollamaRuntimeDisabled: false });

		expect(screen.getByTestId("node-settings-ollama-endpoint")).toBeTruthy();
		expect(screen.queryByTestId("node-settings-ollama-disabled")).toBeNull();
	});
});

describe("NodeSettingsFieldsCard — curated tunables", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	it.each([
		["chat", ["node-settings-max-provider-calls", "node-settings-custom-tool-max-timeout"]],
		[
			"runtime",
			[
				"node-settings-llama-readiness-cap",
				"node-settings-llama-chat-http-timeout",
				"node-settings-llama-embedding-http-timeout",
				"node-settings-llama-cpu-thread-reserve",
				"node-settings-llama-gpu-reserve",
				"node-settings-llama-ram-reserve",
				"node-settings-image-idle-ttl",
				"node-settings-image-max-processes",
				"node-settings-image-text-encoder-gpu",
				"node-settings-model-fit-safety-margin",
				"node-settings-chat-cache-ram-mode",
			],
		],
		["runtimes", ["node-settings-container-runtime"]],
		["models", ["node-settings-hf-download-connections", "node-settings-hf-disk-margin"]],
		[
			"knowledge",
			[
				"node-settings-knowledge-default-results",
				"node-settings-knowledge-max-results",
				"node-settings-web-fetch-timeout",
				"node-settings-web-fetch-max-chars",
			],
		],
		["voice", ["node-settings-transcription-idle-timeout", "node-settings-transcription-inference-timeout"]],
		[
			"workspaces",
			[
				"node-settings-graph-workflow-max-runs",
				"node-settings-graph-workflow-node-timeout",
				"node-settings-work-session-max-steps",
				"node-settings-work-session-max-concurrent",
				"node-settings-development-max-duration",
				"node-settings-development-max-tool-calls",
				"node-settings-development-max-output-tokens",
			],
		],
	] as const)("renders the %s section's tunables", (section, testIds) => {
		renderCard({ section });

		for (const testId of testIds) {
			expect(screen.getByTestId(testId)).toBeTruthy();
		}
	});

	it("shows the draft-model GPU layers only for an external-draft mode", () => {
		renderCard({ section: "runtime" });
		expect(screen.queryByTestId("node-settings-speculative-draft-gpu-layers")).toBeNull();
		cleanup();

		renderCard({ section: "runtime", form: { ...toNodeSettingsFieldsForm(undefined), speculativeMode: "draft-simple" } });
		expect(screen.getByTestId("node-settings-speculative-draft-gpu-layers")).toBeTruthy();
		expect(screen.getByTestId("node-settings-restart-badge-speculativeDraftGpuLayers")).toBeTruthy();
	});

	it("offers the prompt-cache size only in Custom mode and reports a mode pick to the draft", () => {
		const { onChange } = renderCard({ section: "runtime" });
		expect(screen.queryByTestId("node-settings-chat-cache-ram-size")).toBeNull();

		fireEvent.click(screen.getByRole("radio", { name: "Custom" }));
		expect(onChange).toHaveBeenCalledWith("llamaChatCacheRamMode", "custom");
		cleanup();

		renderCard({
			section: "runtime",
			form: { ...toNodeSettingsFieldsForm(undefined), llamaChatCacheRamMode: "custom", llamaChatCacheRamMiB: 2048 },
		});
		expect((screen.getByTestId("node-settings-chat-cache-ram-size") as HTMLInputElement).value).toBe("2048 MiB");
	});

	it("shows the disk margin in GB and the chat request timeout in minutes", () => {
		renderCard({ section: "models" });
		expect((screen.getByTestId("node-settings-hf-disk-margin") as HTMLInputElement).value).toBe("1 GB");
		expect(
			screen.getByText("Allowed range: 1 B–1024 GB. A download is refused if it would leave less free space than this."),
		).toBeTruthy();
		cleanup();

		renderCard({ section: "runtime" });
		expect((screen.getByTestId("node-settings-llama-chat-http-timeout") as HTMLInputElement).value).toBe("60 minutes");
		expect(
			screen.getByText("Allowed range: 1–1440 minutes. The network timeout for one chat request to llama-server."),
		).toBeTruthy();
		// The real i18next instance (initialised in setup) holds the shipped bundles; react-i18next is mocked above.
		const cpuReserveKey = "pages.nodeSettings.fields.llamaCpuThreadReserve.description";
		const cpuReserveEn = i18next.getFixedT("en")(cpuReserveKey);
		expect(cpuReserveEn).toBe(
			"Only applies when a model runs on the CPU runtime. On a GPU runtime, llama.cpp picks its own thread count.",
		);
		// Only en is loaded into the test instance; de is read from its shipped bundle file.
		const de = nonEnglishLocales.find((locale) => locale.code === "de")?.resource;
		expect(cpuReserveKey.split(".").reduce<unknown>((node, part) => (node as Record<string, unknown>)?.[part], de)).toBe(
			"Gilt nur, wenn ein Modell auf der CPU-Laufzeit läuft. Auf einer GPU-Laufzeit wählt llama.cpp die Thread-Anzahl selbst.",
		);
		expect(screen.getByText(`Allowed range: 0–64. ${cpuReserveEn}`)).toBeTruthy();
	});
});

describe("NodeSettingsFieldsCard — chat knobs", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	it("places the agent-limit and context cards in the chat section only", () => {
		renderCard({ section: "chat" });
		const agentCard = screen.getByTestId("node-settings-agent-run-limits-card");
		const contextCard = screen.getByTestId("node-settings-context-compaction-card");
		expect(within(agentCard).getByText("Agent limits")).toBeTruthy();
		expect(within(contextCard).getByText("Context & compaction")).toBeTruthy();
		expect(within(agentCard).getByTestId("node-settings-spawn-max-concurrent")).toBeTruthy();
		expect(within(contextCard).getByTestId("node-settings-default-context-tokens")).toBeTruthy();
		cleanup();

		renderCard({ section: "runtime" });
		expect(screen.queryByTestId("node-settings-agent-run-limits-card")).toBeNull();
		expect(screen.queryByTestId("node-settings-context-compaction-card")).toBeNull();
	});

	it("renders the switches on by default and reports a click through the generic onChange", () => {
		const { onChange } = renderCard({ section: "chat" });

		for (const testId of [
			"node-settings-compaction-auto-enabled",
			"node-settings-compaction-distill-enabled",
			"node-settings-provider-retry-enabled",
		]) {
			expect((screen.getByTestId(testId) as HTMLInputElement).checked).toBe(true);
		}
		fireEvent.click(screen.getByTestId("node-settings-provider-retry-enabled"));

		expect(onChange).toHaveBeenCalledWith("providerRetryEnabled", false);
	});

	it("shows the stored number and edits it through the generic onChange", () => {
		const { onChange } = renderCard({ section: "chat", form: { ...toNodeSettingsFieldsForm(undefined), knowledgeChatTopK: 7 } });
		const input = screen.getByTestId("node-settings-knowledge-chat-top-k") as HTMLInputElement;
		expect(input.value).toBe("7");

		fireEvent.change(input, { target: { value: "9" } });

		expect(onChange).toHaveBeenCalledWith("knowledgeChatTopK", 9);
	});

	it("shows the thinking budgets per effort and reports edits through the generic onChange", () => {
		const { onChange } = renderCard({ section: "chat" });
		const card = screen.getByTestId("node-settings-reasoning-budgets-card");
		expect(within(card).getByText("Thinking budgets")).toBeTruthy();
		// Unset: blank, with the shipped default shown in its place and nothing to reset.
		const minimal = within(card).getByTestId("node-settings-reasoning-budget-minimal") as HTMLInputElement;
		expect(minimal.value).toBe("");
		expect(minimal.placeholder).toBe("Default: 1024 tokens");
		expect((within(card).getByTestId("node-settings-reasoning-budget-high") as HTMLInputElement).placeholder).toBe(
			"Default: 24576 tokens",
		);
		expect(within(card).queryByTestId("node-settings-reasoning-budget-minimal-use-default")).toBeNull();
		expect((within(card).getByTestId("node-settings-default-reasoning-effort") as HTMLInputElement).value).toBe("Default (low)");
		// Read per turn: no restart badge on any of them.
		expect(screen.queryByTestId("node-settings-restart-badge-reasoningBudgetLowTokens")).toBeNull();
		expect(screen.queryByTestId("node-settings-restart-badge-defaultReasoningEffort")).toBeNull();

		fireEvent.change(within(card).getByTestId("node-settings-reasoning-budget-medium"), { target: { value: "4096" } });

		expect(onChange).toHaveBeenCalledWith("reasoningBudgetMediumTokens", 4096);
	});

	it("shows the answer-length limit next to the thinking budgets and reports edits through the generic onChange", () => {
		const { onChange } = renderCard({ section: "chat" });
		const card = screen.getByTestId("node-settings-output-cap-card");
		expect(within(card).getByText("Answer length")).toBeTruthy();
		expect((within(card).getByTestId("node-settings-chat-output-cap-max-tokens") as HTMLInputElement).placeholder).toBe(
			"Default: 16384 tokens",
		);
		// This wrapper renders inline fallbacks, so the option shows the literal; the bundle strings are asserted below.
		expect((within(card).getByTestId("node-settings-chat-output-cap-mode") as HTMLInputElement).value).toBe("Default (cap)");
		// Read per turn: no restart badge.
		expect(screen.queryByTestId("node-settings-restart-badge-chatOutputCapMode")).toBeNull();
		expect(screen.queryByTestId("node-settings-restart-badge-chatOutputCapMaxTokens")).toBeNull();

		fireEvent.change(within(card).getByTestId("node-settings-chat-output-cap-max-tokens"), { target: { value: "4096" } });

		expect(onChange).toHaveBeenCalledWith("chatOutputCapMaxTokens", 4096);
	});

	it("offers Use default on a stored thinking budget and output-cap ceiling, which blanks the field", () => {
		const { onChange } = renderCard({
			section: "chat",
			form: { ...toNodeSettingsFieldsForm(undefined), reasoningBudgetLowTokens: 512, chatOutputCapMaxTokens: 2048 },
		});

		fireEvent.click(screen.getByTestId("node-settings-reasoning-budget-low-use-default"));
		fireEvent.click(screen.getByTestId("node-settings-chat-output-cap-max-tokens-use-default"));

		expect(onChange).toHaveBeenCalledWith("reasoningBudgetLowTokens", "");
		expect(onChange).toHaveBeenCalledWith("chatOutputCapMaxTokens", "");
		// Only the stored fields carry the action.
		expect(screen.queryByTestId("node-settings-reasoning-budget-medium-use-default")).toBeNull();
	});

	it("keeps the same input mounted when an unset thinking budget gets its first digit", () => {
		const field = (form: NodeSettingsFieldsForm) => (
			<MantineProvider env="test" theme={testMantineTheme}>
				<NodeSettingsNumberField
					field="reasoningBudgetLowTokens"
					label="Low effort"
					bounds={{ min: 128, max: 131072 }}
					unit="tokens"
					form={form}
					errors={{}}
					onChange={vi.fn()}
					testId="budget"
					shippedDefault={2048}
				/>
			</MantineProvider>
		);
		const blank = toNodeSettingsFieldsForm(undefined);
		const { rerender } = render(field(blank));
		const input = screen.getByTestId("budget");

		rerender(field({ ...blank, reasoningBudgetLowTokens: 1 }));

		// A remount here drops focus after one keystroke, so "1500" would arrive as "1".
		expect(screen.getByTestId("budget")).toBe(input);
		expect(screen.getByTestId("budget-use-default")).toBeTruthy();
	});

	it("ships the use-default and slow-hardware timeout strings in en and de", () => {
		const de = nonEnglishLocales.find((locale) => locale.code === "de")?.resource;
		const lookup = (bundle: unknown, key: string) =>
			key.split(".").reduce<unknown>((node, part) => (node as Record<string, unknown>)?.[part], bundle);
		for (const [key, en, german] of [
			["pages.nodeSettings.fields.useDefault", "Use default", "Standard verwenden"],
			["pages.nodeSettings.fields.defaultValue", "Default: {{value}}", "Standard: {{value}}"],
			["pages.nodeSettings.fields.defaultOption", "Default ({{value}})", "Standard ({{value}})"],
			[
				"pages.nodeSettings.fields.chatOutputCapMaxTokens.timeoutHint",
				"On slow hardware a long answer can reach the message request timeout (Local chat runtime) before this limit. If answers end in a timeout, raise that timeout.",
				"Auf langsamer Hardware kann eine lange Antwort das Zeitlimit für Nachrichtenanfragen (Lokale Chat-Runtime) vor dieser Grenze erreichen. Enden Antworten mit einer Zeitüberschreitung, erhöhen Sie dieses Zeitlimit.",
			],
			[
				"pages.nodeSettings.localChatRuntime.outputCapHint",
				"On slow hardware a long answer can reach this timeout before the answer length limit (Longest answer) stops it. Raise this timeout, or lower Longest answer.",
				"Auf langsamer Hardware kann eine lange Antwort dieses Zeitlimit erreichen, bevor die Begrenzung der Antwortlänge (Längste Antwort) sie stoppt. Erhöhen Sie dieses Zeitlimit oder senken Sie „Längste Antwort“.",
			],
		] as const) {
			expect(i18next.getFixedT("en")(key)).toBe(en);
			expect(lookup(de, key)).toBe(german);
		}
	});

	it("ships the answer-length strings in en and de", () => {
		const de = nonEnglishLocales.find((locale) => locale.code === "de")?.resource;
		const lookup = (bundle: unknown, key: string) =>
			key.split(".").reduce<unknown>((node, part) => (node as Record<string, unknown>)?.[part], bundle);
		for (const [key, en, german] of [
			["pages.nodeSettings.fields.chatOutputCapMaxTokens.label", "Longest answer", "Längste Antwort"],
			// The ceiling bounds the answer; the thinking budget adds to it, so the help text must not read it as the total.
			[
				"pages.nodeSettings.fields.chatOutputCapMaxTokens.description",
				"The answer may use half the model's context window, but never more than this. A thinking model's thinking budget comes on top, so this is not the total.",
				"Die Antwort darf das halbe Kontextfenster des Modells nutzen, aber nie mehr als diesen Wert. Das Denkbudget eines Denkmodells kommt hinzu, dies ist also nicht die Gesamtgrenze.",
			],
			[
				"chat.notices.outputLimitReachedText",
				"The answer stopped at the length limit before the model finished.",
				"Die Antwort wurde an der Längengrenze beendet, bevor das Modell fertig war.",
			],
		] as const) {
			expect(i18next.getFixedT("en")(key)).toBe(en);
			expect(lookup(de, key)).toBe(german);
		}
	});

	it("ships the thinking-budget strings in en and de", () => {
		const de = nonEnglishLocales.find((locale) => locale.code === "de")?.resource;
		const lookup = (bundle: unknown, key: string) =>
			key.split(".").reduce<unknown>((node, part) => (node as Record<string, unknown>)?.[part], bundle);
		const key = "pages.nodeSettings.fields.defaultReasoningEffort.label";
		expect(i18next.getFixedT("en")(key)).toBe("Budget when no effort is chosen");
		expect(lookup(de, key)).toBe("Budget ohne gewählte Denkintensität");
	});

	it("badges only the restart-gated tool-pipeline limits", () => {
		renderCard({ section: "chat" });

		expect(screen.getByTestId("node-settings-restart-badge-toolPipelineMaxIterationsPerRequest")).toBeTruthy();
		expect(screen.getByTestId("node-settings-restart-badge-toolPipelineMaxToolResultChars")).toBeTruthy();
		expect(screen.getByTestId("node-settings-restart-badge-toolPipelineMaxConsecutiveInvalidToolCalls")).toBeTruthy();
		expect(screen.queryByTestId("node-settings-restart-badge-spawnMaxConcurrent")).toBeNull();
		expect(screen.queryByTestId("node-settings-restart-badge-defaultContextTokens")).toBeNull();
	});
});

describe("NodeSettingsFieldsCard — knowledge, privacy and usage knobs", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	it("places each card in its own section", () => {
		renderCard({ section: "knowledge" });
		expect(
			within(screen.getByTestId("node-settings-knowledge-retrieval-card")).getByText("Retrieval and knowledge tools"),
		).toBeTruthy();
		expect(within(screen.getByTestId("node-settings-background-models-card")).getByText("Learning models")).toBeTruthy();
		expect(screen.queryByTestId("node-settings-cloud-access-card")).toBeNull();
		cleanup();

		renderCard({ section: "privacy" });
		const cloudCard = screen.getByTestId("node-settings-cloud-access-card");
		expect(within(cloudCard).getByTestId("node-settings-allow-cloud-model-access")).toBeTruthy();
		cleanup();

		renderCard({ section: "usage" });
		const retentionCard = screen.getByTestId("node-settings-retention-card");
		expect(within(retentionCard).getByTestId("node-settings-chat-retention-days")).toBeTruthy();
		expect(within(retentionCard).getByTestId("node-settings-benchmark-kld-cache")).toBeTruthy();
	});

	it("renders the cloud opt-in and chat retention off by default and reports a click through onChange", () => {
		const { onChange } = renderCard({ section: "privacy" });
		const cloud = screen.getByTestId("node-settings-allow-cloud-model-access") as HTMLInputElement;
		expect(cloud.checked).toBe(false);

		fireEvent.click(cloud);

		expect(onChange).toHaveBeenCalledWith("allowCloudModelAccess", true);
		cleanup();

		const cloudPermissions = {
			"node-settings-allow-cloud-model-unattended-runs": "allowCloudModelUnattendedRuns",
			"node-settings-allow-cloud-model-web-tools": "allowCloudModelWebTools",
			"node-settings-allow-cloud-model-mcp-tools": "allowCloudModelMcpTools",
			"node-settings-allow-cloud-model-sub-agents": "allowCloudModelSubAgents",
		} as const;
		const privacy = renderCard({ section: "privacy" });
		const cloudCard = screen.getByTestId("node-settings-cloud-access-card");
		expect(within(cloudCard).getByText("Cloud models")).toBeTruthy();
		for (const [testId, field] of Object.entries(cloudPermissions)) {
			const toggle = within(cloudCard).getByTestId(testId) as HTMLInputElement;
			expect(toggle.checked).toBe(false);
			fireEvent.click(toggle);
			expect(privacy.onChange).toHaveBeenCalledWith(field, true);
		}
		cleanup();

		renderCard({ section: "usage" });
		expect((screen.getByTestId("node-settings-chat-retention-enabled") as HTMLInputElement).checked).toBe(false);
		expect((screen.getByTestId("node-settings-agent-log-retention-enabled") as HTMLInputElement).checked).toBe(true);
	});

	it("shows the benchmark cache in GB and edits it through onChange", () => {
		const { onChange } = renderCard({ section: "usage" });
		const input = screen.getByTestId("node-settings-benchmark-kld-cache") as HTMLInputElement;
		expect(input.value).toBe("64 GB");

		fireEvent.change(input, { target: { value: "32" } });

		expect(onChange).toHaveBeenCalledWith("benchmarkKldCacheMaxBytes", 32);
	});

	it("badges only the restart-gated scheduled reindex pair", () => {
		renderCard({ section: "knowledge" });

		expect(screen.getByTestId("node-settings-restart-badge-knowledgeScheduledReindexEnabled")).toBeTruthy();
		expect(screen.getByTestId("node-settings-restart-badge-knowledgeScheduledReindexIntervalMinutes")).toBeTruthy();
		expect(screen.queryByTestId("node-settings-restart-badge-knowledgeRetrievalLatencyBudgetMs")).toBeNull();
	});

	it("defaults each learning model to the inherit option and offers the local models it was given", () => {
		const { onChange } = renderCard({
			section: "knowledge",
			backgroundModelOptions: [{ value: "qwen3:8b", label: "qwen3:8b" }],
		});

		const select = screen.getByTestId("node-settings-playbook-eval-model") as HTMLInputElement;
		expect(select.value).toBe("Use the default model");

		fireEvent.click(select);
		const listbox = screen.getByRole("listbox", { name: "Playbook eval model", hidden: true });
		fireEvent.click(within(listbox).getByRole("option", { name: "qwen3:8b", hidden: true }));

		expect(onChange).toHaveBeenCalledWith("playbookEvalModelName", "qwen3:8b");
	});

	it("keeps a stored learning model selectable after it was uninstalled", () => {
		renderCard({
			section: "knowledge",
			form: { ...toNodeSettingsFieldsForm(undefined), memoryExtractionModelName: "deleted-model" },
		});

		expect((screen.getByTestId("node-settings-memory-extraction-model") as HTMLInputElement).value).toBe("deleted-model");
	});
});

describe("NodeSettingsFieldsCard — feature switches", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	const switchTestIds = [
		"development",
		"work-sessions",
		"dev-workflows",
		"graph-workflows",
		"agent-home",
		"compute",
		"external-apps",
		"transcription",
		"scheduler",
	].map((name) => `node-settings-feature-${name}`);

	it("places the Features card with all nine switches in the general section only", () => {
		renderCard({ section: "general" });
		const card = screen.getByTestId("node-settings-features-card");
		expect(within(card).getByText("Features")).toBeTruthy();
		for (const testId of switchTestIds) {
			expect(within(card).getByTestId(testId)).toBeTruthy();
		}
		// Execution previews moved to the Sandbox & isolation section, which the page composes.
		expect(within(card).queryByTestId("node-settings-feature-execution-previews")).toBeNull();
		cleanup();

		renderCard({ section: "chat" });
		expect(screen.queryByTestId("node-settings-features-card")).toBeNull();
	});

	it("renders the effective values and reports a toggle through the generic onChange", () => {
		const { onChange } = renderCard({
			section: "general",
			form: { ...toNodeSettingsFieldsForm(undefined), developmentEnabled: false, agentHomeEnabled: true },
		});
		expect((screen.getByTestId("node-settings-feature-development") as HTMLInputElement).checked).toBe(false);
		expect((screen.getByTestId("node-settings-feature-agent-home") as HTMLInputElement).checked).toBe(true);

		fireEvent.click(screen.getByTestId("node-settings-feature-scheduler"));

		expect(onChange).toHaveBeenCalledWith("schedulerEnabled", false);
	});

	it("badges only Development and Scheduler, and explains the delayed parts of the others", () => {
		renderCard({ section: "general" });

		expect(screen.getByTestId("node-settings-restart-badge-developmentEnabled")).toBeTruthy();
		expect(screen.getByTestId("node-settings-restart-badge-schedulerEnabled")).toBeTruthy();
		expect(screen.queryByTestId("node-settings-restart-badge-externalAppsEnabled")).toBeNull();
		expect(screen.queryByTestId("node-settings-restart-badge-workSessionsEnabled")).toBeNull();
		expect(screen.getByText(/the container bridge listener applies after a restart/)).toBeTruthy();
		expect(screen.getByText(/The Mathematician agent is seeded on the next restart/)).toBeTruthy();
		expect(screen.getByText(/workflow definitions are seeded on the next restart/)).toBeTruthy();
	});

	it("shows a coupling error on the switch it is blamed on, with both codes in the en and de bundles", () => {
		renderCard({ section: "general", errors: { devWorkflowsEnabled: "requiresWorkSessions" } });

		const switchRoot = (testId: string): HTMLElement => screen.getByTestId(testId).closest(".mantine-Switch-root") as HTMLElement;
		expect(within(switchRoot("node-settings-feature-dev-workflows")).getByText("Invalid value.")).toBeTruthy();
		expect(within(switchRoot("node-settings-feature-work-sessions")).queryByText("Invalid value.")).toBeNull();
		const de = nonEnglishLocales.find((locale) => locale.code === "de")?.resource;
		for (const code of ["requiresWorkSessions", "requiresToolCapableModels"]) {
			const key = `pages.nodeSettings.fields.errors.${code}`;
			expect(i18next.getFixedT("en")(key)).not.toBe(key);
			expect(typeof key.split(".").reduce<unknown>((node, part) => (node as Record<string, unknown>)?.[part], de)).toBe("string");
		}
	});
});
