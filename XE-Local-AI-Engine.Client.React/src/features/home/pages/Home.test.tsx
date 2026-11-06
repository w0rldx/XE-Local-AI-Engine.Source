// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";
import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import type { ActiveOperation } from "@/core/operations/models/OperationModels";
import { usePendingChatConversationStore } from "@/core/ui/stores/PendingChatConversationStore";
import { Home } from "@/features/home/pages/Home";
import { renderWithProviders } from "@/test/RenderWithProviders";

// Each card's server read, answered per test. The options keep their real shape apart from the query function, so
// the cards' own select/derivation runs unchanged.
const {
	listModels,
	listConversations,
	usageSummary,
	listUpcoming,
	nodeInfo,
	updateStatus,
	releaseNotes,
	uiMode,
	schedulerCapability,
	activeDownloads,
	activeOperations,
} = vi.hoisted(() => ({
	listModels: vi.fn(),
	listConversations: vi.fn(),
	usageSummary: vi.fn(),
	listUpcoming: vi.fn(),
	nodeInfo: vi.fn(),
	updateStatus: vi.fn(),
	releaseNotes: vi.fn(),
	uiMode: { current: "advanced" as "simple" | "advanced" },
	schedulerCapability: { current: true },
	activeDownloads: { current: new Map<string, unknown>() },
	activeOperations: { current: [] as ActiveOperation[] },
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>()),
	listLocalModelsOptions: () => ({ queryKey: ["listLocalModels"], queryFn: () => listModels() }),
	listNodeChatConversationsOptions: () => ({ queryKey: ["listConversations"], queryFn: () => listConversations() }),
	getAgentUsageSummaryOptions: (options: unknown) => ({
		queryKey: ["usageSummary", options],
		queryFn: () => usageSummary(options),
	}),
	listUpcomingScheduledJobsOptions: (options: unknown) => ({
		queryKey: ["listUpcoming", options],
		queryFn: () => listUpcoming(options),
	}),
	getNodeInfoOptions: () => ({ queryKey: ["nodeInfo"], queryFn: () => nodeInfo() }),
	getAppUpdateStatusOptions: (options: unknown) => ({
		queryKey: ["appUpdateStatus", options],
		queryFn: () => updateStatus(options),
	}),
	getAppReleaseNotesOptions: () => ({ queryKey: ["releaseNotes"], queryFn: () => releaseNotes() }),
}));

vi.mock("@/capabilities/NodeCapabilities", async (importOriginal) => {
	const actual = await importOriginal<typeof import("@/capabilities/NodeCapabilities")>();
	return {
		...actual,
		nodeCapabilities: {
			...actual.nodeCapabilities,
			get scheduler() {
				return schedulerCapability.current;
			},
		},
	};
});

vi.mock("@/core/layout/hooks/useUiMode", () => ({ useUiMode: () => uiMode.current }));

// The download feed is the shared hub-backed hook, tested beside it; here it is the switch for the first-run state.
vi.mock("@/core/model-downloads/queries/useGgufDownload", () => ({
	useActiveGgufDownloads: () => activeDownloads.current,
}));
// The merged operations feed is tested beside it; here it decides whether the Activity card has rows.
vi.mock("@/core/operations/hooks/useActiveOperations", () => ({
	useActiveOperations: () => activeOperations.current,
}));

function conversation(id: string, lastSeenUtc: number, overrides: Record<string, unknown> = {}) {
	return {
		conversationId: id,
		title: `Chat ${id}`,
		createdAtUtc: lastSeenUtc,
		lastSeenUtc,
		lastMessagePreview: `Preview ${id}`,
		lastMessageStatus: "completed",
		purged: false,
		origin: "local",
		isPinned: false,
		archived: false,
		...overrides,
	};
}

function usage(runCount: number, retentionDays = 30) {
	return {
		items: [],
		byProvider: [],
		retentionDays,
		totals: {
			runCount,
			promptTokens: 1000,
			completionTokens: 500,
			reasoningTokens: null,
			totalTokens: 1500,
			estimatedCostUsd: 0,
			currency: "USD",
		},
	};
}

function upcomingJob(id: string, displayName: string, nextFireTimeUtc: number) {
	return { scheduledJobId: id, displayName, scheduleKind: "Cron", nextFireTimeUtc };
}

function status(isDesktop: boolean) {
	return {
		currentVersion: "1.2.0",
		updateAvailable: false,
		isConfigured: true,
		isDesktop,
		checkStatus: "ready",
		selectedChannel: "stable",
		defaultChannel: "stable",
		availableChannels: ["stable"],
	};
}

