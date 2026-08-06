# PhotoDrive public demo frontend

This folder is a self-contained, backend-free product landing page for a personal family project. It introduces PhotoDrive's Photos, Files, private-sharing, and recovery features and shows a synthetic-data screenshot of the running application. It does not import code, configuration, environment variables, or assets from `../frontend`.

```bash
npm ci
npm run lint
npm run build
```

The output is `out/`. The repository Cloudflare Pages workflow sparse-checks out and deploys only this folder.

The self-hosting link targets this public PhotoDrive repository.
