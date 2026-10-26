import { Card, Group, Stack, Table } from "@mantine/core";
import { useElementSize } from "@mantine/hooks";
import { type CSSProperties, Fragment, type ReactNode } from "react";

import useWindowDimensions from "@/core/layout/hooks/useWindowDimensions";
import { LabelValueRow } from "@/core/ui/components/LabelValueRow/LabelValueRow";
import classes from "@/core/ui/components/ResponsiveTable/ResponsiveTable.module.css";
import { shouldRenderCards } from "@/core/ui/components/ResponsiveTable/ResponsiveTableLayout";

export interface ResponsiveTableColumn<T> {
	key: string;
	header: ReactNode;
	cell: (row: T) => ReactNode;
	/** The column that names the row: the card's title. At most one column sets it. */
	primary?: boolean;
	/** Left out of the card (a detail the row can do without on a narrow screen). */
	hideOnCard?: boolean;
	/** Applied to the column's header and body cells in the table (e.g. `nowrap`). */
	style?: CSSProperties;
	/** Long free text (a URL, a schedule): wraps anywhere in the table instead of widening it. */
	wrap?: boolean;
}

interface ResponsiveTableProps<T> {
	rows: readonly T[];
	columns: readonly ResponsiveTableColumn<T>[];
	rowKey: (row: T) => string;
	/** The row's buttons, without a wrapper: the table keeps them on one line, the card lets them wrap. */
	actions?: (row: T) => ReactNode;
	actionsHeader?: ReactNode;
	/** Rendered instead of the table when there are no rows. */
	empty?: ReactNode;
	/** The width the table needs; a container narrower than this gets cards below `lg`, a scrolling table from `lg`. */
	minWidth: number;
	rowTestId?: (row: T) => string;
	/** Applied to the row or card (e.g. dimming a revoked record). */
	rowStyle?: (row: T) => CSSProperties | undefined;
	/** Extra content under a row: a full-width row in the table, the end of the card. Nothing when it returns null. */
	rowDetail?: (row: T) => ReactNode;
	/** Off for tables whose detail rows would break the alternating stripes. */
	striped?: boolean;
	"data-testid"?: string;
	"data-tour"?: string;
}

// A list of records with per-row actions. Where it fits, it is the plain striped table every list page used before;
// where it would scroll sideways below `lg`, each row becomes a card (primary column as the title, the rest as
// label/value rows, the actions as a wrapping button row), so no column or action hides behind a horizontal scroll.
// The switch follows the table's own container rather than the viewport, so a table beside a chart or inside a
// narrow card switches when IT runs out of room. Where the table does scroll (the desktop shell), the actions column
// is pinned to the right edge so a row's buttons stay on screen.
export function ResponsiveTable<T>({
	rows,
	columns,
	rowKey,
	actions,
	actionsHeader,
	empty,
	minWidth,
	rowTestId,
	rowStyle,
	rowDetail,
	striped = true,
	"data-testid": testId,
	"data-tour": tourId,
}: ResponsiveTableProps<T>) {
	const { width: viewportWidth, height: viewportHeight } = useWindowDimensions();
	const { ref, width: containerWidth } = useElementSize<HTMLDivElement>();

	if (rows.length === 0 && empty !== undefined) {
		// A tour step can point at an empty table, so the anchor stays on the empty state.
		return tourId === undefined ? empty : <div data-tour={tourId}>{empty}</div>;
	}

	if (shouldRenderCards(viewportWidth, viewportHeight, containerWidth, minWidth)) {
		const primary = columns.find((column) => column.primary);
		const details = columns.filter((column) => !(column.primary || column.hideOnCard));
		return (
			<div ref={ref} data-tour={tourId}>
				<Stack component="ul" gap="sm" m={0} p={0} className="list-none" data-testid={testId}>
					{rows.map((row) => (
						<Card
							key={rowKey(row)}
							component="li"
							withBorder={true}
							padding="sm"
							style={rowStyle?.(row)}
							data-testid={rowTestId?.(row)}
						>
							<Stack gap="xs">
								{primary === undefined ? null : <div>{primary.cell(row)}</div>}
								{details.map((column) => (
									<LabelValueRow key={column.key} label={column.header} justify="space-between">
										{column.cell(row)}
									</LabelValueRow>
								))}
								{actions === undefined ? null : (
									<Group gap="xs" wrap="wrap">
										{actions(row)}
									</Group>
								)}
								{rowDetail?.(row)}
							</Stack>
						</Card>
					))}
				</Stack>
			</div>
		);
	}

	return (
		<div ref={ref} data-tour={tourId}>
			<Table.ScrollContainer minWidth={minWidth}>
				<Table striped={striped} highlightOnHover={true} verticalSpacing="sm" className={classes["table"]} data-testid={testId}>
					<Table.Thead>
						<Table.Tr>
							{columns.map((column) => (
								<Table.Th key={column.key} style={column.style} className={column.wrap ? classes["wrap"] : undefined}>
									{column.header}
								</Table.Th>
							))}
							{actions === undefined ? null : <Table.Th className={classes["actions"]}>{actionsHeader}</Table.Th>}
						</Table.Tr>
					</Table.Thead>
					<Table.Tbody>
						{rows.map((row) => {
							const detail = rowDetail?.(row);
							return (
								<Fragment key={rowKey(row)}>
									<Table.Tr style={rowStyle?.(row)} data-testid={rowTestId?.(row)}>
										{columns.map((column) => (
											<Table.Td key={column.key} style={column.style} className={column.wrap ? classes["wrap"] : undefined}>
												{column.cell(row)}
											</Table.Td>
										))}
										{actions === undefined ? null : (
											<Table.Td className={classes["actions"]}>
												<Group gap="xs" wrap="nowrap">
													{actions(row)}
												</Group>
											</Table.Td>
										)}
									</Table.Tr>
									{detail ? (
										<Table.Tr>
											<Table.Td colSpan={columns.length + (actions === undefined ? 0 : 1)}>{detail}</Table.Td>
										</Table.Tr>
									) : null}
								</Fragment>
							);
						})}
					</Table.Tbody>
				</Table>
			</Table.ScrollContainer>
		</div>
	);
}
