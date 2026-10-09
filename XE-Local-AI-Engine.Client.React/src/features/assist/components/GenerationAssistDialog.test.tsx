// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (_key: string, defaultValue?: string, options?: Record<string, unknown>) => {
			let text = defaultValue ?? _key;
			if (options) {
				for (const [name, value] of Object.entries(options)) {
					text = text.replace(`{{${name}}}`, String(value));
				}
			}
			return text;
		},
	}),
}));

// Stub the draft SDK fn the dialog drives, plus the two SAVE fns it must never reach — drafting persists nothing, so
// the create/update spies staying untouched is the assertion, not an incidental detail. Everything else stays real,
// including the callWithResponseValidation bridge the hook wraps the call in.
const { draftSkillSpy, createSkillSpy, updateSkillSpy } = vi.hoisted(() => ({
	draftSkillSpy: vi.fn(),
	createSkillSpy: vi.fn(),
	updateSkillSpy: vi.fn(),
}));
vi.mock("@/core/api/generated", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated")>()),
	draftSkill: draftSkillSpy,
	createSkill: createSkillSpy,
	updateSkill: updateSkillSpy,
}));

// The real viewer lazy-loads Monaco, which jsdom cannot lay out; this stand-in shows which texts it was handed.
vi.mock("@/core/ui/components/CodeEditor/CodeDiffViewer", () => ({
	CodeDiffViewer: (props: { original: string; modified: string; "data-testid"?: string }) => (
		<div data-testid={props["data-testid"]}>
			<pre data-testid="diff-original">{props.original}</pre>
			<pre data-testid="diff-modified">{props.modified}</pre>
		</div>
	),
}));

import type {
	XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse,
	XeLocalAiEngineClientEndpointsSkillsV1DraftSkillRequest,
} from "@/core/api/generated";
import { ApiError } from "@/core/api/errors/ApiError";
import { GenerationAssistDialog } from "@/features/assist/components/GenerationAssistDialog";
import type { AssistDraft, AssistMode, AssistSurface, GenerationMetadata } from "@/features/assist/models/AssistModels";
import { testMantineTheme } from "@/test/MantineTestRender";

const models: XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse[] = [
	{
		modelName: "qwen3-4b",
		kind: "Chat",
		detectedKind: "Chat",
		provider: "llamacpp",
		capabilities: [],
		isSelected: true,
		isReasoningCapable: false,
		isToolCapable: false,
		isOverridden: false,
	},
];

const generationMetadata: GenerationMetadata = {
	model: "qwen3-4b",
	mode: "Create",
	userBrief: "Review supplier invoices.",
	rationale: "Kept the steps short so the agent can follow them without loading anything else.",
	assumptions: ["Invoices arrive as PDFs."],
	confidence: 0.7,
	generatedAtUtc: 1_760_000_000_000,
	draftContentHash: "abc123",
};

const draftResponse = {
	data: {
		name: "invoice-review",
		description: "How to review supplier invoices.",
		body: "# Review an invoice\n\n1. Check the rate card.",
		generationMetadata,
	},
};

const existingSkill = { name: "invoice-review", description: "Old description.", content: "# Old body" };

function renderDialog(surface: AssistSurface = "skill", installedModels = models, mode: AssistMode = "Create") {
	const onApply = vi.fn<(draft: AssistDraft) => void>();
	const onDiscard = vi.fn<() => void>();
	const onClose = vi.fn<() => void>();
	const queryClient = new QueryClient({ defaultOptions: { mutations: { retry: false }, queries: { retry: false } } });

	render(
		<QueryClientProvider client={queryClient}>
			<MantineProvider env="test" theme={testMantineTheme}>
				<GenerationAssistDialog
					opened={true}
					surface={surface}
					mode={mode}
					existing={mode === "Improve" ? existingSkill : { name: "", description: "", content: "" }}
					models={installedModels}
					loadedModelNames={["qwen3-4b"]}
					onApply={onApply}
					onDiscard={onDiscard}
					onClose={onClose}
				/>
			</MantineProvider>
		</QueryClientProvider>,
	);

	return { onApply, onDiscard, onClose };
}

/** Fills the brief and runs one generation. */
function generate(brief = "Review supplier invoices.") {
	fireEvent.change(screen.getByTestId("assist-brief"), { target: { value: brief } });
	fireEvent.click(screen.getByTestId("assist-generate"));
}

