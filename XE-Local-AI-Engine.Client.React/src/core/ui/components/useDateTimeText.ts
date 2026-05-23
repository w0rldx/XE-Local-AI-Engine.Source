import { useEffect, useState } from "react";

export function useDateTimeText(value: string | null | undefined, fallback = "-") {
	const [formattedValue, setFormattedValue] = useState<string | null>(null);

	useEffect(() => {
		if (!value) {
			setFormattedValue(null);
			return;
		}

		setFormattedValue(new Date(value).toLocaleString());
	}, [value]);

	return value ? (formattedValue ?? fallback) : fallback;
}
