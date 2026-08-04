// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The card posts through the interim resolve mutation (the generated one does not exist until the OpenAPI regen);
// stub its mutationFn so the wiring can be asserted without a backend. `withResponseValidation` preserves the
// mutationFn, so the spy still receives the mutate variables. The wire module's parse helper is left intact.
const resolveQuestionSpy = vi.fn().mockResolvedValue({});
vi.mock("@/features/chat/api/AskUserQuestionWire", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/features/chat/api/AskUserQuestionWire")>()),
	resolveUserQuestionMutation: () => ({ mutationFn: resolveQuestionSpy }),
}));

import type { PendingUserQuestion } from "@/features/chat/api/AskUserQuestionWire";
import { AskUserQuestionCard } from "@/features/chat/components/AskUserQuestionCard";

function renderWithProviders(ui: ReactElement) {
	const queryClient = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
	return render(
		<QueryClientProvider client={queryClient}>
			<MantineProvider>{ui}</MantineProvider>
		</QueryClientProvider>,
	);
}

const singleSelect: PendingUserQuestion = {
	requestId: "question-42",
	questions: [
		{
			header: "Auth",
			question: "Which auth method?",
			options: [
				{ label: "OAuth device flow", description: "No secret to store", recommended: true },
				{ label: "API key" },
			],
		},
	],
};

const multiSelect: PendingUserQuestion = {
	requestId: "question-77",
	questions: [
		{
			question: "Which platforms?",
			multiSelect: true,
			options: [{ label: "Linux" }, { label: "Windows" }, { label: "macOS" }],
		},
	],
};

describe("AskUserQuestionCard", () => {
	beforeEach(() => {
		resolveQuestionSpy.mockClear();
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
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("badges the recommended option without pre-selecting it", () => {
		renderWithProviders(<AskUserQuestionCard pending={singleSelect} />);

		expect(screen.getByTestId("chat-ask-user-recommended").textContent).toContain("Recommended");
		// Advisory only: the user still chooses, so nothing starts checked and Submit stays disabled.
		expect((screen.getByTestId("chat-ask-user-option-0-0") as HTMLInputElement).checked).toBe(false);
		expect((screen.getByTestId("chat-ask-user-submit") as HTMLButtonElement).disabled).toBe(true);
	});

	it("renders the question as a labelled group with the client-appended Other row", () => {
		renderWithProviders(<AskUserQuestionCard pending={singleSelect} />);

		const group = screen.getByTestId("chat-ask-user-question-0");
		expect(group.tagName).toBe("FIELDSET");
		expect(group.querySelector("legend")?.textContent).toBe("Auth");
		// The model never declares "Other" — the client always offers it.
		expect(screen.getByTestId("chat-ask-user-other-0")).toBeTruthy();
	});

	it("keeps a single-select question to one choice and submits the chosen label", async () => {
		renderWithProviders(<AskUserQuestionCard pending={singleSelect} />);

		fireEvent.click(screen.getByTestId("chat-ask-user-option-0-0"));
		fireEvent.click(screen.getByTestId("chat-ask-user-option-0-1"));

		// Radio semantics: the second pick replaces the first.
		expect((screen.getByTestId("chat-ask-user-option-0-0") as HTMLInputElement).checked).toBe(false);
		fireEvent.click(screen.getByTestId("chat-ask-user-submit"));

		await waitFor(() => expect(resolveQuestionSpy).toHaveBeenCalledTimes(1));
		expect(resolveQuestionSpy.mock.calls[0]?.[0]).toEqual({
			body: {
				requestId: "question-42",
				answers: [{ question: "Which auth method?", selected: ["API key"], other: undefined }],
			},
		});
	});

	it("accumulates several choices on a multi-select question", async () => {
		renderWithProviders(<AskUserQuestionCard pending={multiSelect} />);

		fireEvent.click(screen.getByTestId("chat-ask-user-option-0-0"));
		fireEvent.click(screen.getByTestId("chat-ask-user-option-0-2"));
		fireEvent.click(screen.getByTestId("chat-ask-user-submit"));

		await waitFor(() => expect(resolveQuestionSpy).toHaveBeenCalledTimes(1));
		expect(resolveQuestionSpy.mock.calls[0]?.[0]).toEqual({
			body: {
				requestId: "question-77",
				answers: [{ question: "Which platforms?", selected: ["Linux", "macOS"], other: undefined }],
			},
		});
	});

	it("captures the free-text Other answer and blocks submit until it is filled", async () => {
		renderWithProviders(<AskUserQuestionCard pending={singleSelect} />);

		fireEvent.click(screen.getByTestId("chat-ask-user-other-0"));
		// "Other" with an empty box is not an answer.
		expect((screen.getByTestId("chat-ask-user-submit") as HTMLButtonElement).disabled).toBe(true);

		fireEvent.change(screen.getByTestId("chat-ask-user-other-text-0"), { target: { value: "  mTLS  " } });
		fireEvent.click(screen.getByTestId("chat-ask-user-submit"));

		await waitFor(() => expect(resolveQuestionSpy).toHaveBeenCalledTimes(1));
		expect(resolveQuestionSpy.mock.calls[0]?.[0]).toEqual({
			body: {
				requestId: "question-42",
				answers: [{ question: "Which auth method?", selected: [], other: "mTLS" }],
			},
		});
	});

	it("requires every question in the call to be answered before submitting", () => {
		const twoQuestions: PendingUserQuestion = {
			requestId: "question-99",
			questions: [
				{ question: "First?", options: [{ label: "a" }, { label: "b" }] },
				{ question: "Second?", options: [{ label: "c" }, { label: "d" }] },
			],
		};
		renderWithProviders(<AskUserQuestionCard pending={twoQuestions} />);

		fireEvent.click(screen.getByTestId("chat-ask-user-option-0-0"));
		expect((screen.getByTestId("chat-ask-user-submit") as HTMLButtonElement).disabled).toBe(true);

		fireEvent.click(screen.getByTestId("chat-ask-user-option-1-1"));
		expect((screen.getByTestId("chat-ask-user-submit") as HTMLButtonElement).disabled).toBe(false);
	});

	it("collapses to a read-only summary of the choice once submitted", async () => {
		renderWithProviders(<AskUserQuestionCard pending={singleSelect} />);

		fireEvent.click(screen.getByTestId("chat-ask-user-option-0-0"));
		fireEvent.click(screen.getByTestId("chat-ask-user-submit"));

		const summary = await screen.findByTestId("chat-ask-user-summary");
		expect(summary.textContent).toContain("OAuth device flow");
		// The controls are gone: scrollback keeps the record, not a live form.
		expect(screen.queryByTestId("chat-ask-user-submit")).toBeNull();
	});

	it("re-arms the form when the post fails so the answer can be retried", async () => {
		resolveQuestionSpy.mockRejectedValueOnce(new Error("network down"));
		renderWithProviders(<AskUserQuestionCard pending={singleSelect} />);

		fireEvent.click(screen.getByTestId("chat-ask-user-option-0-0"));
		fireEvent.click(screen.getByTestId("chat-ask-user-submit"));

		await waitFor(() => expect(resolveQuestionSpy).toHaveBeenCalledTimes(1));
		// The optimistic summary rolls back and the (still-selected) form returns.
		await waitFor(() => expect(screen.getByTestId("chat-ask-user-submit")).toBeTruthy());
		expect((screen.getByTestId("chat-ask-user-option-0-0") as HTMLInputElement).checked).toBe(true);
	});
});
