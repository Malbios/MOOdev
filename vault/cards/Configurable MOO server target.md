Let a user pick which MOO server (host+port) the IDE talks to, instead of it being fixed at
Sidecar startup via `appsettings.json`'s `Moo:Host`/`Moo:Port`.

Confirmed viable in principle: only the Sidecar ever opens a raw connection to the actual MOO
server (`Program.fs:504-520`) - the browser Client and the LanguageServer both only ever talk to
Sidecar itself, never to MOO directly. So this is fundamentally about making Sidecar's own target
dynamic, not something that ripples through every component.

The real complexity isn't the socket, it's everything that currently travels with it as fixed
config alongside `Moo:Host`/`Moo:Port`:
- `Moo:TreeDir` - the git content tree, conceptually tied to whichever specific world the server
  represents.
- The LanguageServer's own static analysis graph, loaded once from that same tree at its own
  process startup (`Metadata.Loader.load`) - not reloadable at runtime today.
- The target server needs this project's own bootstrap objects already set up (dedicated LSP
  service character + listener, per moo-dev's CLAUDE.md) - so it's "any server prepared for this
  tooling," not literally any MOO server on the network.

Recommendation: put the picker in the login flow (a natural "choose a target" moment already
exists there), and start with "restart the local stack pointed at a different host+port + tree"
rather than true hot-swapping mid-session - much smaller lift, covers the realistic use case
(pointing your one IDE at a different world) without touching the LSP's graph-reload assumptions.