function note(version: string, overrides: Record<string, unknown> = {}) {
	return { version, notesMarkdown: `## Changes in ${version}`, isTruncated: false, isNewerThanRunning: false, ...overrides };
}

const notFound = () => new ApiError(404, { type: "", title: "Not Found", status: 404, detail: "" });

const chatModel = { modelName: "qwen3-8b.gguf", provider: "llamacpp", kind: "Chat" };

function renderHome() {
	return renderWithProviders(<Home />, { route: "/" });
}

beforeEach(() => {
	uiMode.current = "advanced";
	activeDownloads.current = new Map();
	activeOperations.current = [];
	listModels.mockResolvedValue({ items: [chatModel], isAvailable: true });
	listConversations.mockResolvedValue({ items: [], maxMessageSizeKb: 64, maxUploadFileSizeMb: 10 });
	usageSummary.mockResolvedValue(usage(3));
	schedulerCapability.current = true;
	listUpcoming.mockResolvedValue({ items: [] });
	nodeInfo.mockResolvedValue({ isLocalMode: true });
	updateStatus.mockResolvedValue(status(true));
	releaseNotes.mockResolvedValue({ currentVersion: "1.2.0", items: [] });
	usePendingChatConversationStore.setState({ pendingConversationId: "", newConversationRequested: false });
});

afterEach(() => {
	cleanup();
	vi.clearAllMocks();
});

describe("Home cards by UI mode", () => {
	it("adds the usage card in Advanced mode, labelled with its real scope", async () => {
		renderHome();

		expect(await screen.findByTestId("home-usage")).toBeTruthy();
		expect(await screen.findByText("Chat and integration runs, last 30 days")).toBeTruthy();
		expect((await screen.findByTestId("home-usage-runs")).textContent).toBe("3");
		expect(screen.getByTestId("home-welcome")).toBeTruthy();
		expect(screen.getByTestId("home-recent")).toBeTruthy();
		expect(await screen.findByTestId("home-upcoming")).toBeTruthy();
	});

	it("shows Welcome, Recent and What's New but no usage or upcoming card in Simple mode, and never asks for them", async () => {
		uiMode.current = "simple";
		releaseNotes.mockResolvedValue({ currentVersion: "1.2.0", items: [note("1.2.0")] });

		renderHome();

		expect(await screen.findByTestId("home-recent-empty")).toBeTruthy();
		expect(await screen.findByTestId("home-whats-new")).toBeTruthy();
		expect(screen.getByTestId("home-welcome")).toBeTruthy();
		expect(screen.queryByTestId("home-usage")).toBeNull();
		expect(screen.queryByTestId("home-upcoming")).toBeNull();
		expect(usageSummary).not.toHaveBeenCalled();
		expect(listUpcoming).not.toHaveBeenCalled();
	});

	it.each(["simple", "advanced"] as const)("shows the Activity card in %s mode while an operation runs", async (mode) => {
		uiMode.current = mode;
		activeOperations.current = [
			{
				id: "gguf-download:op-1",
				kind: "gguf-download",
				label: "Downloading qwen3-8b",
				progress: { kind: "indeterminate" },
				state: "running",
				href: { to: "/models" },
			},
		];

		renderHome();

		expect((await screen.findByTestId("home-activity")).textContent).toContain("Downloading qwen3-8b");
	});

	it("names the shorter retention window and a whole-day 30-day query range", async () => {
		usageSummary.mockResolvedValue(usage(1, 7));

		renderHome();

		expect(await screen.findByText("Chat and integration runs, last 7 days")).toBeTruthy();
		const options = usageSummary.mock.calls[0]?.[0] as { query: { fromEpochMs: number; toEpochMs: number } } | undefined;
		expect(options).toBeDefined();
		expect((options?.query.toEpochMs ?? 0) - (options?.query.fromEpochMs ?? 0)).toBe(30 * 86_400_000);
	});

	it("keeps the rest of the page when the usage read fails", async () => {
		usageSummary.mockRejectedValue(new Error("boom"));

		renderHome();

		expect(await screen.findByTestId("home-usage-error")).toBeTruthy();
		expect(screen.getByTestId("home-welcome")).toBeTruthy();
		expect(await screen.findByTestId("home-recent-empty")).toBeTruthy();
	});
});

