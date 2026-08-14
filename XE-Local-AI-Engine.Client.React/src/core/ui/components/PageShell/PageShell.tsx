import { Container, type MantineSpacing, Stack } from "@mantine/core";
import type { ReactNode } from "react";

interface PageShellProps {
	children: ReactNode;
	/** Gap between top-level page sections. Defaults to the app-wide "lg". */
	gap?: MantineSpacing;
	"data-testid"?: string;
}

// Standard page frame: every routed page renders inside this shell so all pages share the same outer
// margins (the Layout scroll container supplies horizontal padding; the shell owns the vertical rhythm).
export function PageShell({ children, gap = "lg", "data-testid": testId }: PageShellProps) {
	return (
		<Container fluid={true} py="lg" data-testid={testId}>
			<Stack gap={gap}>{children}</Stack>
		</Container>
	);
}
