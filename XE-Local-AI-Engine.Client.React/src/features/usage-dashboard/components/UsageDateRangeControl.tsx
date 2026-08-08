import { Group, TextInput } from "@mantine/core";
import { useTranslation } from "react-i18next";

import type { UsageDateRange } from "@/features/usage-dashboard/models/UsageDashboardModel";
import { isoDateToUtcMs, utcMsToIsoDate } from "@/features/usage-dashboard/models/UsageDashboardModel";

interface UsageDateRangeControlProps {
	readonly range: UsageDateRange;
	// Retention floor + today as start-of-UTC-day unix-ms; bound the pickers so an out-of-retention day cannot be chosen.
	readonly minMs: number;
	readonly maxMs: number;
	onChange(next: UsageDateRange): void;
}

// Date-range picker built on two native date inputs (no extra date lib — @mantine/dates is not a dependency). Emits
// the raw edited range; the page clamps it to retention + ordering before it reaches the query.
export function UsageDateRangeControl({ range, minMs, maxMs, onChange }: UsageDateRangeControlProps) {
	const { t } = useTranslation();

	const handleFrom = (value: string): void => {
		const fromMs = isoDateToUtcMs(value);
		if (fromMs !== null) {
			onChange({ fromMs, toMs: range.toMs });
		}
	};

	const handleTo = (value: string): void => {
		const toMs = isoDateToUtcMs(value);
		if (toMs !== null) {
			onChange({ fromMs: range.fromMs, toMs });
		}
	};

	const minIso = utcMsToIsoDate(minMs);
	const maxIso = utcMsToIsoDate(maxMs);

	return (
		<Group gap="sm" align="flex-end" wrap="wrap" data-testid="usage-date-range">
			<TextInput
				type="date"
				label={t("pages.usage.range.from", "From")}
				value={utcMsToIsoDate(range.fromMs)}
				min={minIso}
				max={maxIso}
				onChange={(event) => handleFrom(event.currentTarget.value)}
				data-testid="usage-date-from"
			/>
			<TextInput
				type="date"
				label={t("pages.usage.range.to", "To")}
				value={utcMsToIsoDate(range.toMs)}
				min={minIso}
				max={maxIso}
				onChange={(event) => handleTo(event.currentTarget.value)}
				data-testid="usage-date-to"
			/>
		</Group>
	);
}