describe("Home welcome card", () => {
	it("tells a node with no chat model to install one and shows no setup progress", async () => {
		listModels.mockResolvedValue({ items: [], isAvailable: true });

		renderHome();

		expect(await screen.findByTestId("home-no-model")).toBeTruthy();
		expect(screen.getByTestId("home-go-to-models").getAttribute("href")).toBe("/models");
		expect(screen.queryByTestId("home-setup")).toBeNull();
	});

	it("shows the first-run download with its progress while no chat model exists", async () => {
		listModels.mockResolvedValue({ items: [], isAvailable: true });
		activeDownloads.current = new Map([
			["qwen3.5-9b", { modelName: "qwen3.5-9b", phase: "Downloading", pct: undefined, completedBytes: 25, totalBytes: 100 }],
		]);

		renderHome();

		expect((await screen.findByTestId("home-setup-model")).textContent).toBe("qwen3.5-9b · 25%");
		expect(screen.queryByTestId("home-no-model")).toBeNull();
	});

	it("offers chat, not the install hint, once an external chat model is registered", async () => {
		listModels.mockResolvedValue({ items: [{ modelName: "gpt", provider: "external", kind: "Chat" }], isAvailable: true });
		activeDownloads.current = new Map([["qwen3.5-9b", { modelName: "qwen3.5-9b", phase: "Downloading", pct: 40 }]]);

		renderHome();

		expect(await screen.findByText("Start a new chat or pick up where you left off.")).toBeTruthy();
		expect(screen.queryByTestId("home-setup")).toBeNull();
		expect(screen.queryByTestId("home-go-to-models")).toBeNull();
	});

	it("hands a new-conversation request to chat", async () => {
		renderHome();

		fireEvent.click(await screen.findByTestId("home-new-chat"));

		expect(usePendingChatConversationStore.getState().newConversationRequested).toBe(true);
	});

	it("continues the most recently active conversation", async () => {
		listConversations.mockResolvedValue({
			items: [conversation("older", 1_000), conversation("newest", 3_000)],
			maxMessageSizeKb: 64,
			maxUploadFileSizeMb: 10,
		});

		renderHome();

		fireEvent.click(await screen.findByTestId("home-continue"));

		expect(usePendingChatConversationStore.getState().pendingConversationId).toBe("newest");
	});
});

describe("Home recent chats card", () => {
	it("lists conversations by last activity, not pinned first, and opens the clicked one", async () => {
		listConversations.mockResolvedValue({
			items: [
				conversation("pinned-old", 1_000, { isPinned: true }),
				conversation("b", 5_000),
				conversation("a", 4_000, { title: null, lastMessagePreview: null }),
			],
			maxMessageSizeKb: 64,
			maxUploadFileSizeMb: 10,
		});

		renderHome();

		await waitFor(() => expect(screen.getAllByTestId("home-recent-item")).toHaveLength(3));
		const rows = screen.getAllByTestId("home-recent-item");
		expect(rows.map((row) => row.querySelector(".mantine-NavLink-label")?.textContent)).toEqual([
			"Chat b",
			"Untitled chat",
			"Chat pinned-old",
		]);

		fireEvent.click(rows[1] as HTMLElement);
		expect(usePendingChatConversationStore.getState().pendingConversationId).toBe("a");
	});

	it("shows at most five conversations", async () => {
		listConversations.mockResolvedValue({
			items: Array.from({ length: 8 }, (_, index) => conversation(`c${index}`, index * 1_000)),
			maxMessageSizeKb: 64,
			maxUploadFileSizeMb: 10,
		});

		renderHome();

		await waitFor(() => expect(screen.getAllByTestId("home-recent-item")).toHaveLength(5));
	});

	it("shows the load error inside the card", async () => {
		listConversations.mockRejectedValue(new Error("boom"));

		renderHome();

		expect(await screen.findByTestId("home-recent-error")).toBeTruthy();
		expect(screen.getByTestId("home-welcome")).toBeTruthy();
	});
});

