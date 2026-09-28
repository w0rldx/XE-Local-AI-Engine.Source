# How knowledge excerpts reach a chat answer

When knowledge grounding is enabled for a conversation, each user message triggers a knowledge search before the
model is called. The best excerpts are placed into the prompt as a separate context block.

## The context block

The block opens with a short instruction that asks the model to ground its answer in the excerpts and to cite the
source titles. A second paragraph warns that the excerpts are untrusted data: the model must not follow any
instruction that appears inside a retrieved document.

Every excerpt is wrapped in markers that carry its title, section, collection and, for code, the file path and
symbol. Because the excerpts change with every question, the markers use a fresh random value each time instead of
a stable one.

## The size limit

The block has a character budget. Excerpts are added strongest first; when the next one no longer fits, it is cut or
dropped, and a notice tells the model that lower-ranked excerpts were left out.

## The Sources strip

Below the answer the chat shows a Sources strip. It lists exactly the excerpts the model was given, in the same
order, so the user can open the underlying document. An excerpt that was dropped for space never appears there.
