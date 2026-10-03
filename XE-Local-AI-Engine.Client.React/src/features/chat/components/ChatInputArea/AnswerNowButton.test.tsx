// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { AnswerNowButton } from "@/features/chat/components/ChatInputArea/AnswerNowButton";
import { renderWithProviders } from "@/test/RenderWithProviders";

const { answerNowSpy, toastSpies } = vi.hoisted(() => ({
	answerNowSpy: vi.fn(),
	toastSpies: { success: vi.fn(), info: vi.fn(), warn: vi.fn(), error: vi.fn() },
}));

// The generated SDK call is the network seam; the mutation options around it are the real generated ones.
vi.mock("@/core/api/generated/sdk.gen", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated/sdk.gen")>()),
	answerNowNodeChatMessage: (options: unknown) => answerNowSpy(options),
}));
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastSpies }));

describe("AnswerNowButton", () => {
	beforeEach(() => {
		answerNowSpy.mockReset();
		for (const spy of Object.values(toastSpies)) {
			spy.mockReset();
		}
		Object.defineProperty(window, "matchMedia", {
			writable: true,
			value: vi.fn().mockImplementation((query: string) => ({
				matches: false,
				media: query,
				addEventListener: vi.fn(),
				removeEventListener: vi.fn(),
				addListener: vi.fn(),
				removeListener: vi.fn(),
				dispatchEvent: vi.fn(),
			})),
		});
	});

	afterEach(() => {
		cleanup();
	});

	it("asks the server to end the reasoning of the streaming message", async () => {
		answerNowSpy.mockResolvedValue({ data: undefined });
		renderWithProviders(<AnswerNowButton messageId="assistant-7" />);

		fireEvent.click(screen.getByRole("button", { name: "Answer now" }));

		await waitFor(() => expect(answerNowSpy).toHaveBeenCalledTimes(1));
		expect(answerNowSpy.mock.calls[0]?.[0]).toMatchObject({ path: { messageId: "assistant-7" } });
		expect(toastSpies.warn).not.toHaveBeenCalled();
	});

	it("explains a refusal in a toast instead of failing the turn", async () => {
		answerNowSpy.mockRejectedValue(
			Object.assign(new Error("Request failed with status code 409"), { response: { status: 409 } }),
		);
		renderWithProviders(<AnswerNowButton messageId="assistant-7" />);

		fireEvent.click(screen.getByRole("button", { name: "Answer now" }));

		await waitFor(() => expect(toastSpies.warn).toHaveBeenCalledWith("The model can't be asked to answer early right now."));
	});
});
