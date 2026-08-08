import { m, useReducedMotion } from "framer-motion";

import { CHAT_ACCENT } from "@/features/chat/components/ChatVisualTokens";

interface StreamCaretProps {
	size?: number;
}

export function StreamCaret({ size = 13 }: StreamCaretProps) {
	const reduced = useReducedMotion();

	if (reduced) {
		return (
			<span aria-hidden="true" style={{ color: CHAT_ACCENT }}>
				▌
			</span>
		);
	}

	return (
		<m.span
			aria-hidden="true"
			style={{
				display: "inline-block",
				width: 6,
				height: size,
				marginLeft: 2,
				verticalAlign: "-2px",
				background: CHAT_ACCENT,
				borderRadius: 1,
			}}
			animate={{ opacity: [1, 0, 1] }}
			transition={{ duration: 0.85, repeat: Number.POSITIVE_INFINITY, ease: "easeInOut" }}
		/>
	);
}
