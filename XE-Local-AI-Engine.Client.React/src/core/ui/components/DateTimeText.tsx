import { useDateTimeText } from "@/core/ui/components/useDateTimeText";

interface DateTimeTextProps {
	value: string | null | undefined;
	fallback?: string;
}

export function DateTimeText({ value, fallback = "-" }: DateTimeTextProps) {
	const formattedValue = useDateTimeText(value, fallback);

	if (!value) {
		return fallback;
	}

	return (
		<time dateTime={value} suppressHydrationWarning={true}>
			{formattedValue}
		</time>
	);
}