beforeEach(() => {
	// Mantine reads the color scheme from matchMedia, its ScrollArea (inside DialogShell) uses a ResizeObserver, and
	// the autosizing Textarea re-measures on `document.fonts` ready. jsdom implements none of the three.
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
	Object.defineProperty(document, "fonts", {
		writable: true,
		value: { ready: Promise.resolve(), addEventListener: vi.fn(), removeEventListener: vi.fn() },
	});
	// jsdom does not implement scrollIntoView; Mantine's Combobox calls it on a timer when the model dropdown opens.
	Element.prototype.scrollIntoView = vi.fn();
	Object.defineProperty(window, "ResizeObserver", {
		writable: true,
		value: class ResizeObserverMock {
			observe = vi.fn();

			unobserve = vi.fn();

			disconnect = vi.fn();
		},
	});
});

afterEach(() => {
	cleanup();
	vi.clearAllMocks();
});

describe("GenerationAssistDialog", () => {
	it("hands an applied draft to the parent and saves nothing itself", async () => {
		draftSkillSpy.mockResolvedValue(draftResponse);
		const { onApply, onClose } = renderDialog();

		generate();
		await waitFor(() => expect(screen.getByTestId("assist-result")).toBeTruthy());

		fireEvent.click(screen.getByTestId("assist-apply"));

		expect(onApply).toHaveBeenCalledWith({
			name: "invoice-review",
			description: "How to review supplier invoices.",
			content: "# Review an invoice\n\n1. Check the rate card.",
			generationMetadata,
		});
		expect(onClose).toHaveBeenCalled();
		// The whole point of the review step: no persistence happens on this path.
		expect(createSkillSpy).not.toHaveBeenCalled();
		expect(updateSkillSpy).not.toHaveBeenCalled();
	});

	it("sends the brief and mode the endpoint expects", async () => {
		draftSkillSpy.mockResolvedValue(draftResponse);
		renderDialog();

		generate("Review supplier invoices.");
		await waitFor(() => expect(draftSkillSpy).toHaveBeenCalled());

		const options = draftSkillSpy.mock.calls[0]?.[0] as {
			body: XeLocalAiEngineClientEndpointsSkillsV1DraftSkillRequest;
			signal?: AbortSignal;
		};
		expect(options.body.mode).toBe("Create");
		expect(options.body.brief).toBe("Review supplier invoices.");
		expect(options.body.modelName).toBe("qwen3-4b");
		// Create mode carries no baseline — only Improve sends the form's current content.
		expect(options.body.existingContent).toBeUndefined();
		// The Cancel button aborts through this signal, so it must reach the SDK call.
		expect(options.signal).toBeInstanceOf(AbortSignal);
	});

	it("applies the operator's edits while keeping the draft's provenance intact", async () => {
		draftSkillSpy.mockResolvedValue(draftResponse);
		const { onApply } = renderDialog();

		generate();
		await waitFor(() => expect(screen.getByTestId("assist-result")).toBeTruthy());

		fireEvent.change(screen.getByTestId("assist-result-name"), { target: { value: "invoice-check" } });
		fireEvent.click(screen.getByTestId("assist-apply"));

		expect(onApply).toHaveBeenCalledWith(expect.objectContaining({ name: "invoice-check", generationMetadata }));
	});

	it("renders a busy node as a notice rather than an error", async () => {
		draftSkillSpy.mockRejectedValue(
			new ApiError(409, { type: "about:blank", title: "Conflict", status: 409, detail: "The node is busy." }),
		);
		renderDialog();

		generate();

		await waitFor(() => expect(screen.getByTestId("assist-busy-notice")).toBeTruthy());
		expect(screen.queryByTestId("assist-error")).toBeNull();
		expect(screen.queryByTestId("assist-unparseable")).toBeNull();
	});

	it("offers a retry state when the model output could not be parsed", async () => {
		draftSkillSpy.mockRejectedValue(
			new ApiError(422, { type: "about:blank", title: "Unprocessable", status: 422, detail: "Unparseable." }),
		);
		renderDialog();

		generate();

		await waitFor(() => expect(screen.getByTestId("assist-unparseable")).toBeTruthy());
		expect(screen.queryByTestId("assist-error")).toBeNull();
	});

	it("surfaces any other failure with the server's own message", async () => {
		draftSkillSpy.mockRejectedValue(
			new ApiError(400, {
				type: "about:blank",
				title: "Bad Request",
				status: 400,
				detail: "Model 'qwen3-4b' is not eligible for drafting.",
			}),
		);
		renderDialog();

		generate();

		await waitFor(() => expect(screen.getByTestId("assist-error")).toBeTruthy());
		expect(screen.getByTestId("assist-error").textContent).toContain("not eligible for drafting");
		expect(screen.queryByTestId("assist-busy-notice")).toBeNull();
	});

	it("tells the parent to drop the provenance when the draft is discarded", async () => {
		draftSkillSpy.mockResolvedValue(draftResponse);
		const { onDiscard, onClose, onApply } = renderDialog();

		generate();
		await waitFor(() => expect(screen.getByTestId("assist-result")).toBeTruthy());

		fireEvent.click(screen.getByTestId("assist-discard"));

		expect(onDiscard).toHaveBeenCalled();
		expect(onClose).toHaveBeenCalled();
		expect(onApply).not.toHaveBeenCalled();
	});

	// jsdom has no stacking context, so the dropdown-behind-the-dialog z-index regression is only visible in a browser;
	// this pins the selection itself: the picked model reaches the request and re-clicking it does not deselect it.
	it("sends the model the operator picked, not the default", async () => {
		draftSkillSpy.mockResolvedValue(draftResponse);
		const secondModel: XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse = {
			modelName: "llama-3-8b",
			kind: "Chat",
			detectedKind: "Chat",
			provider: "llamacpp",
			capabilities: [],
			isSelected: false,
			isReasoningCapable: false,
			isToolCapable: false,
			isOverridden: false,
		};
		renderDialog("skill", [...models, secondModel]);

		const pickSecond = () => {
			fireEvent.click(screen.getByTestId("assist-model"));
			fireEvent.click(screen.getByRole("option", { name: "llama-3-8b" }));
		};
		pickSecond();
		pickSecond();
		expect((screen.getByTestId("assist-model") as HTMLInputElement).value).toBe("llama-3-8b");

		generate();
		await waitFor(() => expect(draftSkillSpy).toHaveBeenCalled());

		const options = draftSkillSpy.mock.calls[0]?.[0] as { body: XeLocalAiEngineClientEndpointsSkillsV1DraftSkillRequest };
		expect(options.body.modelName).toBe("llama-3-8b");
	});

	it("offers no Compare view outside Improve mode", async () => {
		draftSkillSpy.mockResolvedValue(draftResponse);
		renderDialog();

		generate();
		await waitFor(() => expect(screen.getByTestId("assist-result")).toBeTruthy());

		expect(screen.queryByTestId("assist-view")).toBeNull();
		expect(screen.queryByRole("radio", { name: "Compare" })).toBeNull();
	});

	it("compares the current content with an Improve draft and still applies the draft from there", async () => {
		draftSkillSpy.mockResolvedValue(draftResponse);
		const { onApply } = renderDialog("skill", models, "Improve");
		// Nothing to compare until a draft exists.
		expect(screen.queryByTestId("assist-view")).toBeNull();

		generate("Tighten the steps.");
		await waitFor(() => expect(screen.getByTestId("assist-view")).toBeTruthy());
		expect(screen.queryByTestId("assist-compare")).toBeNull();

		fireEvent.click(screen.getByRole("radio", { name: "Compare" }));

		expect(screen.getByTestId("diff-original").textContent).toBe("# Old body");
		expect(screen.getByTestId("diff-modified").textContent).toBe("# Review an invoice\n\n1. Check the rate card.");
		expect(screen.queryByTestId("assist-result-content")).toBeNull();

		fireEvent.click(screen.getByTestId("assist-apply"));

		expect(onApply).toHaveBeenCalledWith({
			name: "invoice-review",
			description: "How to review supplier invoices.",
			content: "# Review an invoice\n\n1. Check the rate card.",
			generationMetadata,
		});
	});

	it("returns to the Draft view when a new draft is generated", async () => {
		draftSkillSpy.mockResolvedValue(draftResponse);
		renderDialog("skill", models, "Improve");

		generate("Tighten the steps.");
		await waitFor(() => expect(screen.getByTestId("assist-view")).toBeTruthy());
		fireEvent.click(screen.getByRole("radio", { name: "Compare" }));
		expect(screen.getByTestId("assist-compare")).toBeTruthy();

		fireEvent.click(screen.getByTestId("assist-generate"));
		await waitFor(() => expect(draftSkillSpy).toHaveBeenCalledTimes(2));
		await waitFor(() => expect(screen.getByTestId("assist-result-content")).toBeTruthy());

		expect(screen.queryByTestId("assist-compare")).toBeNull();
	});

	// The image form has nothing to save: applying fills the prompt, so its intro must not tell the operator to save.
	it("gives the image surface its own intro", () => {
		renderDialog("image");

		expect(screen.getByTestId("assist-intro").textContent).toBe(
			"A model on this node writes a prompt from your description. Applying it only fills the prompt field; nothing is generated until you start the image generation yourself.",
		);
	});
});
