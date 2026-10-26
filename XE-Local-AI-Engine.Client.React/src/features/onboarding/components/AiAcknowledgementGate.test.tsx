// @vitest-environment jsdom

// The gate over the real generated SDK: MSW answers the acknowledgement routes, so what is pinned is that the server's
// answer decides whether the notice shows and that accepting records it and closes the notice.

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, describe, expect, it } from "vitest";

import { AiAcknowledgementGate } from "@/features/onboarding/components/AiAcknowledgementGate";
import { jsonRoute, localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const route = "tutorial-state/ai-acknowledgement";

afterEach(cleanup);

describe("AiAcknowledgementGate", () => {
	it("shows the notice with no way to dismiss it while the user has not acknowledged", async () => {
		server.use(jsonRoute("get", route, { acknowledged: false, acknowledgedAtUtc: null }));

		renderWithProviders(<AiAcknowledgementGate />);

		expect(await screen.findByText(/AI models can make mistakes/)).toBeTruthy();
		expect(screen.getByText(/responsible for checking every result/)).toBeTruthy();
		expect(screen.getByText(/responsible for anything an agent changes or breaks/)).toBeTruthy();
		expect(screen.queryByRole("button", { name: /close/i })).toBeNull();
	});

	it("stays hidden once the user has acknowledged", async () => {
		let reads = 0;
		server.use(
			http.get(localApiPath(route), () => {
				reads += 1;
				return HttpResponse.json({ acknowledged: true, acknowledgedAtUtc: "2026-10-26T10:00:00Z" });
			}),
		);

		renderWithProviders(<AiAcknowledgementGate />);

		await waitFor(() => expect(reads).toBe(1));
		expect(screen.queryByTestId("ai-acknowledgement-dialog")).toBeNull();
	});

	it("records the acknowledgement on accept and closes the notice", async () => {
		let posts = 0;
		server.use(
			jsonRoute("get", route, { acknowledged: false, acknowledgedAtUtc: null }),
			http.post(localApiPath(route), () => {
				posts += 1;
				return HttpResponse.json({ acknowledged: true, acknowledgedAtUtc: "2026-10-26T10:00:00Z" });
			}),
		);

		renderWithProviders(<AiAcknowledgementGate />);

		fireEvent.click(await screen.findByTestId("ai-acknowledgement-accept"));

		await waitFor(() => expect(screen.queryByTestId("ai-acknowledgement-dialog")).toBeNull());
		expect(posts).toBe(1);
	});
});
