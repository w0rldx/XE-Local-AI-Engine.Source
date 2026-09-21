// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ImageJobDeleteButton } from "@/features/images/components/ImageJobDeleteButton";
import type { ImageJobView } from "@/features/images/models/ImageModels";
import { installJsdomEnvironmentMocks, testMantineTheme } from "@/test/MantineTestRender";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { setupMswServer } from "@/test/UseMswServer";

const { toastMock } = vi.hoisted(() => ({
	toastMock: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warn: vi.fn(), warning: vi.fn(), progress: vi.fn() },
}));
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));

setupMswServer();

const jobId = "11111111-1111-4111-8111-111111111111";

function job(overrides: Partial<ImageJobView> = {}): ImageJobView {
	return {
		id: jobId,
		modelName: "sd-1.5",
		prompt: "a watercolor fox",
		negativePrompt: null,
		status: "Succeeded",
		seed: 42,
		width: 512,
		height: 512,
		steps: 20,
		sampler: "euler_a",
		cfgScale: 7,
		createdAtUtc: 1_700_000_000_000,
		startedAtUtc: 1_700_000_000_000,
		completedAtUtc: 1_700_000_005_000,
		durationMs: 5000,
		imageId: "22222222-2222-4222-8222-222222222222",
		sanitizedError: null,
		...overrides,
	};
}

function renderButton(view = job()) {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
	return render(
		<QueryClientProvider client={queryClient}>
			<MantineProvider env="test" theme={testMantineTheme}>
				<ImageJobDeleteButton job={view} />
			</MantineProvider>
		</QueryClientProvider>,
	);
}

describe("ImageJobDeleteButton", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		toastMock.success.mockClear();
		toastMock.error.mockClear();
	});

	afterEach(cleanup);

	// The node refuses a job it is still working on. The button says so up front rather than letting the operator
	// discover it through a 409 toast — a convenience gate over the node's own refusal, never a replacement for it.
	it("is disabled while the job is still running", () => {
		renderButton(job({ status: "Generating", imageId: null }));

		expect((screen.getByTestId("image-job-delete") as HTMLButtonElement).disabled).toBe(true);
	});

	it("is enabled once the job has ended", () => {
		renderButton(job({ status: "Failed", imageId: null, sanitizedError: "Image generation failed." }));

		expect((screen.getByTestId("image-job-delete") as HTMLButtonElement).disabled).toBe(false);
	});

	// A destructive confirmation must name what is lost. The bundle string — not the in-code default — is what the
	// operator reads, so that is what is asserted.
	it("confirms before deleting, naming the image that goes with the job", async () => {
		let deleted = 0;
		server.use(
			http.delete(localApiPath(`images/jobs/${jobId}`), () => {
				deleted += 1;
				return new HttpResponse(null, { status: 204 });
			}),
		);
		renderButton();

		fireEvent.click(screen.getByTestId("image-job-delete"));

		const dialog = await screen.findByTestId("image-job-delete-confirm");
		expect(dialog.textContent).toContain("removed from disk");
		expect(deleted).toBe(0);

		fireEvent.click(screen.getByTestId("image-job-delete-accept"));

		await waitFor(() => {
			expect(deleted).toBe(1);
		});
		await waitFor(() => {
			expect(toastMock.success).toHaveBeenCalledWith("Image job deleted.");
		});
	});

	// The realistic failure: the job started generating between this page's last read and the click. The dialog stays
	// open so a retry costs one click, and the node's own sentence is what the operator is shown.
	it("keeps the dialog open and surfaces the node's reason when the delete is refused", async () => {
		server.use(
			http.delete(localApiPath(`images/jobs/${jobId}`), () =>
				HttpResponse.json(
					{
						type: "about:blank",
						title: "Conflict",
						status: 409,
						detail: "The job is still queued or generating. Cancel it, then delete it.",
						outcome: "NotTerminal",
					},
					{ status: 409, headers: { "content-type": "application/problem+json" } },
				),
			),
		);
		renderButton();

		fireEvent.click(screen.getByTestId("image-job-delete"));
		fireEvent.click(await screen.findByTestId("image-job-delete-accept"));

		await waitFor(() => {
			expect(toastMock.error).toHaveBeenCalledWith("The job is still queued or generating. Cancel it, then delete it.");
		});
		expect(screen.queryByTestId("image-job-delete-confirm")).not.toBeNull();
	});
});
