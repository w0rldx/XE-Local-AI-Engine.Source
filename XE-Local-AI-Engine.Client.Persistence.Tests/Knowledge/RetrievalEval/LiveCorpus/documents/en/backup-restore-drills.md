# Backup restore drills

A backup that has never been restored is a hope, not a backup. This page describes how the small office runs its
restore drills.

## Cadence

Once per quarter one person, chosen by rotation, restores a randomly picked folder and one full virtual machine
from the offsite copy onto spare hardware. The drill is timed from the first command to a working login.

## Success criteria

The drill passes when the restored machine boots, the file checksums match the manifest, and the whole restore
finishes within four hours. A drill that takes longer is logged as a failure even if the data came back intact,
because four hours is what the office promised its clients.

## Keys and credentials

The encryption passphrase for the offsite copy is printed and sealed in an envelope in the fire safe. The drill
must use that envelope, not a password someone remembers, so that a lost laptop never blocks a restore.

## After the drill

Results go into the drill log with the elapsed time and any surprise. The envelope is resealed with a new signature
across the flap.
