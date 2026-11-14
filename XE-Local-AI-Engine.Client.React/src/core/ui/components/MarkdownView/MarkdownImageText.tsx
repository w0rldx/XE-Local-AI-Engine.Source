import { Text } from "@mantine/core";
import type { ComponentProps } from "react";
import { useTranslation } from "react-i18next";

/** An image's alt text (or a translated "image" fallback) and URL as plain text: no `<img>`, so nothing is fetched. */
export function MarkdownImageText({ src, alt }: Pick<ComponentProps<"img">, "src" | "alt">) {
	const { t } = useTranslation();
	const label = typeof alt === "string" && alt.length > 0 ? alt : t("components.markdownView.imageFallback", "image");
	return (
		<Text span={true} size="sm" c="dimmed">
			[{label}] {typeof src === "string" ? src : ""}
		</Text>
	);
}
