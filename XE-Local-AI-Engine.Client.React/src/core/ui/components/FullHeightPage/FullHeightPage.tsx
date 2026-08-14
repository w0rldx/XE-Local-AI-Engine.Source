import { Box } from "@mantine/core";
import type { ReactNode } from "react";

interface FullHeightPageProps {
	children: ReactNode;
	"data-tour"?: string;
	"data-testid"?: string;
}

// Frame for pages that own their scrolling (e.g. chat): claims the full height of the Layout scroll
// container and lets an inner region scroll instead of the page. Counterpart of PageShell for the
// full-height archetype so the vertical padding stays consistent with normal pages.
export function FullHeightPage({ children, "data-tour": dataTour, "data-testid": testId }: FullHeightPageProps) {
	return (
		<Box
			py="lg"
			style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}
			data-tour={dataTour}
			data-testid={testId}
		>
			{children}
		</Box>
	);
}
