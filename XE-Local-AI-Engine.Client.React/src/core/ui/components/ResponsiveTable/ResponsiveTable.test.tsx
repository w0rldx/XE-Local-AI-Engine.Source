// @vitest-environment jsdom

import { cleanup, fireEvent, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import {
	COMPACT_CONTROLS_BREAKPOINT,
	DESKTOP_NAV_BREAKPOINT,
	DESKTOP_NAV_MIN_HEIGHT,
} from "@/core/layout/constants/LayoutBreakpoints";
import { ResponsiveTable, type ResponsiveTableColumn } from "@/core/ui/components/ResponsiveTable/ResponsiveTable";
import classes from "@/core/ui/components/ResponsiveTable/ResponsiveTable.module.css";
import { shouldRenderCards } from "@/core/ui/components/ResponsiveTable/ResponsiveTableLayout";
import { installJsdomEnvironmentMocks, renderWithMantine } from "@/test/MantineTestRender";

// jsdom never lays anything out, so the container width the component measures is set here; 0 is "not measured yet".
const measured = vi.hoisted(() => ({ width: 0 }));
vi.mock("@mantine/hooks", async (importOriginal) => ({
	...(await importOriginal<typeof import("@mantine/hooks")>()),
	useElementSize: () => ({ ref: () => undefined, width: measured.width, height: 0 }),
}));

interface Job {
	readonly id: string;
	readonly name: string;
	readonly kind: string;
	readonly note: string;
}

const jobs: readonly Job[] = [
	{ id: "a", name: "Nightly cleanup", kind: "Cron", note: "internal" },
	{ id: "b", name: "Model refresh", kind: "On demand", note: "internal" },
];

const columns: readonly ResponsiveTableColumn<Job>[] = [
	{ key: "name", header: "Name", cell: (job) => job.name, primary: true },
	{ key: "kind", header: "Kind", cell: (job) => job.kind },
	{ key: "note", header: "Note", cell: (job) => job.note, hideOnCard: true },
];

function renderTable(onDelete = vi.fn()) {
	renderWithMantine(
		<ResponsiveTable
			rows={jobs}
			columns={columns}
			rowKey={(job) => job.id}
			actions={(job) => (
				<button type="button" aria-label={`Delete ${job.name}`} onClick={() => onDelete(job.id)}>
					x
				</button>
			)}
			actionsHeader="Actions"
			minWidth={600}
			rowTestId={(job) => `job-row-${job.id}`}
			data-testid="jobs"
		/>,
	);
	return onDelete;
}

function setViewportWidth(width: number): void {
	Object.defineProperty(window, "innerWidth", { configurable: true, writable: true, value: width });
}

describe("ResponsiveTable", () => {
	const originalWidth = window.innerWidth;

	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
		setViewportWidth(originalWidth);
		measured.width = 0;
	});

	it("chooses cards only below lg, from the container once measured and from the viewport before", () => {
		const tall = DESKTOP_NAV_MIN_HEIGHT;
		expect(shouldRenderCards(DESKTOP_NAV_BREAKPOINT, tall, 300, 600)).toBe(false);
		// A desktop-wide but phone-landscape-short viewport is not the desktop shell, so the container still decides.
		expect(shouldRenderCards(DESKTOP_NAV_BREAKPOINT, tall - 1, 300, 600)).toBe(true);
		expect(shouldRenderCards(DESKTOP_NAV_BREAKPOINT - 1, tall, 599, 600)).toBe(true);
		expect(shouldRenderCards(DESKTOP_NAV_BREAKPOINT - 1, tall, 600, 600)).toBe(false);
		expect(shouldRenderCards(COMPACT_CONTROLS_BREAKPOINT - 1, tall, 0, 600)).toBe(true);
		expect(shouldRenderCards(COMPACT_CONTROLS_BREAKPOINT, tall, 0, 600)).toBe(false);
	});

	it("switches to cards on a tablet when its container is narrower than the table", () => {
		setViewportWidth(768);
		measured.width = 500;
		renderTable();

		expect(screen.queryByRole("table")).toBeNull();
		expect(screen.getByTestId("job-row-a").tagName).toBe("LI");
	});

	it("keeps the table on a tablet when its container fits it", () => {
		setViewportWidth(768);
		measured.width = 700;
		renderTable();

		expect(screen.getByTestId("jobs").tagName).toBe("TABLE");
	});

	it("renders a table with every column and an actions column from sm up", () => {
		setViewportWidth(COMPACT_CONTROLS_BREAKPOINT);
		renderTable();

		const table = screen.getByTestId("jobs");
		expect(table.tagName).toBe("TABLE");
		expect(
			within(table)
				.getAllByRole("columnheader")
				.map((th) => th.textContent),
		).toEqual(["Name", "Kind", "Note", "Actions"]);
		expect(screen.getByTestId("job-row-a").tagName).toBe("TR");
	});

	it("keeps a row's actions on one line in the table", () => {
		setViewportWidth(1440);
		renderTable();

		const group = screen.getByRole("button", { name: "Delete Nightly cleanup" }).parentElement as HTMLElement;
		expect(group.style.getPropertyValue("--group-wrap")).toBe("nowrap");
	});

	// In the desktop shell a wide table scrolls; the actions header and cells are pinned to the right edge so a row's
	// buttons never sit behind the scroll, and a column flagged `wrap` breaks its text instead of widening the table.
	it("pins the actions column and wraps flagged columns in the table", () => {
		setViewportWidth(1440);
		renderWithMantine(
			<ResponsiveTable
				rows={jobs}
				columns={[...columns, { key: "url", header: "URL", cell: (job) => `https://example.test/${job.id}`, wrap: true }]}
				rowKey={(job) => job.id}
				actions={(job) => <button type="button">{`Delete ${job.name}`}</button>}
				actionsHeader="Actions"
				minWidth={600}
			/>,
		);

		const pinned = classes["actions"] ?? "missing";
		const wrapping = classes["wrap"] ?? "missing";
		expect(screen.getByRole("columnheader", { name: "Actions" }).classList.contains(pinned)).toBe(true);
		expect(screen.getByRole("button", { name: "Delete Model refresh" }).closest("td")?.classList.contains(pinned)).toBe(true);
		expect(screen.getByRole("columnheader", { name: "URL" }).classList.contains(wrapping)).toBe(true);
		expect(screen.getByText("https://example.test/a").closest("td")?.classList.contains(wrapping)).toBe(true);
		expect(screen.getByText("Nightly cleanup").closest("td")?.classList.contains(wrapping)).toBe(false);
	});

	it("renders one list item per row below sm, titled by the primary column and without hidden columns", () => {
		setViewportWidth(COMPACT_CONTROLS_BREAKPOINT - 1);
		renderTable();

		expect(screen.queryByRole("table")).toBeNull();
		const items = within(screen.getByRole("list")).getAllByRole("listitem");
		expect(items).toHaveLength(2);

		const card = screen.getByTestId("job-row-b");
		expect(card.tagName).toBe("LI");
		expect(within(card).getByText("Model refresh")).toBeTruthy();
		expect(within(card).getByText("Kind")).toBeTruthy();
		expect(within(card).getByText("On demand")).toBeTruthy();
		expect(within(card).queryByText("Note")).toBeNull();
		expect(within(card).queryByText("Name")).toBeNull();
	});

	it("keeps the actions reachable by their labels on a card", () => {
		setViewportWidth(360);
		const onDelete = renderTable();

		fireEvent.click(screen.getByRole("button", { name: "Delete Model refresh" }));

		expect(onDelete).toHaveBeenCalledWith("b");
	});

	it("renders a row's detail as a full-width row in the table and at the end of the card", () => {
		setViewportWidth(1440);
		const props = {
			rows: jobs,
			columns,
			rowKey: (job: Job) => job.id,
			minWidth: 600,
			rowDetail: (job: Job) => (job.id === "a" ? <p>detail of a</p> : null),
		};
		renderWithMantine(<ResponsiveTable {...props} />);

		const cell = screen.getByText("detail of a").closest("td") as HTMLElement;
		expect(cell.getAttribute("colspan")).toBe(String(columns.length));
		expect(screen.getAllByRole("row")).toHaveLength(1 + jobs.length + 1);

		cleanup();
		setViewportWidth(360);
		renderWithMantine(<ResponsiveTable {...props} />);

		expect(within(screen.getAllByRole("listitem")[0] as HTMLElement).getByText("detail of a")).toBeTruthy();
	});

	it("renders the empty state instead of an empty table", () => {
		setViewportWidth(1440);
		renderWithMantine(
			<ResponsiveTable
				rows={[]}
				columns={columns}
				rowKey={(job) => job.id}
				minWidth={600}
				empty={<p>No jobs</p>}
				data-testid="jobs"
			/>,
		);

		expect(screen.getByText("No jobs")).toBeTruthy();
		expect(screen.queryByTestId("jobs")).toBeNull();
	});
});
