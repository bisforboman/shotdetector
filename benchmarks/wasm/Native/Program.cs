// Bench.cs natively (README.md one folder up): the frames read from the file with RandomAccess.
using Microsoft.Win32.SafeHandles;
using ShotDetector.WasmBench;

var handles = new Dictionary<int, SafeFileHandle>();
var host = new Host(
    path =>
    {
        handles.Add(handles.Count, File.OpenHandle(path));
        return handles.Count - 1;
    },
    handle => (position, buffer) => RandomAccess.Read(handles[handle], buffer, position),
    handle => handles[handle].Dispose());
return Bench.Run(args, host, "native", () => null);
