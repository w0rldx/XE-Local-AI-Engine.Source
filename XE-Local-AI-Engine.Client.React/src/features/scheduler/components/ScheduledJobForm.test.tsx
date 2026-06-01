// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ScheduledJobForm } from "@/features/scheduler/components/ScheduledJobForm";
import type { ScheduledJobFormValues, ScheduledJobTemplate } from "@/features/scheduler/models/SchedulerModels";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (_key: string, defaultValue?: string) => defaultValue ?? _key,
	}),
}));

const templates: ScheduledJobTemplate[] = [
	{
		templateId: "cleanup",
		displayName: "Cleanup",
		description: "Removes old rows",
		parameterSchema: null,
		defaultParameters: null,
		supportedScheduleKinds: ["Cron", "SimpleInterval"],
		defaultScheduleKind: "Cron",
		defaultMisfirePolicy: "Smart",
		defaultMaxRuntimeSeconds: 60,
		allowManualTrigger: true,
		allowAgentCreation: false,
		historyDetailLevel: "Summary",
	},
];

function emptyValues(): ScheduledJobFormValues {
	return {
		templateId: "",
		displayName: "",
		description: "",
		scheduleKind: "Cron",
		cronExpression: "",
		intervalSeconds: "",
		repeatCount: "",
		startAtUtc: "",
		endAtUtc: "",
		timeZoneId: "UTC",
		misfirePolicy: "Smart",
		preventOverlap: true,
		maxRuntimeSeconds: "",
		parameters: "",
	};
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

function renderForm(props: Partial<Parameters<typeof ScheduledJobForm>[0]> = {}) {
	const onSubmit = vi.fn();
	const onCancel = vi.fn();
	render(
		<MantineProvider>
			<ScheduledJobForm
				initialValues={emptyValues()}
				templates={templates}
				isEditing={false}
				isSubmitting={false}
				onSubmit={onSubmit}
				onCancel={onCancel}
				{...props}
			/>
		</MantineProvider>,
	);
	return { onSubmit, onCancel };
}

describe("ScheduledJobForm", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("blocks submit and surfaces validation errors when required fields are empty", () => {
		const { onSubmit } = renderForm();

		fireEvent.click(screen.getByTestId("scheduler-form-submit"));

		expect(onSubmit).not.toHaveBeenCalled();
	});

	it("submits a valid cron job", () => {
		const { onSubmit } = renderForm({
			initialValues: { ...emptyValues(), templateId: "cleanup", displayName: "Cleanup", cronExpression: "0 0 3 * * ?" },
		});

		fireEvent.click(screen.getByTestId("scheduler-form-submit"));

		expect(onSubmit).toHaveBeenCalledTimes(1);
	});

	it("shows the cron field for the cron schedule kind", () => {
		renderForm({ initialValues: { ...emptyValues(), scheduleKind: "Cron" } });

		expect(screen.getByTestId("scheduler-form-cron")).toBeTruthy();
	});

	it("shows the interval fields for the interval schedule kind", () => {
		renderForm({ initialValues: { ...emptyValues(), scheduleKind: "SimpleInterval" } });

		expect(screen.getByTestId("scheduler-form-interval")).toBeTruthy();
		expect(screen.getByTestId("scheduler-form-repeat-count")).toBeTruthy();
	});

	it("shows the start-at field for the one-shot schedule kind", () => {
		renderForm({ initialValues: { ...emptyValues(), scheduleKind: "OneShot" } });

		expect(screen.getByTestId("scheduler-form-start-at")).toBeTruthy();
	});

	it("disables the template picker when editing", () => {
		renderForm({ isEditing: true, initialValues: { ...emptyValues(), templateId: "cleanup", displayName: "Cleanup" } });

		const templateInput = screen.getByTestId("scheduler-form-template") as HTMLInputElement;
		expect(templateInput.disabled).toBe(true);
	});
});
