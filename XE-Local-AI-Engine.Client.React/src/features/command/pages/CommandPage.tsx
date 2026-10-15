import { ActionIcon, Box, Group, SegmentedControl, Stack, Text, Textarea, Tooltip } from "@mantine/core";
import { IconHome, IconMicrophone, IconPlayerStopFilled, IconSend } from "@tabler/icons-react";
import { Link } from "@tanstack/react-router";
import { type KeyboardEvent, useCallback, useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { createAgentConversation, invokeIntent, streamAgentReply, toIntentRefusal } from "@/features/command/api/CommandApi";
import { type CommandResult, CommandResultRow } from "@/features/command/components/CommandResultRow";
import { type DictationLanguage, useCommandDictation } from "@/features/command/hooks/useCommandDictation";

/** The answer on screen plus a short history; memory only, gone on reload. */
const MAX_RESULTS = 5;

const DICTATION_LANGUAGE_KEY = "xe.command.dictationLanguage";
const DICTATION_LANGUAGES: readonly DictationLanguage[] = ["auto", "en", "de"];

function isDictationLanguage(value: unknown): value is DictationLanguage {
	return DICTATION_LANGUAGES.includes(value as DictationLanguage);
}

/** This browser's last choice; failing that the UI language when dictation offers it, else detection. */
function initialDictationLanguage(uiLanguage: string): DictationLanguage {
	try {
		const stored = globalThis.localStorage?.getItem(DICTATION_LANGUAGE_KEY);
		if (isDictationLanguage(stored)) {
			return stored;
		}
	} catch {
		// Storage blocked: fall through to the default.
	}
	const ui = uiLanguage.slice(0, 2).toLowerCase();
	return isDictationLanguage(ui) ? ui : "auto";
}

// The command surface (ADR 0021): a typed or dictated sentence, confirmed with Enter, either starts the workflow the
// intent router resolves it to or falls through to the default agent as an ordinary conversation. Sized for the
// desktop shell's 520×220 overlay window, which loads this route directly; it works the same in a browser tab.
export function CommandPage() {
	const { t, i18n } = useTranslation();
	const [dictationLanguage, setDictationLanguage] = useState<DictationLanguage>(() =>
		initialDictationLanguage(i18n.language ?? ""),
	);
	const chooseDictationLanguage = (value: string): void => {
		if (!isDictationLanguage(value)) {
			return;
		}
		setDictationLanguage(value);
		try {
			globalThis.localStorage?.setItem(DICTATION_LANGUAGE_KEY, value);
		} catch {
			// Storage blocked: the choice lasts for this page only.
		}
	};
	const [text, setText] = useState("");
	// When the first dictated words of the current text were said; null for typed text, which is "said" on Enter.
	const [utteredAt, setUtteredAt] = useState<string | null>(null);
	const [results, setResults] = useState<CommandResult[]>([]);

	const appendDictated = useCallback((segment: string, saidAt: Date) => {
		setText((current) => (current.trim().length === 0 ? segment : `${current.trimEnd()} ${segment}`));
		setUtteredAt((current) => current ?? saidAt.toISOString());
	}, []);
	const dictation = useCommandDictation(appendDictated);
	const listening = dictation.state === "listening";

	const update = useCallback((id: string, next: (result: CommandResult) => CommandResult) => {
		setResults((current) => current.map((result) => (result.id === id ? next(result) : result)));
	}, []);

	const askAgent = useCallback(
		async (id: string, utterance: string): Promise<void> => {
			const reply = (patch: Partial<Extract<CommandResult, { kind: "reply" }>>) =>
				update(id, (result) => ({
					conversationId: null,
					text: "",
					done: false,
					failed: false,
					...(result.kind === "reply" ? result : {}),
					...patch,
					id,
					utterance,
					kind: "reply",
				}));
			reply({});
			try {
				const conversationId = await createAgentConversation(utterance);
				reply({ conversationId });
				await streamAgentReply(conversationId, utterance, (replyText) => reply({ text: replyText }));
				reply({ done: true });
			} catch {
				reply({ done: true, failed: true });
			}
		},
		[update],
	);

	const submit = useCallback(async (): Promise<void> => {
		const utterance = text.trim();
		if (utterance.length === 0) {
			return;
		}
		const id = crypto.randomUUID();
		const saidAt = utteredAt ?? new Date().toISOString();
		setText("");
		setUtteredAt(null);
		setResults((current) => [{ id, utterance, kind: "pending" } satisfies CommandResult, ...current].slice(0, MAX_RESULTS));

		try {
			const answer = await invokeIntent(utterance, id, saidAt);
			if (answer.status === "Started" && answer.run !== null) {
				const { run } = answer;
				update(id, () => ({
					id,
					utterance,
					kind: "run",
					definitionName: answer.definitionName ?? "",
					definitionId: answer.definitionId ?? run.run.definitionId,
					run,
				}));
				return;
			}
		} catch (error) {
			update(id, () => ({ id, utterance, kind: "refused", refusal: toIntentRefusal(error) }));
			return;
		}
		// NotMatched: the assistant always answers.
		await askAgent(id, utterance);
	}, [askAgent, text, update, utteredAt]);

	const handleKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
		if (event.key === "Enter" && !event.shiftKey && !event.nativeEvent.isComposing) {
			event.preventDefault();
			submit().catch(() => undefined);
		} else if (event.key === "Escape") {
			// The desktop overlay hides its own window on Escape; the page only forgets the draft.
			setText("");
			setUtteredAt(null);
		}
	};

	return (
		<Stack gap="xs" p="sm" h="100dvh" data-testid="command-page">
			<Group gap="xs" align="flex-start" wrap="nowrap">
				<Textarea
					autoFocus={true}
					autosize={true}
					minRows={1}
					maxRows={3}
					style={{ flex: 1 }}
					aria-label={t("pages.command.input.label")}
					placeholder={t("pages.command.input.placeholder")}
					value={text}
					onChange={(event) => {
						const { value } = event.currentTarget;
						setText(value);
						if (value.trim().length === 0) {
							setUtteredAt(null);
						}
					}}
					onKeyDown={handleKeyDown}
				/>
				<Tooltip label={t(listening ? "pages.command.mic.stop" : "pages.command.mic.start")}>
					<ActionIcon
						size="lg"
						variant={listening ? "filled" : "light"}
						color={listening ? "red" : undefined}
						loading={dictation.state === "starting" || dictation.state === "stopping"}
						aria-label={t(listening ? "pages.command.mic.stop" : "pages.command.mic.start")}
						aria-pressed={listening}
						onClick={() => (listening ? dictation.stop() : dictation.start(dictationLanguage)).catch(() => undefined)}
					>
						{listening ? <IconPlayerStopFilled size={18} /> : <IconMicrophone size={18} />}
					</ActionIcon>
				</Tooltip>
				<SegmentedControl
					size="xs"
					aria-label={t("pages.command.mic.language.label")}
					disabled={dictation.state !== "idle"}
					value={dictationLanguage}
					onChange={chooseDictationLanguage}
					data={DICTATION_LANGUAGES.map((value) => ({ value, label: t(`pages.command.mic.language.${value}`) }))}
				/>
				<Tooltip label={t("pages.command.send")}>
					<ActionIcon
						size="lg"
						aria-label={t("pages.command.send")}
						disabled={text.trim().length === 0}
						onClick={() => submit().catch(() => undefined)}
					>
						<IconSend size={18} />
					</ActionIcon>
				</Tooltip>
				<Tooltip label={t("pages.command.openApp")}>
					<ActionIcon
						size="lg"
						variant="subtle"
						component={Link}
						to={nodeRoutePaths.home}
						aria-label={t("pages.command.openApp")}
					>
						<IconHome size={18} />
					</ActionIcon>
				</Tooltip>
			</Group>
			{dictation.partial.length > 0 ? (
				<Text size="xs" c="dimmed" fs="italic" lineClamp={2} data-testid="command-partial">
					{dictation.partial}
				</Text>
			) : null}
			{dictation.error === null ? null : (
				<Text size="xs" c="red" role="alert">
					{t(`pages.command.mic.errors.${dictation.error.code}`, { detail: dictation.error.detail ?? "" })}
				</Text>
			)}
			<Box style={{ flex: 1, minHeight: 0, overflowY: "auto" }} aria-live="polite">
				{results.length === 0 ? (
					<Text size="sm" c="dimmed">
						{t("pages.command.empty")}
					</Text>
				) : (
					<Stack gap={4}>
						{results.map((result, index) => (
							<CommandResultRow key={result.id} result={result} compact={index > 0} />
						))}
					</Stack>
				)}
			</Box>
		</Stack>
	);
}
