import { createContext } from "react";

/**
 * Sends the "Continue" follow-up for an answer cut off at the length limit. The chat page provides it and the message
 * list narrows it to the latest turn; undefined (the default, and every other turn) hides the Continue button.
 */
export const ChatNoticeContinueContext = createContext<(() => void) | undefined>(undefined);
