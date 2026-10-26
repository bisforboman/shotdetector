// Bench.cs on browser-wasm under Node (README.md one folder up): main.mjs opens and reads the host's files.
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using ShotDetector.WasmBench;

[assembly: SupportedOSPlatform("browser")]

try
{
    return Bench.Run(args, new Host(NodeFiles.Open, h => (position, buffer) => NodeFiles.Read(h, position, buffer), NodeFiles.Close), "wasm", () => (long)NodeFiles.HeapBytes());
}
catch (Exception e)
{
    // The runtime reports an exception escaping Main as "Error: null" under Node.
    Console.Error.WriteLine(e);
    return 1;
}

/// <summary>main.mjs's imports: Node's file system and the WebAssembly memory's size.</summary>
internal static partial class NodeFiles
{
    [JSImport("open", "main.mjs")]
    internal static partial int Open(string path);

    [JSImport("read", "main.mjs")]
    internal static partial int Read(int handle, double position, [JSMarshalAs<JSType.MemoryView>] Span<byte> buffer);

    [JSImport("close", "main.mjs")]
    internal static partial void Close(int handle);

    [JSImport("heapBytes", "main.mjs")]
    internal static partial double HeapBytes();
}
