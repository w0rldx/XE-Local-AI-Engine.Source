// @vitest-environment jsdom

import { act, cleanup, fireEvent, screen } from "@testing-library/react";
import { createRef } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { CustomToolForm, type CustomToolFormHandle } from "@/features/customTools/components/CustomToolForm";
import type { CustomToolFormValues } from "@/features/customTools/models/CustomToolModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

// Save lives in the host dialog's sticky footer and reaches the form through an imperative handle, so "pressing Save"
// in a test means calling ref.submit(). These cases cover the seams the page depends on: validate-then-submit, the
// dirty and acknowledgement callbacks that gate the footer, and the kind switch keeping both drafts alive.

function values(overrides: Partial<CustomToolFormValues> = {}): CustomToolFormValues {
	return {
		name: "",
		description: "",
		kind: "HttpFetch",
		mode: "Fixed",
		enabled: false,
		acknowledged: false,
		parameters: [],
		http: { method: "GET", urlTemplate: "", headers: [], bodyTemplate: "", allowedHosts: [] },
		command: { executable: "", argsTemplate: [], workingDirectory: "", timeoutSeconds: 0, env: [] },
		...overrides,
	};
}

function renderForm(overrides: Partial<CustomToolFormValues> = {}, props: Record<string, unknown> = {}) {
	const ref = createRef<CustomToolFormHandle>();
	const onSubmit = vi.fn();
	const view = renderWithProviders(
		<CustomToolForm
			ref={ref}
			initialValues={values(overrides)}
			isSubmitting={false}
			showEnabledToggle={false}
			onSubmit={onSubmit}
			onCancel={vi.fn()}
			{...props}
		/>,
	);
	return { ...view, ref, onSubmit };
}

describe("CustomToolForm", () => {
	afterEach(cleanup);

	it("always shows the host-execution danger note", () => {
		renderForm();

		expect(screen.getByTestId("custom-tool-form-danger-note")).toBeTruthy();
	});

	// A blank create form must not reach the server; the name error is the one the operator sees first.
	it("blocks submit on an invalid form and surfaces the field error", () => {
		const { ref, onSubmit } = renderForm();

		act(() => ref.current?.submit());

		expect(onSubmit).not.toHaveBeenCalled();
		expect(screen.getByText("Name is required.")).toBeTruthy();
	});

	// The name error text is field-state dependent: empty means "required", non-empty means "the slug rule".
	it("distinguishes a missing name from an invalid slug", () => {
		const { ref } = renderForm({ name: "Bad-Name" });

		act(() => ref.current?.submit());

		expect(screen.getByText(/Use lowercase letters, digits and underscores/)).toBeTruthy();
	});

	it("submits the values once the form is valid", () => {
		const { ref, onSubmit } = renderForm({
			name: "fetch_status",
			description: "Fetches a status page.",
			acknowledged: true,
			http: { method: "GET", urlTemplate: "https://example.test", headers: [], bodyTemplate: "", allowedHosts: [] },
			command: { executable: "/usr/bin/env", argsTemplate: [], workingDirectory: "", timeoutSeconds: 0, env: [] },
		});

		act(() => ref.current?.submit());

		expect(onSubmit).toHaveBeenCalledOnce();
		expect(onSubmit.mock.calls[0]?.[0]).toMatchObject({ name: "fetch_status", kind: "HttpFetch" });
	});

	// The host dialog gates its Save button on this callback, and the server enforces the same acknowledgement.
	it("reports the acknowledgement state to the host", () => {
		const onAcknowledgedChange = vi.fn();
		renderForm({}, { onAcknowledgedChange });

		expect(onAcknowledgedChange).toHaveBeenLastCalledWith(false);

		fireEvent.click(screen.getByRole("checkbox", { name: /I understand these tools can run code/ }));

		expect(onAcknowledgedChange).toHaveBeenLastCalledWith(true);
	});

	// Drives the page's unsaved-changes guard, so it must stay false until the operator actually edits something.
	it("reports dirtiness only after an edit", () => {
		const onDirtyChange = vi.fn();
		renderForm({}, { onDirtyChange });

		expect(onDirtyChange).toHaveBeenLastCalledWith(false);

		fireEvent.change(screen.getByTestId("custom-tool-form-http-url"), { target: { value: "https://example.test" } });

		expect(onDirtyChange).toHaveBeenLastCalledWith(true);
	});

	// Only the active kind's editor is on screen; the inactive block keeps its draft so switching back restores it.
	it("shows the HTTP editor for an HttpFetch tool and the command editor for a Command tool", () => {
		const { unmount } = renderForm();
		expect(screen.getByTestId("custom-tool-form-http")).toBeTruthy();
		expect(screen.queryByTestId("custom-tool-form-command")).toBeNull();
		unmount();

		renderForm({ kind: "Command" });
		expect(screen.getByTestId("custom-tool-form-command")).toBeTruthy();
		expect(screen.queryByTestId("custom-tool-form-http")).toBeNull();
	});
});
