// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
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

const { hooksMock, monitorHookMock, confirmMock, toastMock } = vi.hoisted(() => ({
	hooksMock: {
		usePlaybookActions: vi.fn(),
		useCreatePlaybookAction: vi.fn(),
		useUpdatePlaybookAction: vi.fn(),
		useUpdateSuggestedAction: vi.fn(),
		useDeletePlaybookAction: vi.fn(),
		useAnalyzePlaybook: vi.fn(),
		usePromoteSuggestedAction: vi.fn(),
		useRejectSuggestedAction: vi.fn(),
		useRunEval: vi.fn(),
	},
	monitorHookMock: {
		usePlaybookMonitor: vi.fn(),
	},
	confirmMock: vi.fn(),
	toastMock: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warn: vi.fn(), warning: vi.fn(), progress: vi.fn() },
}));

vi.mock("@/features/agents/queries/usePlaybookActions", () => hooksMock);
vi.mock("@/features/agents/queries/usePlaybookMonitor", () => monitorHookMock);
vi.mock("@/core/ui/hooks/useConfirm", () => ({
	useConfirm: () => ({ confirm: confirmMock }),
}));
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));

import { PlaybookPanel } from "@/features/agents/components/PlaybookPanel";
import { PromoteConflictError } from "@/features/agents/models/PlaybookActionMappers";
import type { EvalResult, PlaybookAction } from "@/features/agents/models/PlaybookActionModels";
import type { PlaybookMonitor, PlaybookMonitorItem } from "@/features/agents/models/PlaybookMonitorModels";

function makeAction(overrides: Partial<PlaybookAction> = {}): PlaybookAction {
	return {
		id: "action-1",
		agentDefinitionId: "agent-1",
		state: "Enabled",
		source: "Manual",
		triggerCondition: null,
		behavior: "Always cite your sources",
		scope: null,
		priority: 0,
		version: 1,
		createdAtUtc: 1000,
		updatedAtUtc: 2000,
		sourceFeedbackIds: null,
		confidence: null,
		evalResult: null,
		...overrides,
	};
}

// A passing/failing eval result keyed to the action's current version (so the gate is not "stale"). `passed`
// drives the Approve gate; `regressedCaseCount`/`cases` drive the eval summary + regressed-cases list.
function makeEvalResult(overrides: Partial<EvalResult> = {}): EvalResult {
	return {
		passed: true,
		evaluatedAtUtc: 1748600000000,
		actionVersionAtEval: 1,
		modelName: "qwen3.5:9b",
		goldenCaseCount: 3,
		goldenCaseTotal: 3,
		baselinePassCount: 3,
		candidatePassCount: 3,
		regressedCaseCount: 0,
		improvedCaseCount: 0,
		cases: [{ goldenCaseId: "g-1", scoredBy: "assertion", baselinePass: true, candidatePass: true, regressed: false }],
		...overrides,
	};
}

// A Suggested analysis proposal: source "Analysis", state "Suggested", with cited feedback ids + confidence.
function makeSuggestedAction(overrides: Partial<PlaybookAction> = {}): PlaybookAction {
	return makeAction({
		id: "suggested-1",
		state: "Suggested",
		source: "Analysis",
		behavior: "Summarize before answering",
		sourceFeedbackIds: ["fb-1", "fb-2"],
		confidence: 0.82,
		...overrides,
	});
}

function makeMutation() {
	return { mutate: vi.fn(), isPending: false, error: null, variables: undefined };
}

// A cohort-monitoring signal for one Enabled action (joined by actionId).
function makeMonitorItem(overrides: Partial<PlaybookMonitorItem> = {}): PlaybookMonitorItem {
	return {
		actionId: "action-1",
		enabledAtUtc: 1748600000000,
		beforeDownRate: 0.12,
		afterDownRate: 0.05,
		afterSampleSize: 8,
		status: "Improved",
		flagged: false,
		facetToolName: null,
		...overrides,
	};
}

