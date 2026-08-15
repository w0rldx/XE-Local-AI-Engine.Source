// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
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

const { skillHooksMock, toastMock, applyDraftSpy, discardDraftSpy } = vi.hoisted(() => ({
	skillHooksMock: {
		useSkills: vi.fn(),
		useSkill: vi.fn(),
		useCreateSkill: vi.fn(),
		useUpdateSkill: vi.fn(),
		useDeleteSkill: vi.fn(),
	},
	toastMock: { success: vi.fn(), info: vi.fn(), error: vi.fn(), warning: vi.fn() },
	applyDraftSpy: vi.fn(),
	discardDraftSpy: vi.fn(),
}));

vi.mock("@/features/skills/queries/useSkills", () => skillHooksMock);
// The page reads useConfirm at render; nothing in these tests takes a confirm path.
vi.mock("@/core/ui/hooks/useConfirm", () => ({ useConfirm: () => ({ confirm: vi.fn() }) }));
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));
// The bundled-resources panel fetches on mount; it is irrelevant to the save path under test.
vi.mock("@/features/skills/components/SkillResourcesPanel", () => ({ SkillResourcesPanel: () => null }));
// The unsaved-changes guard calls TanStack Router's useBlocker, which needs a Router context this test does not
// provide. Only useBlocker is overridden; its own behavior is covered by useUnsavedChangesGuard.test.tsx.
vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	useBlocker: () => ({ status: "idle", proceed: undefined, reset: undefined }),
}));
// Stand in for the assist affordance with two bare buttons that fire the same callbacks the real dialog does. The
// dialog itself is covered by GenerationAssistDialog.test.tsx; what matters here is what the FORM does with a draft.
vi.mock("@/features/assist/components/AssistActions", () => ({
	AssistActions: ({ onApply, onDiscard }: { onApply: (draft: unknown) => void; onDiscard: () => void }) => (
		<>
			<button
				type="button"
				data-testid="stub-apply-draft"
				onClick={() => {
					applyDraftSpy();
					onApply({
						name: "invoice-review",
						description: "How to review supplier invoices.",
						content: "# Review an invoice",
						generationMetadata: draftMetadata,
					});
				}}
			>
				apply
			</button>
			<button
				type="button"
				data-testid="stub-discard-draft"
				onClick={() => {
					discardDraftSpy();
					onDiscard();
				}}
			>
				discard
			</button>
		</>
	),
}));

import type { XeLocalAiEngineClientEndpointsSkillsV1UpdateSkillRequest } from "@/core/api/generated";
import type { GenerationMetadata } from "@/features/assist/models/AssistModels";
import type { Skill } from "@/features/skills/models/SkillModels";
import { SkillsPage } from "@/features/skills/pages/SkillsPage";
import { useSkillManagementStore } from "@/features/skills/stores/SkillManagementStore";

const draftMetadata: GenerationMetadata = {
	model: "qwen3-4b",
	mode: "Improve",
	userBrief: "Tighten the steps.",
	rationale: "Removed a redundant step.",
	assumptions: [],
	confidence: 0.6,
	generatedAtUtc: 1_760_000_000_000,
	draftContentHash: "hash-1",
};

const storedSkill: Skill = {
	id: "skill-1",
	name: "invoice-review",
	description: "Old description.",
	body: "# Old body",
	enabled: true,
	version: 3,
	createdAtUtc: 1,
	updatedAtUtc: 2,
	license: null,
	compatibility: null,
	allowedTools: null,
	metadata: null,
	origin: "Local",
	sourceUri: null,
	importedAtUtc: null,
	resourceCount: 0,
};

const updateMutate = vi.fn();

function renderPage() {
	const queryClient = new QueryClient({ defaultOptions: { mutations: { retry: false }, queries: { retry: false } } });
	return render(
		<QueryClientProvider client={queryClient}>
			<MantineProvider>
				<SkillsPage />
			</MantineProvider>
		</QueryClientProvider>,
	);
}

beforeEach(() => {
	skillHooksMock.useSkills.mockReturnValue({ data: [], isLoading: false, error: null });
	skillHooksMock.useSkill.mockReturnValue({ data: storedSkill, isLoading: false, error: null });
	skillHooksMock.useCreateSkill.mockReturnValue({ mutate: vi.fn(), isPending: false, error: null });
	skillHooksMock.useUpdateSkill.mockReturnValue({ mutate: updateMutate, isPending: false, error: null });
	skillHooksMock.useDeleteSkill.mockReturnValue({ mutate: vi.fn(), isPending: false, error: null });
	// A successful save so the onSuccess branch (which owns the demotion toast) actually runs.
	updateMutate.mockImplementation((_variables: unknown, options?: { onSuccess?: () => void }) => options?.onSuccess?.());

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
	act(() => useSkillManagementStore.getState().actions.closeEditor());
	vi.clearAllMocks();
});

