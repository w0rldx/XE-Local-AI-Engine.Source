import { StatTile } from "@/core/ui/components/StatTile/StatTile";

export function Metric({
	label,
	value,
	valueTestId,
}: {
	readonly label: string;
	readonly value: string | number;
	/**
	 * Put on the VALUE, not the tile. The validation counts are an acceptance criterion, and a test that can only
	 * locate the tile can assert that a number rendered but not which one — so it would still pass against four
	 * zeroes, which is the exact false green this panel exists to expose.
	 */
	readonly valueTestId?: string;
}) {
	return <StatTile variant="paper" label={label} value={value} valueTestId={valueTestId} />;
}
