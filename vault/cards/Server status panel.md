A small sidebar view showing live server-health info, following the exact same pattern as the
existing Tasks view (a new activity-bar icon, a Sidecar action wrapping a live eval, fetched fresh
each time you switch to the view - no polling). First concrete piece: active listeners, via
ToastStunt's `listeners()` builtin (`server.cc:3210-3240`), which already returns object/port/
interface/TLS-flag for every bound listener with zero new C-side work needed. Room to grow with
other live signals later (connected player count, uptime, ...) rather than being scoped to just
listeners from the start.