/** Opens the editor on the stored skill and waits for the form to mount. */
async function openEditor() {
	renderPage();
	act(() => useSkillManagementStore.getState().actions.openEdit("skill-1"));
	await waitFor(() => expect(screen.getByTestId("skill-form")).toBeTruthy());
}

describe("SkillsPage AI-draft save path", () => {
	it("submits an applied draft with the demotion flag and its provenance, and explains the demotion", async () => {
		await openEditor();

		fireEvent.click(screen.getByTestId("stub-apply-draft"));
		fireEvent.click(screen.getByTestId("skill-form-submit"));

		await waitFor(() => expect(updateMutate).toHaveBeenCalled());
		const [variables] = updateMutate.mock.calls[0] as [
			{ path: { skillId: string }; body: XeLocalAiEngineClientEndpointsSkillsV1UpdateSkillRequest },
		];
		expect(variables.path.skillId).toBe("skill-1");
		expect(variables.body.generated).toBe(true);
		expect(variables.body.generationMetadata).toEqual(draftMetadata);
		// The draft overwrote the three fields it authored.
		expect(variables.body.name).toBe("invoice-review");
		expect(variables.body.body).toBe("# Review an invoice");

		// The Imported badge and disabled toggle arrive with the refetched row; the reason has to be said out loud.
		expect(toastMock.warning).toHaveBeenCalledWith(
			"Saved as an imported skill and left disabled. Review the generated instructions, then enable it.",
		);
	});

	it("keeps the provenance when the operator edits the drafted content afterwards", async () => {
		await openEditor();

		fireEvent.click(screen.getByTestId("stub-apply-draft"));
		fireEvent.change(screen.getByTestId("skill-form-description"), { target: { value: "Hand-tightened summary." } });
		fireEvent.click(screen.getByTestId("skill-form-submit"));

		await waitFor(() => expect(updateMutate).toHaveBeenCalled());
		const [variables] = updateMutate.mock.calls[0] as [{ body: XeLocalAiEngineClientEndpointsSkillsV1UpdateSkillRequest }];
		// Editing does NOT disown the generation — the server recomputes the hash and records wasEdited itself.
		expect(variables.body.generated).toBe(true);
		expect(variables.body.generationMetadata).toEqual(draftMetadata);
		expect(variables.body.description).toBe("Hand-tightened summary.");
	});

	it("drops the flag and the provenance when the draft is explicitly discarded", async () => {
		await openEditor();

		fireEvent.click(screen.getByTestId("stub-apply-draft"));
		fireEvent.click(screen.getByTestId("stub-discard-draft"));
		fireEvent.click(screen.getByTestId("skill-form-submit"));

		await waitFor(() => expect(updateMutate).toHaveBeenCalled());
		const [variables] = updateMutate.mock.calls[0] as [{ body: XeLocalAiEngineClientEndpointsSkillsV1UpdateSkillRequest }];
		expect(variables.body.generated).toBe(false);
		// Null, not omitted: the server reads a null block as "preserve the stored provenance", never as "clear it".
		expect(variables.body.generationMetadata).toBeNull();
		expect(toastMock.warning).not.toHaveBeenCalled();
	});

	it("sends no provenance and echoes the stored enabled flag on an ordinary edit", async () => {
		await openEditor();

		fireEvent.change(screen.getByTestId("skill-form-description"), { target: { value: "Just a wording fix." } });
		fireEvent.click(screen.getByTestId("skill-form-submit"));

		await waitFor(() => expect(updateMutate).toHaveBeenCalled());
		const [variables] = updateMutate.mock.calls[0] as [{ body: XeLocalAiEngineClientEndpointsSkillsV1UpdateSkillRequest }];
		expect(variables.body.generated).toBe(false);
		expect(variables.body.generationMetadata).toBeNull();
		// The PUT is a full replace; an ordinary edit must round-trip the value the row actually holds.
		expect(variables.body.enabled).toBe(true);
		expect(toastMock.warning).not.toHaveBeenCalled();
	});
});
