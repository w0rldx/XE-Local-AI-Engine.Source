// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import {
	NodeSettingsFieldsCard,
	type NodeSettingsFieldsCardProps,
} from "@/features/node-settings/components/NodeSettingsFieldsCard";
import {
	type NodeSettingsFieldsForm,
	toNodeSettingsFieldBounds,
	toNodeSettingsFieldsForm,
	type UsageRateRow,
} from "@/features/node-settings/models/NodeSettingsFieldsModel";

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
	autoEffortFastModelOptions?: NodeSettingsFieldsCardProps["autoEffortFastModelOptions"];
}

function renderCard(
	overrides: RenderOverrides = {},
): { onDownload: () => void; onDownloadEmbedding: () => void; onChange: ReturnType<typeof vi.fn> } {
	const onDownload = overrides.onDownloadRecommendedReranker ?? vi.fn();
	const onDownloadEmbedding = overrides.onDownloadRecommendedEmbedding ?? vi.fn();
	const onChange = overrides.onChange ?? vi.fn();
	render(
		<MantineProvider>
			<NodeSettingsFieldsCard
				form={overrides.form ?? toNodeSettingsFieldsForm(undefined)}
				bounds={toNodeSettingsFieldBounds(undefined)}
				errors={overrides.errors ?? {}}
				onChange={onChange as unknown as NodeSettingsFieldsCardProps["onChange"]}
				showDeveloperFields={false}
				draftModelOptions={[]}
				keepWarmModelOptions={overrides.keepWarmModelOptions ?? []}
				autoEffortFastModelOptions={overrides.autoEffortFastModelOptions ?? []}
				rerankerModelOptions={[]}
				onDownloadRecommendedReranker={onDownload}
				isDownloadRecommendedRerankerPending={overrides.isDownloadRecommendedRerankerPending ?? false}
				isRecommendedRerankerInFlight={overrides.isRecommendedRerankerInFlight ?? false}
				onDownloadRecommendedEmbedding={onDownloadEmbedding}
				isDownloadRecommendedEmbeddingPending={overrides.isDownloadRecommendedEmbeddingPending ?? false}
				isRecommendedEmbeddingInFlight={overrides.isRecommendedEmbeddingInFlight ?? false}
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
		renderCard({ autoEffortFastModelOptions: [{ value: "qwen3-1.7b", label: "qwen3-1.7b" }] });

		const select = screen.getByTestId("node-settings-auto-effort-fast-model") as HTMLInputElement;
		expect(select.value).toBe("Off");

		fireEvent.click(select);
		// Scoped to this select's own listbox: the reranker select on the same card also offers an "Off" entry.
		const listbox = screen.getByRole("listbox", { name: "Fast model for automatic reasoning effort", hidden: true });
		expect(within(listbox).getByRole("option", { name: "Off", hidden: true })).toBeTruthy();
		expect(within(listbox).getByRole("option", { name: "qwen3-1.7b", hidden: true })).toBeTruthy();
	});

	it("edits the selection through the generic onChange", () => {
		const { onChange } = renderCard({ autoEffortFastModelOptions: [{ value: "qwen3-1.7b", label: "qwen3-1.7b" }] });

		fireEvent.click(screen.getByTestId("node-settings-auto-effort-fast-model"));
		fireEvent.click(screen.getByText("qwen3-1.7b"));

		expect(onChange).toHaveBeenCalledWith("autoEffortFastModelName", "qwen3-1.7b");
	});

	it("keeps a stored model selectable after it was uninstalled", () => {
		// Without the synthetic entry the select would silently read "Off" for a node that is still configured.
		renderCard({
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

	it("renders off by default, with no restart hint (the backend reads it live)", () => {
		renderCard();

		const toggle = screen.getByTestId("node-settings-tool-relevance-enabled") as HTMLInputElement;
		expect(toggle.checked).toBe(false);
		expect(screen.queryByTestId("node-settings-restart-hint-toolRelevanceEnabled")).toBeNull();
	});

	it("renders on when the form says so", () => {
		renderCard({ form: { ...toNodeSettingsFieldsForm(undefined), toolRelevanceEnabled: true } });

		expect((screen.getByTestId("node-settings-tool-relevance-enabled") as HTMLInputElement).checked).toBe(true);
	});

	it("reports a click through the generic onChange", () => {
		const { onChange } = renderCard();

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
		renderCard();

		expect(screen.getByTestId("node-settings-keep-model-warm-enabled")).toBeTruthy();
		expect((screen.getByTestId("node-settings-keep-model-warm-model") as HTMLInputElement).disabled).toBe(true);
		expect((screen.getByTestId("node-settings-keep-model-warm-interval") as HTMLInputElement).disabled).toBe(true);
	});

	it("edits the toggle, llama.cpp model, and interval through the generic onChange", () => {
		const form = { ...toNodeSettingsFieldsForm(undefined), keepModelWarmEnabled: true };
		const { onChange } = renderCard({
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
		renderCard({
			form: { ...toNodeSettingsFieldsForm(undefined), llamaMaxLoadedProcesses: 7 },
		});

		const help = screen.getByTestId("node-settings-keep-model-warm-help").textContent ?? "";
		expect(help).toContain("VRAM");
		expect(help).toContain("one of the configured 7 MaxLoadedProcesses slots");
		expect(help).toContain("below the idle TTL");
	});

	it("marks a stale selected model as unavailable", () => {
		renderCard({
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
		renderCard();

		expect(screen.getByTestId("node-settings-reranker-download-recommended")).toBeTruthy();
		// The helper names the recommended model + its extra model server (human copy resolves — no bare i18n key).
		expect(screen.getByText(/bge-reranker-v2-m3/)).toBeTruthy();
	});

	it("invokes the download handler once when the button is clicked", () => {
		const { onDownload } = renderCard();

		fireEvent.click(screen.getByTestId("node-settings-reranker-download-recommended"));

		expect(onDownload).toHaveBeenCalledTimes(1);
	});

	it("disables the button while the recommended reranker download is in flight (duplicate-guard)", () => {
		renderCard({ isRecommendedRerankerInFlight: true });

		expect((screen.getByTestId("node-settings-reranker-download-recommended") as HTMLButtonElement).disabled).toBe(true);
	});

	it("disables the button while the download request is pending", () => {
		renderCard({ isDownloadRecommendedRerankerPending: true });

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
		renderCard();

		expect(screen.getByTestId("node-settings-embedding-download-recommended")).toBeTruthy();
		// The helper names the recommended model + explains why it's required (human copy resolves — no bare i18n key).
		expect(screen.getByText(/nomic-embed-text-v1\.5/)).toBeTruthy();
	});

	it("invokes the download handler once when the button is clicked", () => {
		const { onDownloadEmbedding } = renderCard();

		fireEvent.click(screen.getByTestId("node-settings-embedding-download-recommended"));

		expect(onDownloadEmbedding).toHaveBeenCalledTimes(1);
	});

	it("disables the button while the recommended embedding download is in flight (duplicate-guard)", () => {
		renderCard({ isRecommendedEmbeddingInFlight: true });

		expect((screen.getByTestId("node-settings-embedding-download-recommended") as HTMLButtonElement).disabled).toBe(true);
	});

	it("disables the button while the download request is pending", () => {
		renderCard({ isDownloadRecommendedEmbeddingPending: true });

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
		renderCard();

		expect(screen.getByTestId("node-settings-usage-rates-card")).toBeTruthy();
		expect(screen.getByTestId("node-settings-usage-rates-empty")).toBeTruthy();
		expect(screen.getByTestId("node-settings-usage-rate-add")).toBeTruthy();
		expect(screen.queryByTestId("node-settings-usage-rate-row")).toBeNull();
	});

	it("appends a blank row when Add rate is clicked", () => {
		const { onChange } = renderCard();

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
			form: formWithRates([{ id: "a", modelName: "gpt", inputPer1M: 1, outputPer1M: 2 }]),
		});

		fireEvent.change(screen.getByTestId("node-settings-usage-rate-model"), { target: { value: "gpt-5" } });

		expect(onChange).toHaveBeenCalledWith("usageRates", [{ id: "a", modelName: "gpt-5", inputPer1M: 1, outputPer1M: 2 }]);
	});

	it("edits a row's input rate through the number input", () => {
		const { onChange } = renderCard({
			form: formWithRates([{ id: "a", modelName: "gpt-5", inputPer1M: 1, outputPer1M: 2 }]),
		});

		fireEvent.change(screen.getByTestId("node-settings-usage-rate-input"), { target: { value: "5" } });

		expect(onChange).toHaveBeenCalledWith("usageRates", [{ id: "a", modelName: "gpt-5", inputPer1M: 5, outputPer1M: 2 }]);
	});

	it("removes a row, sending the reduced array", () => {
		const { onChange } = renderCard({
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
			form: formWithRates([{ id: "a", modelName: "gpt-5", inputPer1M: -1, outputPer1M: 2 }]),
			errors: { usageRates: "rate" },
		});

		// The error block renders whenever the page passes a usageRates error code (the card resolves every field error
		// through the shared errors.<code> i18n lookup, whose test-time fallback is the generic "Invalid value.").
		expect(screen.getByTestId("node-settings-usage-rates-error").textContent).toBe("Invalid value.");
	});
});

describe("NodeSettingsFieldsCard — restart-required hint", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		vi.clearAllMocks();
	});

	afterEach(() => cleanup());

	it("marks a restart-gated field so the operator knows a Save is not live", () => {
		renderCard();

		// chatCacheReuse is seeded once into LlamaServerSupervisorOptions at composition — a save needs a node restart.
		const hint = screen.getByTestId("node-settings-restart-hint-chatCacheReuse");
		expect(hint.textContent?.trim()).toBe("Takes effect after the node restarts.");
		expect(screen.getByTestId("node-settings-restart-hint-defaultModelName")).toBeTruthy();
		expect(screen.getByTestId("node-settings-restart-hint-rerankerModelName")).toBeTruthy();
	});

	it("does not mark a field that is read live on every call", () => {
		renderCard();

		// toolCapableModels is re-read per invocation (OrchestrationResolver) — labelling it would be a lie.
		expect(screen.queryByTestId("node-settings-restart-hint-toolCapableModels")).toBeNull();
		expect(screen.queryByTestId("node-settings-restart-hint-enableTools")).toBeNull();
		expect(screen.queryByTestId("node-settings-restart-hint-keepModelWarmIntervalSeconds")).toBeNull();
	});

	it("hints the draft-model fields only once the mode that uses them is selected", () => {
		renderCard({ form: { ...toNodeSettingsFieldsForm(undefined), speculativeMode: "draft-simple" } });

		expect(screen.getByTestId("node-settings-restart-hint-speculativeDraftModelName")).toBeTruthy();
		expect(screen.getByTestId("node-settings-restart-hint-speculativeDraftMaxTokens")).toBeTruthy();
	});

	it("renders the KV cache type picker with a restart hint", () => {
		renderCard();

		expect(screen.getByTestId("node-settings-kv-cache-type")).toBeTruthy();
		// LlamaServerLaunchPolicyOptions is seeded once at host build, so the operator must be told it needs a restart.
		expect(screen.getByTestId("node-settings-restart-hint-kvCacheType")).toBeTruthy();
	});

	it.each(["draft-dflash", "draft-dspark"])("treats %s as an external-draft mode and shows its draft-model fields", (mode) => {
		renderCard({ form: { ...toNodeSettingsFieldsForm(undefined), speculativeMode: mode } });

		// Both load a second GGUF, so the draft-model picker and the draft-tokens input must appear for them.
		expect(screen.getByTestId("node-settings-speculative-draft-model")).toBeTruthy();
		expect(screen.getByTestId("node-settings-speculative-draft-max-tokens")).toBeTruthy();
	});
});
