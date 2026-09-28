# Scheduler operations guide

The scheduler runs recurring background jobs such as model recommendation refreshes. This guide covers the two
things operators ask about most: how long run history is kept, and what happens when a job is started by hand.

## Run history

Every job run is recorded with its events so the jobs page can show what happened. A background service deletes run
records older than the configured number of retention days, together with their events. It wakes up on its own
interval rather than after every run, so an old record can survive a little past the limit. When the scheduler is
disabled, the cleanup does not run either, because no new history is produced.

## Starting a job by hand

The "Run now" button fires a job immediately. The run is recorded as a manual run instead of a scheduled one, which
is how the history view tells the two apart.

A manual run of a model recommendation refresh may override a few settings for that single run: the use case, how
many recommendations to return (between 1 and 50), the quantization to estimate against, and the context-window
target (at least 256 tokens). No other value can be overridden, and the stored job definition never changes.
