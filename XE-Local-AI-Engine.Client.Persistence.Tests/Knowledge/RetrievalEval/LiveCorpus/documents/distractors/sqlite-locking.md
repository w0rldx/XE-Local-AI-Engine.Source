# SQLite locking and concurrent readers

SQLite allows many readers but only one writer at a time. How readers and writers interact depends on the journal
mode.

## Rollback journal

In the classic rollback journal mode a writer needs an exclusive lock on the whole database file. While the writer
holds it, readers must wait, and a busy timeout decides how long a reader waits before it gives up.

## Write-ahead log

In WAL mode readers do not block the writer and the writer does not block readers. Readers see a consistent snapshot
of the database as it was when their read transaction began. There is still only one writer at a time; a second
writer waits on the lock.

## Writer starvation

With a steady stream of readers in rollback journal mode, a writer can wait for a long time, because the database
is almost never free of readers. WAL mode largely avoids this problem.
