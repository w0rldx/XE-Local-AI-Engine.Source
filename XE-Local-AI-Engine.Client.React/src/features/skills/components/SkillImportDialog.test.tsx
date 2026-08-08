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

// The dialog drives the two generated import mutations; stub their mutationFns so the preview→commit wiring can be
// asserted without a backend. `withResponseValidation` preserves mutationFn, so the spies still receive the variables.
const { commitSpy, previewSpy } = vi.hoisted(() => ({ commitSpy: vi.fn(), previewSpy: vi.fn() }));
vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>()),
	commitSkillImportMutation: () => ({ mutationFn: commitSpy }),
	previewSkillImportMutation: () => ({ mutationFn: previewSpy }),
}));

import type { XeLocalAiEngineClientEndpointsSkillsV1SkillImportPreviewResponse } from "@/core/api/generated";
import { SkillImportDialog } from "@/features/skills/components/SkillImportDialog";

const report: XeLocalAiEngineClientEndpointsSkillsV1SkillImportPreviewResponse = {
	token: "report-token-1",
	sourceUri: "github:microsoft/skills",
	warnings: [],
	skills: [
		{
			name: "invoice-review",
			description: "How to review supplier invoices.",
			body: "# Review an invoice\n\nStep one.",
			license: "MIT",
			compatibility: null,
			allowedTools: "read_file",
			metadata: null,
			bodySizeBytes: 31,
			bodyLineCount: 3,
			resources: [{ name: "reference/rates.md", description: "", mediaType: "text/markdown", sizeBytes: 120 }],
			refusedScripts: ["scripts/setup.py", "scripts/run.sh"],
			conflictsWithExistingSkill: false,
			problems: [],
			canImport: true,
		},
		{
			name: "legal-redline",
			description: "How to redline a contract.",
			body: "# Redline",
			license: null,
			compatibility: null,
			allowedTools: null,
			metadata: null,
			bodySizeBytes: 9,
			bodyLineCount: 1,
			resources: [],
			refusedScripts: [],
			conflictsWithExistingSkill: true,
			problems: [],
			canImport: true,
		},
		{
			name: "bad--name",
			description: "Has a name MAF rejects.",
			body: "# Nope",
			license: null,
			compatibility: null,
			allowedTools: null,
			metadata: null,
			bodySizeBytes: 6,
			bodyLineCount: 1,
			resources: [],
			refusedScripts: [],
			conflictsWithExistingSkill: false,
			problems: ["Name 'bad--name' is not valid."],
			canImport: false,
		},
	],
};

function renderDialog() {
	const queryClient = new QueryClient({ defaultOptions: { mutations: { retry: false }, queries: { retry: false } } });
	return render(
		<QueryClientProvider client={queryClient}>
			<MantineProvider>
				<SkillImportDialog opened={true} onClose={vi.fn()} />
			</MantineProvider>
		</QueryClientProvider>,
	);
}

/** Walks the dialog to a rendered report via the Paste tab (the one source a jsdom test can fill honestly). */
async function renderReport() {
	renderDialog();

	fireEvent.click(screen.getByTestId("skill-import-tab-paste"));
	fireEvent.change(screen.getByTestId("skill-import-markdown"), { target: { value: "---\nname: x\n---\n" } });
	fireEvent.click(screen.getByTestId("skill-import-preview"));

	await waitFor(() => expect(screen.getByTestId("skill-import-report")).toBeTruthy());
}