describe("Home upcoming card", () => {
	it("lists the jobs in the order the server sorted them, with their local fire time, and links to the scheduler", async () => {
		listUpcoming.mockResolvedValue({
			items: [upcomingJob("j2", "Nightly digest", 2_000_000), upcomingJob("j1", "Weekly report", 9_000_000)],
		});

		renderHome();

		await waitFor(() => expect(screen.getAllByTestId("home-upcoming-item")).toHaveLength(2));
		const rows = screen.getAllByTestId("home-upcoming-item");
		expect(rows.map((row) => row.textContent)).toEqual([
			`Nightly digest${formatTimestamp(2_000_000, { dateStyle: "medium", timeStyle: "short" })}`,
			`Weekly report${formatTimestamp(9_000_000, { dateStyle: "medium", timeStyle: "short" })}`,
		]);
		expect(screen.getByTestId("home-upcoming-link").getAttribute("href")).toBe("/scheduler");
		expect(listUpcoming).toHaveBeenCalledWith({ query: { limit: 5 } });
	});

	it("says nothing is scheduled when the list is empty", async () => {
		renderHome();

		expect((await screen.findByTestId("home-upcoming-empty")).textContent).toBe("Nothing scheduled");
	});

	it("hides itself when the scheduler switch is off and the route answers 404", async () => {
		listUpcoming.mockRejectedValue(notFound());

		renderHome();

		expect(await screen.findByTestId("home-upcoming")).toBeTruthy();
		await waitFor(() => expect(screen.queryByTestId("home-upcoming")).toBeNull());
		expect(screen.queryByTestId("home-upcoming-error")).toBeNull();
	});

	it("shows any other failure inside the card", async () => {
		listUpcoming.mockRejectedValue(new Error("boom"));

		renderHome();

		expect(await screen.findByTestId("home-upcoming-error")).toBeTruthy();
	});

	it("is absent and never asks when this build has no scheduler", async () => {
		schedulerCapability.current = false;

		renderHome();

		expect(await screen.findByTestId("home-recent-empty")).toBeTruthy();
		expect(screen.queryByTestId("home-upcoming")).toBeNull();
		expect(listUpcoming).not.toHaveBeenCalled();
	});
});

describe("Home What's New card", () => {
	it("renders the running version's notes as markdown under its version", async () => {
		releaseNotes.mockResolvedValue({ currentVersion: "1.2.0", items: [note("1.2.0")] });

		renderHome();

		expect((await screen.findByTestId("home-whats-new")).textContent).toContain("What's new in 1.2.0");
		expect(screen.getByRole("heading", { name: "Changes in 1.2.0" })).toBeTruthy();
		expect(screen.queryByTestId("home-whats-new-update")).toBeNull();
		expect(screen.queryByTestId("home-whats-new-truncated")).toBeNull();
		expect(updateStatus).toHaveBeenCalledWith({ query: { refresh: null } });
	});

	it("names the newest available update beside the running version's notes", async () => {
		releaseNotes.mockResolvedValue({
			currentVersion: "1.2.0",
			items: [note("1.2.0"), note("1.4.0", { isNewerThanRunning: true }), note("1.3.0", { isNewerThanRunning: true })],
		});

		renderHome();

		expect((await screen.findByTestId("home-whats-new-update")).textContent).toBe(
			"Update available: 1.4.0. Install it from About.",
		);
		expect(screen.getByTestId("home-whats-new").textContent).toContain("What's new in 1.2.0");
	});

	it("says when the notes were shortened", async () => {
		releaseNotes.mockResolvedValue({ currentVersion: "1.2.0", items: [note("1.2.0", { isTruncated: true })] });

		renderHome();

		expect((await screen.findByTestId("home-whats-new-truncated")).textContent).toBe("These notes are shortened.");
	});

	it("is absent with no notes yet", async () => {
		renderHome();

		await waitFor(() => expect(releaseNotes).toHaveBeenCalled());
		expect(await screen.findByTestId("home-recent-empty")).toBeTruthy();
		expect(screen.queryByTestId("home-whats-new")).toBeNull();
	});

	it("is absent off desktop and never asks for notes", async () => {
		updateStatus.mockResolvedValue(status(false));
		releaseNotes.mockResolvedValue({ currentVersion: "1.2.0", items: [note("1.2.0")] });

		renderHome();

		await waitFor(() => expect(updateStatus).toHaveBeenCalled());
		expect(await screen.findByTestId("home-recent-empty")).toBeTruthy();
		expect(screen.queryByTestId("home-whats-new")).toBeNull();
		expect(releaseNotes).not.toHaveBeenCalled();
	});

	// Off local mode both app-update routes are unmapped, so asking for the status there is a 404 on every visit.
	it("never asks for the update status when the node is not in local mode", async () => {
		nodeInfo.mockResolvedValue({ isLocalMode: false });

		renderHome();

		expect(await screen.findByTestId("home-recent-empty")).toBeTruthy();
		expect(updateStatus).not.toHaveBeenCalled();
		expect(releaseNotes).not.toHaveBeenCalled();
		expect(nodeInfo).toHaveBeenCalled();
		expect(screen.queryByTestId("home-whats-new")).toBeNull();
	});
});
