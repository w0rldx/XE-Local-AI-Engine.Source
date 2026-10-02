// @vitest-environment jsdom

// The server-side half of the job history. The list is paged BY THE NODE — every row carries a decrypted
// prompt, so a bounded window is the point — and a delete must take the cached PNG with it.

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import type { ReactNode } from "react";
import { describe, expect, it } from "vitest";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { imageBlobQueryKey } from "@/features/images/hooks/useImageObjectUrl";
import {
	useDeleteImageJob,
	useDeleteUploadedImage,
	useImageJobs,
	useUploadedImages,
	useUploadImage,
} from "@/features/images/queries/useImageQueries";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const jobId = "11111111-1111-4111-8111-111111111111";
const imageId = "22222222-2222-4222-8222-222222222222";

function job(id: string) {
	return {
		id,
		modelName: "sd-1.5",
		prompt: "a watercolor fox",
		negativePrompt: null,
		seed: "42",
		width: 512,
		height: 512,
		steps: 20,
		sampler: "euler_a",
		cfgScale: 7,
		status: "Succeeded",
		createdAtUtc: 1_700_000_000_000,
		startedAtUtc: 1_700_000_000_000,
		completedAtUtc: 1_700_000_005_000,
		durationMs: 5000,
		imageId,
		sanitizedError: null,
		cancellationRequestedAtUtc: null,
	};
}

/** Records the query string of every list read and answers one row with a total larger than the page. */
function listRoute(totalCount = 7): URLSearchParams[] {
	const requests: URLSearchParams[] = [];
	server.use(
		http.get(localApiPath("images/jobs"), ({ request }) => {
			requests.push(new URL(request.url).searchParams);
			return HttpResponse.json({ items: [job(jobId)], totalCount });
		}),
	);
	return requests;
}

function harness(): { queryClient: QueryClient; wrapper: ({ children }: { children: ReactNode }) => ReactNode } {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
	return { queryClient, wrapper: ({ children }) => <QueryClientProvider client={queryClient}>{children}</QueryClientProvider> };
}

describe("useImageJobs", () => {
	it("asks the node for the page it was given, not for every job", async () => {
		const requests = listRoute();
		const { wrapper } = harness();

		renderHook(() => useImageJobs(10, 20), { wrapper });

		await waitFor(() => {
			expect(requests).toHaveLength(1);
		});
		expect(requests.at(0)?.get("limit")).toBe("10");
		expect(requests.at(0)?.get("offset")).toBe("20");
	});

	it("surfaces the node's unpaged total, which is what a pager can honestly number", async () => {
		listRoute(42);
		const { wrapper } = harness();

		const { result } = renderHook(() => useImageJobs(10, 0), { wrapper });

		await waitFor(() => {
			expect(result.current.data).toBeDefined();
		});
		expect(result.current.data?.totalCount).toBe(42);
		expect(result.current.data?.items).toHaveLength(1);
	});
});

describe("useDeleteImageJob", () => {
	it("deletes the job, refetches the list and drops the deleted image's cached bytes", async () => {
		const requests = listRoute();
		server.use(http.delete(localApiPath(`images/jobs/${jobId}`), () => new HttpResponse(null, { status: 204 })));
		const { queryClient, wrapper } = harness();
		// Stand in for the decrypted PNG useImageObjectUrl holds under staleTime: Infinity — nothing but this delete
		// would ever evict it, so the bytes of a deleted image would otherwise outlive it for the whole session.
		queryClient.setQueryData(imageBlobQueryKey(imageId), new Blob(["png"]));

		const { result } = renderHook(() => ({ list: useImageJobs(10, 0), remove: useDeleteImageJob() }), { wrapper });

		await waitFor(() => {
			expect(requests).toHaveLength(1);
		});

		result.current.remove.mutate({ jobId, imageId });

		await waitFor(() => {
			expect(requests.length).toBeGreaterThan(1);
		});
		expect(queryClient.getQueryData(imageBlobQueryKey(imageId))).toBeUndefined();
	});

	it("surfaces the node's refusal of a job that is still running", async () => {
		listRoute();
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
		const { wrapper } = harness();

		const { result } = renderHook(() => useDeleteImageJob(), { wrapper });

		result.current.mutate({ jobId, imageId: null });

		await waitFor(() => {
			expect(result.current.isError).toBe(true);
		});
		// The node's own sentence is what reaches the operator — that, not the rejection's class, is the contract
		// the delete button renders through apiErrorMessage.
		expect(apiErrorMessage(result.current.error, "fallback")).toContain("Cancel it, then delete it.");
	});
});

const uploadId = "44444444-4444-4444-8444-444444444444";

function uploaded() {
	return { imageId: uploadId, mimeType: "image/jpeg", width: 4000, height: 3000, createdAtUtc: 1_700_000_000_000 };
}

