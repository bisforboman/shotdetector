// Starts the .NET runtime under Node with the command line's arguments (Program.cs, Bench.cs), and gives it the host's
// files in slices (Emscripten's file system doesn't see them) and the WebAssembly memory's size.
import { dotnet } from './_framework/dotnet.js';
import { closeSync, openSync, readSync } from 'node:fs';

const runtime = await dotnet.withApplicationArguments(...process.argv.slice(2)).create();
// A plain Uint8Array: the runtime's Span.set takes nothing else (not even a Node Buffer).
let buffer = new Uint8Array(0);
runtime.setModuleImports('main.mjs', {
  open: path => openSync(path, 'r'),
  close: fd => closeSync(fd),
  // Reads view.length bytes of the file at position into view (a .NET Span, valid only during the call): how many it
  // read, fewer at the file's end.
  read: (fd, position, view) => {
    if (buffer.length < view.length) buffer = new Uint8Array(view.length);
    const n = readSync(fd, buffer, 0, view.length, position);
    view.set(buffer.subarray(0, n));
    return n;
  },
  // The WebAssembly memory's size in bytes; it only grows, so at the end it is the run's peak.
  heapBytes: () => runtime.localHeapViewU8().length,
});
await runtime.runMain();
