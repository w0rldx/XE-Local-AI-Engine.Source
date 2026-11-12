import "katex/dist/katex.min.css";
import "@/core/chat/components/ChatMath.css";

import type { Options } from "react-markdown";
import rehypeKatex from "rehype-katex";
import remarkMath from "remark-math";

type PluggableList = NonNullable<Options["remarkPlugins"]>;

// Loaded only through useMathPlugins, so KaTeX, its CSS and fonts stay out of the entry chunk. normalizeMath already
// turned every accepted delimiter into `$$`, so single dollars stay plain text (prices). rehype-katex never throws: an
// invalid or half-streamed expression renders as its source, in the surrounding text colour to keep streaming calm.
// Model output is untrusted: `maxSize` (em) caps sizes like `\rule{5000em}{5000em}`, and `strict: "ignore"` keeps
// LaTeX-incompatible input from logging a warning on every streamed frame.
export const mathRemarkPlugins: PluggableList = [[remarkMath, { singleDollarTextMath: false }]];
export const mathRehypePlugins: PluggableList = [[rehypeKatex, { errorColor: "currentColor", maxSize: 50, strict: "ignore" }]];
