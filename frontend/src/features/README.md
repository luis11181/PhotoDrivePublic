# Frontend feature boundaries

Keep route files thin. A route should choose a feature app and provide session or
route-level inputs; it should not own catalog behavior.

## Where code belongs

- `catalog/` — behavior shared by any page that presents `MediaItem` objects.
  Selection, range selection, keyboard shortcuts, context-menu positioning,
  reusable item actions, and stable item links belong here.
- `library/` — the Files, Photos, Favorites, Shared, and Trash workspace. Query
  composition and workflows that coordinate those views belong in
  `library-app.tsx`; view-only filters and result renderers belong in
  `library/components/`.
- `albums/` — album listing, album membership, album sharing, and album editing.
  It may use `catalog/`, but album-only behavior stays here.
- `uploads/` — durable upload queue state and its global UI. Library pages may
  enqueue work but should not implement queue persistence.
- `imports/`, `users/`, and `developer/` — isolated administration features.

## Component placement rule

Start a component in the feature that owns its workflow. Move it to `catalog/`
only after it is used by multiple catalog surfaces and its API can be expressed
without Files-, Photos-, or Albums-specific concepts. Generic visual primitives
that do not know about `MediaItem` belong under the app-wide component layer,
not in `catalog/`.

## App-shell rule

Feature app files coordinate queries, mutations, routing, and dialogs. They
should delegate these concerns instead of accumulating more JSX:

1. Reusable interaction state goes in a hook.
2. A view-specific toolbar or result layout goes in a named component.
3. Mutation workflows with their own invalidation, progress, or recovery rules
   go in a feature-owned domain hook (for example tags or Trash). Move them to a
   shared feature only when another surface needs the exact same workflow.
4. Modal composition belongs in a feature dialog stack; the app shell owns only
   the state that decides which workflow is active.

This keeps abstractions based on proven reuse while making ownership visible
from the import path.