/** Counts list reads (and keeps their query strings) so a test can see the mutations refresh the uploads. */
function uploadsListRoute(totalCount = 1): { reads: number; queries: URLSearchParams[] } {
	const counter = { reads: 0, queries: [] as URLSearchParams[] };
	server.use(
		http.get(localApiPath("images/uploads"), ({ request }) => {
			counter.reads += 1;
			counter.queries.push(new URL(request.url).searchParams);
			return HttpResponse.json({ items: [uploaded()], totalCount });
		}),
	);
	return counter;
}

describe("useUploadedImages", () => {
	it("asks the node for one page and surfaces its unpaged total", async () => {
		const list = uploadsListRoute(23);
		const { wrapper } = harness();

		const { result } = renderHook(() => useUploadedImages(10, 20), { wrapper });

		await waitFor(() => {
			expect(result.current.data).toBeDefined();
		});
		expect(list.queries.at(0)?.get("limit")).toBe("10");
		expect(list.queries.at(0)?.get("offset")).toBe("20");
		expect(result.current.data?.totalCount).toBe(23);
		expect(result.current.data?.items).toHaveLength(1);
	});
});

describe("useUploadImage", () => {
	it("posts the file as multipart under `file` and refreshes the uploads list", async () => {
		const list = uploadsListRoute();
		// Read as text: undici's FormData parser rejects the part jsdom produces, so the multipart body is read raw.
		let received: { contentType: string | null; body: string } | undefined;
		server.use(
			http.post(localApiPath("images/uploads"), async ({ request }) => {
				const contentType = request.headers.get("content-type");
				received = { contentType, body: await request.text() };
				return HttpResponse.json(uploaded());
			}),
		);
		const { wrapper } = harness();
		const { result } = renderHook(() => ({ list: useUploadedImages(10, 0), upload: useUploadImage() }), { wrapper });
		await waitFor(() => {
			expect(result.current.list.data?.items).toHaveLength(1);
		});

		result.current.upload.mutate(new File(["jpeg-bytes"], "holiday.jpg", { type: "image/jpeg" }));

		await waitFor(() => {
			expect(result.current.upload.isSuccess).toBe(true);
		});
		expect(received?.contentType).toContain("multipart/form-data");
		// jsdom's File crosses into undici as an anonymous, empty Blob, so the file name and bytes cannot be checked
		// here; the field name and the part's type are what this client controls.
		expect(received?.body).toContain('Content-Disposition: form-data; name="file"');
		expect(received?.body).toContain("Content-Type: image/jpeg");
		expect(result.current.upload.data).toEqual({ imageId: uploadId, width: 4000, height: 3000, createdAtUtc: 1_700_000_000_000 });
		await waitFor(() => {
			expect(list.reads).toBe(2);
		});
	});

	it("surfaces the node's fixed refusal sentence", async () => {
		server.use(
			http.post(localApiPath("images/uploads"), () =>
				HttpResponse.json(
					{ type: "about:blank", title: "Bad Request", status: 400, detail: "The image is larger than the upload size limit." },
					{ status: 400, headers: { "content-type": "application/problem+json" } },
				),
			),
		);
		const { wrapper } = harness();
		const { result } = renderHook(() => useUploadImage(), { wrapper });

		result.current.mutate(new File(["x"], "big.png", { type: "image/png" }));

		await waitFor(() => {
			expect(result.current.isError).toBe(true);
		});
		expect(apiErrorMessage(result.current.error, "fallback")).toBe("The image is larger than the upload size limit.");
	});
});

describe("useDeleteUploadedImage", () => {
	it("deletes the upload, refreshes the uploads and the derived jobs, and drops its cached bytes", async () => {
		const list = uploadsListRoute();
		const jobReads = listRoute();
		let deleted = false;
		server.use(
			http.delete(localApiPath(`images/uploads/${uploadId}`), () => {
				deleted = true;
				return new HttpResponse(null, { status: 204 });
			}),
		);
		const { queryClient, wrapper } = harness();
		queryClient.setQueryData(imageBlobQueryKey(uploadId), new Blob(["jpeg"]));
		const { result } = renderHook(
			() => ({ list: useUploadedImages(10, 0), jobs: useImageJobs(10, 0), remove: useDeleteUploadedImage() }),
			{ wrapper },
		);
		await waitFor(() => {
			expect(list.reads).toBe(1);
			expect(jobReads).toHaveLength(1);
		});

		result.current.remove.mutate(uploadId);

		await waitFor(() => {
			expect(list.reads).toBe(2);
		});
		// A job edited from this upload now reads back without a source, so its card can say the source was removed.
		await waitFor(() => {
			expect(jobReads).toHaveLength(2);
		});
		expect(deleted).toBe(true);
		expect(queryClient.getQueryData(imageBlobQueryKey(uploadId))).toBeUndefined();
	});
});