// The monitor query result the panel reads: items[] + retrieval config. Defaults to an empty cohort + the
// pinned threshold/topK so the relevance banner is OFF unless a test opts into more Enabled actions.
function makeMonitor(overrides: Partial<PlaybookMonitor> = {}): PlaybookMonitor {
	return {
		items: [],
		// Default to the lexical ranker (embeddings off) — the effective default + auto-fallback.
		retrieval: { threshold: 8, topK: 8, ranker: "lexical", embeddingModel: null },
		...overrides,
	};
}

function makeMonitorQuery(monitor: PlaybookMonitor = makeMonitor()) {
	return { data: monitor, isLoading: false, error: null };
}

// Analyze mutation also exposes isSuccess/data (the empty-result notice reads them).
function makeAnalyzeMutation(overrides: Record<string, unknown> = {}) {
	return { mutate: vi.fn(), isPending: false, isSuccess: false, error: null, data: undefined, ...overrides };
}

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
	Object.defineProperty(document, "fonts", {
		writable: true,
		value: { ready: Promise.resolve(), addEventListener: vi.fn(), removeEventListener: vi.fn() },
	});
}

function renderPanel(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

describe("PlaybookPanel", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		hooksMock.usePlaybookActions.mockReturnValue({ data: [makeAction()], isLoading: false, error: null });
		monitorHookMock.usePlaybookMonitor.mockReturnValue(makeMonitorQuery());
		hooksMock.useCreatePlaybookAction.mockReturnValue(makeMutation());
		hooksMock.useUpdatePlaybookAction.mockReturnValue(makeMutation());
		hooksMock.useUpdateSuggestedAction.mockReturnValue(makeMutation());
		hooksMock.useDeletePlaybookAction.mockReturnValue(makeMutation());
		hooksMock.useAnalyzePlaybook.mockReturnValue(makeAnalyzeMutation());
		hooksMock.usePromoteSuggestedAction.mockReturnValue(makeMutation());
		hooksMock.useRejectSuggestedAction.mockReturnValue(makeMutation());
		hooksMock.useRunEval.mockReturnValue(makeMutation());
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders nothing when the capability gate is off", () => {
		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={false} />);

		// No panel surface is rendered (the component returns null behind the gate).
		expect(screen.queryByTestId("playbook-panel-agent-1")).toBeNull();
		expect(screen.queryByTestId("playbook-add-button")).toBeNull();
		// The query is disabled (null agent id) when the panel is gated off.
		expect(hooksMock.usePlaybookActions).toHaveBeenCalledWith(null);
	});

	it("lists actions and renders the Manual provenance badge", () => {
		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		expect(screen.getByTestId("playbook-action-action-1")).toBeTruthy();
		expect(screen.getByTestId("playbook-source-action-1").textContent).toBe("Manual");
		expect(screen.getByText("Always cite your sources")).toBeTruthy();
	});

	it("shows the empty state when there are no actions", () => {
		hooksMock.usePlaybookActions.mockReturnValue({ data: [], isLoading: false, error: null });

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		expect(screen.getByTestId("playbook-empty")).toBeTruthy();
	});

	it("toggles an action's state through the update mutation without dropping its fields", () => {
		const updateMutation = makeMutation();
		hooksMock.useUpdatePlaybookAction.mockReturnValue(updateMutation);

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		fireEvent.click(screen.getByTestId("playbook-toggle-action-1"));

		expect(updateMutation.mutate).toHaveBeenCalledWith(
			expect.objectContaining({
				actionId: "action-1",
				request: expect.objectContaining({ state: "Disabled", behavior: "Always cite your sources" }),
			}),
		);
	});

	it("opens the add editor and creates an action", () => {
		const createMutation = makeMutation();
		hooksMock.useCreatePlaybookAction.mockReturnValue(createMutation);
		hooksMock.usePlaybookActions.mockReturnValue({ data: [], isLoading: false, error: null });

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		fireEvent.click(screen.getByTestId("playbook-add-button"));
		const behavior = screen.getByTestId("playbook-form-behavior") as HTMLTextAreaElement;
		fireEvent.change(behavior, { target: { value: "Be concise" } });
		fireEvent.click(screen.getByTestId("playbook-form-submit"));

		expect(createMutation.mutate).toHaveBeenCalledWith(
			expect.objectContaining({ behavior: "Be concise", state: "Enabled" }),
			expect.anything(),
		);
	});

	it("blocks create submit when behavior is empty", () => {
		const createMutation = makeMutation();
		hooksMock.useCreatePlaybookAction.mockReturnValue(createMutation);

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		fireEvent.click(screen.getByTestId("playbook-add-button"));
		fireEvent.click(screen.getByTestId("playbook-form-submit"));

		expect(createMutation.mutate).not.toHaveBeenCalled();
		expect(screen.getByText("Behavior is required")).toBeTruthy();
	});

	it("surfaces a load error", () => {
		hooksMock.usePlaybookActions.mockReturnValue({ data: undefined, isLoading: false, error: new Error("boom") });

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		expect(screen.getByTestId("playbook-list-error")).toBeTruthy();
	});

	it("renders a Suggested action with the Analysis badge, confidence, evidence affordance and review controls", () => {
		hooksMock.usePlaybookActions.mockReturnValue({
			data: [makeAction(), makeSuggestedAction()],
			isLoading: false,
			error: null,
		});

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		// Suggested proposal renders in its own section, separate from the manual action.
		expect(screen.getByTestId("playbook-suggested-section")).toBeTruthy();
		expect(screen.getByTestId("playbook-suggested-suggested-1")).toBeTruthy();
		expect(screen.getByTestId("playbook-suggested-source-suggested-1").textContent).toBe("Analysis");
		// Confidence 0.82 → 82%.
		expect(screen.getByTestId("playbook-suggested-confidence-suggested-1").textContent).toContain("82%");
		expect(screen.getByText("Summarize before answering")).toBeTruthy();
		// Evidence affordance summarizes the two cited feedback ids.
		expect(screen.getByTestId("playbook-suggested-evidence-toggle-suggested-1").textContent).toContain("2");
		// Approve / Edit / Reject controls are present.
		expect(screen.getByTestId("playbook-suggested-approve-suggested-1")).toBeTruthy();
		expect(screen.getByTestId("playbook-suggested-edit-suggested-1")).toBeTruthy();
		expect(screen.getByTestId("playbook-suggested-reject-suggested-1")).toBeTruthy();

		// The manual action stays out of the Suggested section (rendered as a normal action row).
		expect(screen.getByTestId("playbook-action-action-1")).toBeTruthy();
	});

	it("expands the evidence affordance to reveal the cited feedback ids", () => {
		hooksMock.usePlaybookActions.mockReturnValue({
			data: [makeSuggestedAction()],
			isLoading: false,
			error: null,
		});

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		fireEvent.click(screen.getByTestId("playbook-suggested-evidence-toggle-suggested-1"));

		const evidence = screen.getByTestId("playbook-suggested-evidence-suggested-1");
		expect(evidence.textContent).toContain("fb-1");
		expect(evidence.textContent).toContain("fb-2");
	});

	it("triggers the analyze call when the Analyze feedback button is clicked", () => {
		const analyzeMutation = makeAnalyzeMutation();
		hooksMock.useAnalyzePlaybook.mockReturnValue(analyzeMutation);

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		fireEvent.click(screen.getByTestId("playbook-analyze-button"));

		expect(analyzeMutation.mutate).toHaveBeenCalledTimes(1);
	});

	it("shows the no-new-suggestions notice after an empty analyze run", () => {
		hooksMock.useAnalyzePlaybook.mockReturnValue(makeAnalyzeMutation({ isSuccess: true, data: [] }));

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		expect(screen.getByTestId("playbook-no-suggestions")).toBeTruthy();
	});

	it("promotes a Suggested action through the promote mutation when Approve is clicked (eval passed)", () => {
		const promoteMutation = makeMutation();
		hooksMock.usePromoteSuggestedAction.mockReturnValue(promoteMutation);
		// Approve is eval-gated: a passing eval (matching the action version) is required to enable the control.
		hooksMock.usePlaybookActions.mockReturnValue({
			data: [makeSuggestedAction({ evalResult: makeEvalResult() })],
			isLoading: false,
			error: null,
		});

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		const approve = screen.getByTestId("playbook-suggested-approve-suggested-1") as HTMLButtonElement;
		expect(approve.disabled).toBe(false);
		fireEvent.click(approve);

		expect(promoteMutation.mutate).toHaveBeenCalledWith("suggested-1", { onError: expect.any(Function) });
	});

	it("renders the evalResult pass badge + counts and disables Approve only when not passed", () => {
		hooksMock.usePlaybookActions.mockReturnValue({
			data: [makeSuggestedAction({ evalResult: makeEvalResult() })],
			isLoading: false,
			error: null,
		});

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		expect(screen.getByTestId("playbook-suggested-eval-status-suggested-1").textContent).toContain("passed");
		expect(screen.getByTestId("playbook-suggested-eval-counts-suggested-1").textContent).toContain("0");
		// Passing eval (current version) → Approve enabled.
		expect((screen.getByTestId("playbook-suggested-approve-suggested-1") as HTMLButtonElement).disabled).toBe(false);
		// No truncation when the evaluated count equals the total → the "evaluated N of TOTAL" note is absent.
		expect(screen.queryByTestId("playbook-suggested-eval-truncated-suggested-1")).toBeNull();
	});

	it("surfaces the truncation note when the golden set was larger than the evaluated subset", () => {
		hooksMock.usePlaybookActions.mockReturnValue({
			data: [makeSuggestedAction({ evalResult: makeEvalResult({ goldenCaseCount: 25, goldenCaseTotal: 40 }) })],
			isLoading: false,
			error: null,
		});

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		const note = screen.getByTestId("playbook-suggested-eval-truncated-suggested-1");
		expect(note.textContent).toContain("25");
		expect(note.textContent).toContain("40");
	});

	it("DISABLES Approve until the eval passes (no eval, regressed, and stale)", () => {
		// No eval yet → Approve disabled.
		hooksMock.usePlaybookActions.mockReturnValue({ data: [makeSuggestedAction()], isLoading: false, error: null });
		const { rerender } = renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);
		expect((screen.getByTestId("playbook-suggested-approve-suggested-1") as HTMLButtonElement).disabled).toBe(true);
		// No eval result → no eval summary rendered.
		expect(screen.queryByTestId("playbook-suggested-eval-suggested-1")).toBeNull();

		// A failing (regressed) eval → Approve stays disabled and the regressed list is shown.
		hooksMock.usePlaybookActions.mockReturnValue({
			data: [
				makeSuggestedAction({
					evalResult: makeEvalResult({
						passed: false,
						regressedCaseCount: 1,
						candidatePassCount: 2,
						cases: [{ goldenCaseId: "g-1", scoredBy: "judge", baselinePass: true, candidatePass: false, regressed: true }],
					}),
				}),
			],
			isLoading: false,
			error: null,
		});
		rerender(
			<MantineProvider>
				<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />
			</MantineProvider>,
		);
		expect((screen.getByTestId("playbook-suggested-approve-suggested-1") as HTMLButtonElement).disabled).toBe(true);
		expect(screen.getByTestId("playbook-suggested-eval-status-suggested-1").textContent).toContain("failed");
		// Expand the regressed cases and confirm the goldenCaseId + how it was scored render.
		fireEvent.click(screen.getByTestId("playbook-suggested-eval-toggle-suggested-1"));
		const regressed = screen.getByTestId("playbook-suggested-eval-regressed-suggested-1");
		expect(regressed.textContent).toContain("g-1");
		expect(regressed.textContent).toContain("judge");

		// A passing but STALE eval (ran against an older version) → Approve stays disabled.
		hooksMock.usePlaybookActions.mockReturnValue({
			data: [makeSuggestedAction({ version: 5, evalResult: makeEvalResult({ passed: true, actionVersionAtEval: 3 }) })],
			isLoading: false,
			error: null,
		});
		rerender(
			<MantineProvider>
				<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />
			</MantineProvider>,
		);
		expect((screen.getByTestId("playbook-suggested-approve-suggested-1") as HTMLButtonElement).disabled).toBe(true);
	});

	it("runs the eval through the run-eval mutation when Run eval is clicked", () => {
		const runEvalMutation = makeMutation();
		hooksMock.useRunEval.mockReturnValue(runEvalMutation);
		hooksMock.usePlaybookActions.mockReturnValue({ data: [makeSuggestedAction()], isLoading: false, error: null });

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		fireEvent.click(screen.getByTestId("playbook-suggested-run-eval-suggested-1"));

		expect(runEvalMutation.mutate).toHaveBeenCalledWith("suggested-1", { onError: expect.any(Function) });
	});

	it("surfaces a 409 promote conflict reason as an error toast", () => {
		// The mocked mutate invokes the promote handler's onError with the typed conflict, mirroring a 409.
		hooksMock.usePromoteSuggestedAction.mockReturnValue({
			mutate: (_id: string, options?: { onError?: (error: unknown) => void }) =>
				options?.onError?.(new PromoteConflictError("EvalRegressed", "Candidate regressed golden case g-1.")),
			isPending: false,
			error: null,
			variables: undefined,
		});
		// A passing eval (matching the action version) ungates the Approve button.
		hooksMock.usePlaybookActions.mockReturnValue({
			data: [makeSuggestedAction({ evalResult: makeEvalResult() })],
			isLoading: false,
			error: null,
		});

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		fireEvent.click(screen.getByTestId("playbook-suggested-approve-suggested-1"));

		// The panel maps the typed conflict status to a localized reason in the error toast.
		expect(toastMock.error).toHaveBeenCalledWith(expect.stringContaining("regressed"));
	});

	it("rejects a Suggested action through the reject mutation after confirmation", async () => {
		confirmMock.mockResolvedValue(true);
		const rejectMutation = makeMutation();
		hooksMock.useRejectSuggestedAction.mockReturnValue(rejectMutation);
		hooksMock.usePlaybookActions.mockReturnValue({ data: [makeSuggestedAction()], isLoading: false, error: null });

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		fireEvent.click(screen.getByTestId("playbook-suggested-reject-suggested-1"));
		// The reject handler awaits the confirm dialog before firing the mutation.
		await Promise.resolve();

		expect(confirmMock).toHaveBeenCalled();
		expect(rejectMutation.mutate).toHaveBeenCalledWith("suggested-1", { onError: expect.any(Function) });
	});

	it("opens the edit form for a Suggested action without the Enabled/Disabled state field (edit-before-approve)", () => {
		hooksMock.usePlaybookActions.mockReturnValue({ data: [makeSuggestedAction()], isLoading: false, error: null });

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		fireEvent.click(screen.getByTestId("playbook-suggested-edit-suggested-1"));

		// The inline form opens, pre-filled with the proposal's behavior.
		const behavior = screen.getByTestId("playbook-form-behavior") as HTMLTextAreaElement;
		expect(behavior.value).toBe("Summarize before answering");
		// The state Select is hidden for a Suggested edit — it stays Suggested until Approve.
		expect(screen.queryByTestId("playbook-form-state")).toBeNull();
	});

	it("saves a Suggested edit via the dedicated /suggested mutation (state-less body), not the manual PUT", () => {
		const updateSuggestedMutation = makeMutation();
		const updateMutation = makeMutation();
		hooksMock.useUpdateSuggestedAction.mockReturnValue(updateSuggestedMutation);
		hooksMock.useUpdatePlaybookAction.mockReturnValue(updateMutation);
		hooksMock.usePlaybookActions.mockReturnValue({ data: [makeSuggestedAction()], isLoading: false, error: null });

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		fireEvent.click(screen.getByTestId("playbook-suggested-edit-suggested-1"));
		const behavior = screen.getByTestId("playbook-form-behavior") as HTMLTextAreaElement;
		fireEvent.change(behavior, { target: { value: "Always summarize first" } });
		fireEvent.click(screen.getByTestId("playbook-form-submit"));

		// The dedicated Suggested-edit mutation fires with the state-less body; the manual PUT is never used.
		expect(updateSuggestedMutation.mutate).toHaveBeenCalledTimes(1);
		expect(updateSuggestedMutation.mutate).toHaveBeenCalledWith(
			expect.objectContaining({
				actionId: "suggested-1",
				request: expect.objectContaining({ behavior: "Always summarize first", priority: 0 }),
			}),
			expect.anything(),
		);
		// The body carries no `state` field (the action stays Suggested).
		const variables = updateSuggestedMutation.mutate.mock.calls.at(0)?.at(0) as { request: Record<string, unknown> };
		expect(variables.request).not.toHaveProperty("state");
		expect(updateMutation.mutate).not.toHaveBeenCalled();
	});

	it("saves a manual edit via the manual update mutation, not the /suggested route", () => {
		const updateSuggestedMutation = makeMutation();
		const updateMutation = makeMutation();
		hooksMock.useUpdateSuggestedAction.mockReturnValue(updateSuggestedMutation);
		hooksMock.useUpdatePlaybookAction.mockReturnValue(updateMutation);
		hooksMock.usePlaybookActions.mockReturnValue({ data: [makeAction()], isLoading: false, error: null });

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		fireEvent.click(screen.getByTestId("playbook-edit-action-1"));
		fireEvent.click(screen.getByTestId("playbook-form-submit"));

		expect(updateMutation.mutate).toHaveBeenCalledTimes(1);
		expect(updateSuggestedMutation.mutate).not.toHaveBeenCalled();
	});

	it("disables the monitor query when the capability gate is off", () => {
		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={false} />);

		expect(monitorHookMock.usePlaybookMonitor).toHaveBeenCalledWith(null);
	});

	it("renders the cap indicator with the live Enabled count", () => {
		hooksMock.usePlaybookActions.mockReturnValue({
			data: [makeAction({ id: "action-1", state: "Enabled" }), makeAction({ id: "action-2", state: "Disabled" })],
			isLoading: false,
			error: null,
		});

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		// Only the Enabled action counts toward the cap indicator.
		expect(screen.getByTestId("playbook-cap-indicator").textContent).toContain("1");
	});

	it("renders the monitoring signal (status badge + before→after down-rate) for an Enabled action", () => {
		monitorHookMock.usePlaybookMonitor.mockReturnValue(
			makeMonitorQuery(makeMonitor({ items: [makeMonitorItem({ actionId: "action-1" })] })),
		);

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		expect(screen.getByTestId("playbook-monitor-status-action-1").textContent).toContain("Improved");
		// 0.12 → 12%, 0.05 → 5%.
		const rate = screen.getByTestId("playbook-monitor-rate-action-1");
		expect(rate.textContent).toContain("12%");
		expect(rate.textContent).toContain("5%");
		// Not flagged → no flag marker.
		expect(screen.queryByTestId("playbook-monitor-flag-action-1")).toBeNull();
	});

	it("renders the flag marker when the monitored action is flagged for review", () => {
		monitorHookMock.usePlaybookMonitor.mockReturnValue(
			makeMonitorQuery(makeMonitor({ items: [makeMonitorItem({ actionId: "action-1", status: "Regressed", flagged: true })] })),
		);

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		expect(screen.getByTestId("playbook-monitor-flag-action-1")).toBeTruthy();
		expect(screen.getByTestId("playbook-monitor-status-action-1").textContent).toContain("Regressed");
	});

	it("renders a neutral placeholder when an Enabled action has no monitor signal yet", () => {
		// No monitor items → the Enabled action has no enable clock yet → neutral placeholder, no badge.
		monitorHookMock.usePlaybookMonitor.mockReturnValue(makeMonitorQuery(makeMonitor({ items: [] })));

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		expect(screen.getByTestId("playbook-monitor-none-action-1")).toBeTruthy();
		expect(screen.queryByTestId("playbook-monitor-status-action-1")).toBeNull();
	});

	it("does NOT render the monitoring signal for a Disabled action", () => {
		hooksMock.usePlaybookActions.mockReturnValue({
			data: [makeAction({ id: "action-1", state: "Disabled" })],
			isLoading: false,
			error: null,
		});
		monitorHookMock.usePlaybookMonitor.mockReturnValue(
			makeMonitorQuery(makeMonitor({ items: [makeMonitorItem({ actionId: "action-1" })] })),
		);

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		expect(screen.queryByTestId("playbook-monitor-action-1")).toBeNull();
		expect(screen.queryByTestId("playbook-monitor-none-action-1")).toBeNull();
	});

	it("shows the relevance-gated banner only when the Enabled count exceeds the retrieval threshold", () => {
		const enabledActions = Array.from({ length: 3 }, (_, i) => makeAction({ id: `action-${i}`, state: "Enabled", priority: i }));
		hooksMock.usePlaybookActions.mockReturnValue({ data: enabledActions, isLoading: false, error: null });

		// threshold 2, 3 Enabled → banner shows with topK + count.
		monitorHookMock.usePlaybookMonitor.mockReturnValue(
			makeMonitorQuery(makeMonitor({ retrieval: { threshold: 2, topK: 2, ranker: "lexical", embeddingModel: null } })),
		);

		const { rerender } = renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		const banner = screen.getByTestId("playbook-relevance-banner");
		expect(banner.textContent).toContain("2");
		expect(banner.textContent).toContain("3");

		// threshold 3, 3 Enabled (not strictly greater) → banner hidden.
		monitorHookMock.usePlaybookMonitor.mockReturnValue(
			makeMonitorQuery(makeMonitor({ retrieval: { threshold: 3, topK: 2, ranker: "lexical", embeddingModel: null } })),
		);
		rerender(
			<MantineProvider>
				<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />
			</MantineProvider>,
		);
		expect(screen.queryByTestId("playbook-relevance-banner")).toBeNull();
	});

	it("names the embedding ranker (with the model) in the relevance banner when embeddings are active", () => {
		const enabledActions = Array.from({ length: 3 }, (_, i) => makeAction({ id: `action-${i}`, state: "Enabled", priority: i }));
		hooksMock.usePlaybookActions.mockReturnValue({ data: enabledActions, isLoading: false, error: null });
		monitorHookMock.usePlaybookMonitor.mockReturnValue(
			makeMonitorQuery(
				makeMonitor({ retrieval: { threshold: 2, topK: 2, ranker: "embedding", embeddingModel: "nomic-embed-text" } }),
			),
		);

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		const ranker = screen.getByTestId("playbook-relevance-ranker");
		expect(ranker.textContent).toContain("embedding similarity");
		expect(ranker.textContent).toContain("nomic-embed-text");
	});

	it("names the lexical ranker in the relevance banner when embeddings are off", () => {
		const enabledActions = Array.from({ length: 3 }, (_, i) => makeAction({ id: `action-${i}`, state: "Enabled", priority: i }));
		hooksMock.usePlaybookActions.mockReturnValue({ data: enabledActions, isLoading: false, error: null });
		monitorHookMock.usePlaybookMonitor.mockReturnValue(
			makeMonitorQuery(makeMonitor({ retrieval: { threshold: 2, topK: 2, ranker: "lexical", embeddingModel: null } })),
		);

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		const ranker = screen.getByTestId("playbook-relevance-ranker");
		expect(ranker.textContent).toContain("lexical overlap");
		// No embedding model name leaks into the lexical clause.
		expect(ranker.textContent).not.toContain("nomic-embed-text");
	});

	it("surfaces the CapReached 409 reason as an error toast", () => {
		hooksMock.usePromoteSuggestedAction.mockReturnValue({
			mutate: (_id: string, options?: { onError?: (error: unknown) => void }) =>
				options?.onError?.(new PromoteConflictError("CapReached", "Archive an enabled action before promoting (cap reached).")),
			isPending: false,
			error: null,
			variables: undefined,
		});
		hooksMock.usePlaybookActions.mockReturnValue({
			data: [makeSuggestedAction({ evalResult: makeEvalResult() })],
			isLoading: false,
			error: null,
		});

		renderPanel(<PlaybookPanel agentDefinitionId="agent-1" agentName="Researcher" enabled={true} />);

		fireEvent.click(screen.getByTestId("playbook-suggested-approve-suggested-1"));

		// The typed CapReached conflict maps to a localized cap reason in the error toast.
		expect(toastMock.error).toHaveBeenCalledWith(expect.stringContaining("cap"));
	});
});
