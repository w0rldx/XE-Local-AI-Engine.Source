// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ScheduledJobRunDetail } from "@/features/scheduler/components/ScheduledJobRunDetail";
import type { ScheduledJobRun } from "@/features/scheduler/models/SchedulerModels";
import { installJsdomEnvironmentMocks, renderWithMantine } from "@/test/MantineTestRender";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (_key: string, defaultValue?: string) => defaultValue ?? _key,
	}),
}));

const { hooksMock, confirmMock, deleteMock } = vi.hoisted(() => ({
	hooksMock: {
		useScheduledRunAnswer: vi.fn(),
		useDeleteScheduledRunAnswer: vi.fn(),
	},
	confirmMock: vi.fn(),
	deleteMock: vi.fn(),
}));

vi.mock("@/features/scheduler/queries/useScheduler", () => hooksMock);
vi.mock("@/core/ui/hooks/useConfirm", () => ({
	useConfirm: () => ({ confirm: confirmMock }),
}));

const succeededRun: ScheduledJobRun = {
	id: "run-1",
	scheduledJobId: "job-1",
	templateId: "run-agent",
	triggeredBy: "Manual",
	status: "Succeeded",
	scheduledFireTimeUtc: null,
	actualFireTimeUtc: 1_000,
	completedAtUtc: 2_000,
	durationMs: 1_000,
	summary: "Agent run completed on model 'local-model'.",
	errorMessage: null,
	cancellationRequestedAtUtc: null,
	createdAtUtc: 1_000,
	answerConversationId: "run-1",
};

const conversation = {
	conversationId: "run-1",
	messages: [
		{ messageId: "m-2", sequence: 2, role: "assistant", content: "Two disk **warnings** overnight." },
		{ messageId: "m-1", sequence: 1, role: "user", content: "Summarize the overnight logs." },
	],
};

function renderDetail(run: ScheduledJobRun) {
	return renderWithMantine(<ScheduledJobRunDetail run={run} isLoading={false} />);
}

describe("ScheduledJobRunDetail answer link", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		hooksMock.useScheduledRunAnswer.mockReturnValue({ isPending: false, isError: false, data: conversation });
		hooksMock.useDeleteScheduledRunAnswer.mockReturnValue({ mutateAsync: deleteMock, isPending: false });
		deleteMock.mockResolvedValue(undefined);
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("offers no answer for a run that has none", () => {
		renderDetail({ ...succeededRun, answerConversationId: null });

		expect(screen.queryByTestId("scheduler-run-detail-open-answer")).toBeNull();
		expect(hooksMock.useScheduledRunAnswer).not.toHaveBeenCalled();
	});

	it("loads the answer only when opened, then shows prompt before answer, read-only", () => {
		renderDetail(succeededRun);

		expect(screen.queryByTestId("scheduler-run-answer")).toBeNull();
		expect(hooksMock.useScheduledRunAnswer).not.toHaveBeenCalled();

		fireEvent.click(screen.getByTestId("scheduler-run-detail-open-answer"));

		expect(hooksMock.useScheduledRunAnswer).toHaveBeenCalledWith("run-1");
		const view = screen.getByTestId("scheduler-run-answer");
		expect(view.textContent?.indexOf("Summarize the overnight logs.")).toBeLessThan(view.textContent?.indexOf("Two disk") ?? -1);
		expect(screen.getByTestId("scheduler-run-answer-assistant").querySelector("strong")?.textContent).toBe("warnings");
		expect(screen.queryByRole("textbox")).toBeNull();
	});

	it("deletes the answer for good after confirmation", async () => {
		confirmMock.mockResolvedValue(true);
		renderDetail(succeededRun);
		fireEvent.click(screen.getByTestId("scheduler-run-detail-open-answer"));

		fireEvent.click(screen.getByTestId("scheduler-run-answer-delete"));

		await waitFor(() =>
			expect(deleteMock).toHaveBeenCalledWith({ path: { conversationId: "run-1" }, body: { purgeImmediately: true } }),
		);
	});

	it("keeps the answer when the confirmation is declined", async () => {
		confirmMock.mockResolvedValue(false);
		renderDetail(succeededRun);
		fireEvent.click(screen.getByTestId("scheduler-run-detail-open-answer"));

		fireEvent.click(screen.getByTestId("scheduler-run-answer-delete"));

		await waitFor(() => expect(confirmMock).toHaveBeenCalledTimes(1));
		expect(deleteMock).not.toHaveBeenCalled();
	});
});
