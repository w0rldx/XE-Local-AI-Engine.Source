// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ScheduledJobList } from "@/features/scheduler/components/ScheduledJobList";
import type { ScheduledJob } from "@/features/scheduler/models/SchedulerModels";

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

const cronJob: ScheduledJob = {
	id: "job-1",
	templateId: "cleanup",
	displayName: "Nightly cleanup",
	description: "Removes old rows",
	enabled: false,
	scheduleKind: "Cron",
	cronExpression: "0 0 3 * * ?",
	intervalSeconds: null,
	repeatCount: null,
	startAtUtc: null,
	endAtUtc: null,
	timeZoneId: "UTC",
	misfirePolicy: "Smart",
	preventOverlap: true,
	maxRuntimeSeconds: null,
	hasParameters: true,
	createdBy: "User",
	createdAtUtc: 1000,
	updatedAtUtc: 2000,
	disabledAtUtc: null,
	deletedAtUtc: null,
};

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

function renderList(props: Partial<Parameters<typeof ScheduledJobList>[0]> = {}) {
	const handlers = {
		onEdit: vi.fn(),
		onDelete: vi.fn(),
		onTrigger: vi.fn(),
		onToggleEnabled: vi.fn(),
	};
	render(
		<MantineProvider>
			<ScheduledJobList jobs={[cronJob]} isMutating={false} {...handlers} {...props} />
		</MantineProvider>,
	);
	return handlers;
}

describe("ScheduledJobList", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders the jobs table with the cron schedule summary", () => {
		renderList();

		expect(screen.getByTestId("scheduler-jobs-table")).toBeTruthy();
		expect(screen.getByText("0 0 3 * * ?")).toBeTruthy();
	});

	it("shows a has-parameters badge instead of the raw parameter value", () => {
		renderList();

		expect(screen.getByTestId("scheduler-job-has-parameters-job-1")).toBeTruthy();
	});

	it("shows the empty state when there are no jobs", () => {
		renderList({ jobs: [] });

		expect(screen.getByTestId("scheduler-jobs-empty")).toBeTruthy();
	});

	it("toggles a job enabled through the row switch", () => {
		const handlers = renderList();

		fireEvent.click(screen.getByTestId("scheduler-job-enabled-job-1"));

		expect(handlers.onToggleEnabled).toHaveBeenCalledWith(cronJob, true);
	});

	it("triggers a job through the row action", () => {
		const handlers = renderList();

		fireEvent.click(screen.getByTestId("scheduler-job-trigger-job-1"));

		expect(handlers.onTrigger).toHaveBeenCalledWith(cronJob);
	});

	it("edits a job through the row action", () => {
		const handlers = renderList();

		fireEvent.click(screen.getByTestId("scheduler-job-edit-job-1"));

		expect(handlers.onEdit).toHaveBeenCalledWith("job-1");
	});
});
