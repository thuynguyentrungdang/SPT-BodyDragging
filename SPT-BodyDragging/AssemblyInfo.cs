using System.Runtime.CompilerServices;

// BodyDragFika is a separate assembly that calls back into internal members (Plugin's config
// entries, RemoteCorpseDragFollower) - it ships alongside this DLL and is only ever loaded by
// this plugin's own reflection loader, so there's no reason to make those members public.
[assembly: InternalsVisibleTo("BodyDragFika")]
