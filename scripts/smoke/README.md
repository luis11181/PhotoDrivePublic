# Live browser smoke

`live-media-flow.cjs` is deliberately opt-in. It talks to a running Development API and its configured Development S3 bucket, uploads one generated 1×1 GIF, verifies the authenticated browser flow, and leaves that item in Trash after confirming restore works.

It is not a normal CI test. The fast backend suite uses a temporary SQLite database and fake object store instead.

From `frontend`, install the Playwright browser once:

```bash
npm exec playwright install chromium
```

Start the Development API and frontend, then run:

```bash
MEDIA_EXPLORER_LIVE_SMOKE=1 \
MEDIA_EXPLORER_SMOKE_EMAIL=mediaexplorer.dev@example.test \
MEDIA_EXPLORER_SMOKE_PASSWORD=your-development-password \
npm run smoke:live
```

Optional `MEDIA_EXPLORER_FRONTEND_URL` and `MEDIA_EXPLORER_API_URL` override the localhost defaults. Keep credentials in the shell environment; never add them to this script, `.env.local`, logs, or source control.

The smoke validates login, protected roots, upload, the same-folder name warning, metadata, thumbnail delivery, filters, notifications, trash/restore, correlation IDs, and that JSON API responses do not expose storage identities.
