// @vitest-environment jsdom

import { Table, useMantineTheme } from "@mantine/core";
import { cleanup, render, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { ThemeProvider } from "@/core/theme/provider/ThemeProvider";
import { installJsdomEnvironmentMocks } from "@/test/MantineTestRender";

describe("ThemeProvider table scroll affordance", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	// Mantine's ScrollArea defaults to type="hover": the scrollbar only appears once a pointer enters the region, so a
	// touch device gets no affordance at all and an overflowing table just looks clipped. "auto" shows the bar whenever
	// the content overflows, on any input device, and shows nothing when it fits. Asserted at the theme level because
	// the fix is app-wide (35+ tables) rather than per call site.
	it("defaults every Table.ScrollContainer to an overflow-driven scrollbar", () => {
		// Reads the theme the provider actually hands to MantineProvider, which is what every Table.ScrollContainer in
		// the app resolves against (useProps merges component defaults -> theme defaults -> call-site props).
		const { result } = renderHook(() => useMantineTheme(), { wrapper: ThemeProvider });
		const defaultProps = result.current.components["TableScrollContainer"]?.defaultProps as
			| { scrollAreaProps?: { type?: string } }
			| undefined;

		expect(defaultProps?.scrollAreaProps?.type).toBe("auto");
	});

	// The prop above is only worth anything if the container still renders a ScrollArea (type="native" would ignore
	// scrollAreaProps entirely) and actually mounts a horizontal scrollbar for the overflow direction tables clip in.
	it("renders a horizontal scrollbar element for a table wrapped in a scroll container", () => {
		const { container } = render(
			<ThemeProvider>
				<Table.ScrollContainer minWidth={500}>
					<Table>
						<Table.Tbody>
							<Table.Tr>
								<Table.Td>cell</Table.Td>
							</Table.Tr>
						</Table.Tbody>
					</Table>
				</Table.ScrollContainer>
			</ThemeProvider>,
		);

		const horizontalScrollbar = container.querySelector('.mantine-ScrollArea-scrollbar[data-orientation="horizontal"]');
		expect(horizontalScrollbar).not.toBeNull();
	});
});
