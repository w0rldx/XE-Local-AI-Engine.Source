# Storage housekeeping for uploaded documents

Uploaded knowledge documents are stored twice: a row in the database describes the document, and the original bytes
live as a file in the data directory. Deleting a document removes the row first and the file second.

## Why files can be left behind

If the application stops between those two steps, or the file is locked by a virus scanner, the file stays on disk
without a row that points at it. Nothing in the user interface can reach it any more, and deleting the document
again does not help, because the row is already gone.

## The startup sweep

Every time the application starts, a background task lists the stored files and checks for each one whether its row
still exists. Files without a row are deleted. The sweep runs after startup has finished, so it never delays the
first page load, and a failure is only logged.

## Interrupted re-indexing

Re-indexing a document temporarily keeps the previous file under a backup name. If the process dies at that moment,
the next start restores the backup and logs a warning naming the file, before the orphan sweep runs.
