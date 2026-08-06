# Backup and restore

PhotoDrive stores immutable originals and the mutable catalog separately. A
complete recovery requires the originals bucket plus one timestamp-matched
catalog/users backup set.

## What is protected

- Original bytes and recovery manifests live in the private originals bucket.
- The catalog and users SQLite databases live in the `photodrive-data` volume.
- Verified snapshots are staged locally and uploaded to the backup bucket.
- Thumbnails and cached originals are disposable and can be rebuilt.

The default schedule creates a verified remote backup every 12 hours. It is not
continuous replication; changes since the most recent verified set are not in
that set.

## Before an upgrade

1. In Owner → Development, confirm the latest remote backup is current.
2. Record the image/source version you are running.
3. Keep the `photodrive-data` volume intact.
4. Run `./photodrive update`. It builds first, explicitly applies checked-in
   migrations, and recreates the app without deleting the volume.

## Restore drill

Perform a drill periodically on a separate disposable Docker volume:

1. Download a matching `catalog.db` and `users.db` from one timestamped backup
   set in the private backup bucket.
2. Stop the test instance before touching database files.
3. Verify the set without applying it:

   ```bash
   docker compose run --rm --no-deps photodrive \
     maintenance restore \
     --catalog /data/restore/catalog.db \
     --users /data/restore/users.db
   ```

4. Only after verification succeeds, repeat with `--apply --confirm RESTORE`.
5. Start the test instance and check Files, Photos, Albums, Shared with me,
   Favorites, Trash, Owner access, a known shared item, and original delivery.

The restore command retains rollback copies. Do not remove them until the
family validates the restored catalog and a newer remote backup is verified.

## Important boundaries

- Never copy a live SQLite file while its WAL may be active.
- Never treat the thumbnail/original cache as a backup.
- Never use the same S3 bucket for originals and database backups.
- Never run `docker compose down --volumes` as part of an update.
- Test restore, not only upload. A backup is useful only when it can be read.