describe("SkillImportDialog", () => {
	beforeEach(() => {
		previewSpy.mockResolvedValue(report);
		commitSpy.mockResolvedValue({ outcomes: [{ name: "invoice-review", status: "Imported", reason: null }] });
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

	it("states the consequence an operator actually needs: an enabled skill runs with the agent's tool access", () => {
		renderDialog();

		const consequence = screen.getByTestId("skill-import-warning-consequence").textContent ?? "";
		expect(consequence).toContain("tool access");
		expect(consequence).toContain("knowledge base");
		// The honest posture: we refuse code and show everything. Nothing may claim we validated it.
		expect(screen.getByTestId("skill-import-warning").textContent).toContain("does not validate, scan or sandbox");
	});

	it("keeps the Import button disabled until the acknowledgement is ticked", async () => {
		await renderReport();

		fireEvent.click(screen.getByTestId("skill-import-select-invoice-review"));
		expect((screen.getByTestId("skill-import-submit") as HTMLButtonElement).disabled).toBe(true);

		fireEvent.click(screen.getByTestId("skill-import-acknowledge"));

		expect((screen.getByTestId("skill-import-submit") as HTMLButtonElement).disabled).toBe(false);
	});

	it("keeps the Import button disabled when nothing is selected, even after acknowledging", async () => {
		await renderReport();

		fireEvent.click(screen.getByTestId("skill-import-acknowledge"));

		expect((screen.getByTestId("skill-import-submit") as HTMLButtonElement).disabled).toBe(true);
	});

	it("lists the scripts the importer refused", async () => {
		await renderReport();

		const refused = screen.getByTestId("skill-import-refused-invoice-review").textContent ?? "";
		expect(refused).toContain("scripts/setup.py");
		expect(refused).toContain("scripts/run.sh");
		expect(refused).toContain("scripts are never imported");
	});

	it("cannot select a candidate that has a problem", async () => {
		await renderReport();

		const checkbox = screen.getByTestId("skill-import-select-bad--name") as HTMLInputElement;
		expect(checkbox.disabled).toBe(true);
		expect(screen.getByTestId("skill-import-problems-bad--name").textContent).toContain("not valid");

		// jsdom still toggles a disabled input on a synthetic click, so the disabled attribute alone would not prove the
		// invariant. Force the toggle through and assert the dialog refuses to count it.
		fireEvent.click(checkbox);
		fireEvent.click(screen.getByTestId("skill-import-acknowledge"));

		expect(screen.getByTestId("skill-import-submit").textContent).toContain("Import 0 selected");
		expect((screen.getByTestId("skill-import-submit") as HTMLButtonElement).disabled).toBe(true);
	});

	it("defaults conflict resolution to Skip and offers Replace explicitly", async () => {
		await renderReport();

		// The conflict choice only appears once a conflicting candidate is actually selected.
		expect(screen.queryByTestId("skill-import-conflict-skip")).toBeNull();
		fireEvent.click(screen.getByTestId("skill-import-select-legal-redline"));

		expect((screen.getByTestId("skill-import-conflict-skip") as HTMLInputElement).checked).toBe(true);
		expect((screen.getByTestId("skill-import-conflict-replace") as HTMLInputElement).checked).toBe(false);
		// Replace has to admit what it costs, in the label itself.
		expect(screen.getByText(/loses any local edits/)).toBeTruthy();
	});

	it("shows the verbatim body once the operator expands it", async () => {
		await renderReport();

		expect(screen.queryByTestId("skill-import-body-invoice-review")).toBeNull();

		fireEvent.click(screen.getByTestId("skill-import-body-toggle-invoice-review"));

		await waitFor(() => expect(screen.getByTestId("skill-import-body-invoice-review").textContent).toContain("Review an invoice"));
	});

	it("commits the report token, the selected names and the acknowledgement", async () => {
		await renderReport();

		fireEvent.click(screen.getByTestId("skill-import-select-invoice-review"));
		fireEvent.click(screen.getByTestId("skill-import-acknowledge"));
		fireEvent.click(screen.getByTestId("skill-import-submit"));

		await waitFor(() => expect(commitSpy).toHaveBeenCalledTimes(1));
		expect(commitSpy.mock.calls[0]?.[0]).toEqual({
			body: { acknowledged: true, conflictResolution: "Skip", skillNames: ["invoice-review"], token: "report-token-1" },
		});
		// The result names what landed, and repeats that an imported skill is not live until enabled.
		await waitFor(() => expect(screen.getByTestId("skill-import-outcome-invoice-review")).toBeTruthy());
		expect(screen.getByTestId("skill-import-outcomes").textContent).toContain("disabled");
	});

	it("never bulk-imports: only the ticked candidates are sent", async () => {
		await renderReport();

		fireEvent.click(screen.getByTestId("skill-import-select-legal-redline"));
		fireEvent.click(screen.getByTestId("skill-import-acknowledge"));
		fireEvent.click(screen.getByTestId("skill-import-submit"));

		await waitFor(() => expect(commitSpy).toHaveBeenCalledTimes(1));
		expect(commitSpy.mock.calls[0]?.[0]?.body?.skillNames).toEqual(["legal-redline"]);
	});
});
