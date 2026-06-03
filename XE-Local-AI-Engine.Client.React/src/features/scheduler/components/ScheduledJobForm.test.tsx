// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { createRef } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ScheduledJobForm, type ScheduledJobFormHandle } from "@/features/scheduler/components/ScheduledJobForm";
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
	const ref = createRef<ScheduledJobFormHandle>();
	render(
		<MantineProvider>
			<ScheduledJobForm
				ref={ref}
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
	return { onSubmit, onCancel, ref };
}

// The form exposes a submit() handle via useImperativeHandle. Call ref.current.submit()
// to trigger internal Zod validation — same path the footer Save button uses.
function submitViaHandle(ref: React.RefObject<ScheduledJobFormHandle | null>) {
	ref.current?.submit();
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
		const { onSubmit, ref } = renderForm();

		submitViaHandle(ref);

		expect(onSubmit).not.toHaveBeenCalled();
	});

	it("submits a valid cron job", () => {
		const { onSubmit, ref } = renderForm({
			initialValues: { ...emptyValues(), templateId: "cleanup", displayName: "Cleanup", cronExpression: "0 0 3 * * ?" },
		});

		submitViaHandle(ref);

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

	it("hides the cron/interval/start-at inputs for the manual schedule kind", () => {
		renderForm({ initialValues: { ...emptyValues(), scheduleKind: "Manual" } });

		expect(screen.queryByTestId("scheduler-form-cron")).toBeNull();
		expect(screen.queryByTestId("scheduler-form-interval")).toBeNull();
		expect(screen.queryByTestId("scheduler-form-repeat-count")).toBeNull();
		expect(screen.queryByTestId("scheduler-form-start-at")).toBeNull();
		expect(screen.getByTestId("scheduler-form-manual-note")).toBeTruthy();
	});

	it("submits a manual job without schedule fields", () => {
		const { onSubmit, ref } = renderForm({
			initialValues: { ...emptyValues(), templateId: "cleanup", displayName: "On demand", scheduleKind: "Manual" },
		});

		submitViaHandle(ref);

		expect(onSubmit).toHaveBeenCalledTimes(1);
	});

	it("disables the template picker when editing", () => {
		renderForm({ isEditing: true, initialValues: { ...emptyValues(), templateId: "cleanup", displayName: "Cleanup" } });

		const templateInput = screen.getByTestId("scheduler-form-template") as HTMLInputElement;
		expect(templateInput.disabled).toBe(true);
	});

	it("calls onDirtyChange with true when a field is modified", () => {
		const onDirtyChange = vi.fn();
		renderForm({ onDirtyChange });

		fireEvent.change(screen.getByTestId("scheduler-form-name"), { target: { value: "New name" } });

		expect(onDirtyChange).toHaveBeenCalledWith(true);
	});

	it("calls onDirtyChange with false when values match initialValues", () => {
		const onDirtyChange = vi.fn();
		renderForm({
			initialValues: { ...emptyValues(), displayName: "Existing" },
			onDirtyChange,
		});

		// On mount the values equal initialValues — onDirtyChange should fire with false.
		expect(onDirtyChange).toHaveBeenCalledWith(false);
	});
});
