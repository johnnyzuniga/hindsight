// PhotoSync: a stand-in background "photo sync" tool for the ResMan demo. It quietly eats memory.
//   PhotoSync [targetMemoryPercent] [seconds]   grows until system memory use reaches the target, then holds it

using System.Runtime.InteropServices;

var target = double.TryParse(args.ElementAtOrDefault(0), out var t) ? Math.Clamp(t, 50, 96) : 93;
var seconds = int.TryParse(args.ElementAtOrDefault(1), out var s) ? s : 90;
var chunks = new List<byte[]>();
var until = DateTime.UtcNow.AddSeconds(seconds);
Console.WriteLine($"PhotoSync: indexing photos (demo memory hog to {target}% for {seconds}s)");

while (DateTime.UtcNow < until)
{
    var (load, availMb) = Memory();
    // Grow gradually (so the black box shows the climb), but never leave less than ~400 MB free.
    if (load < target && availMb > 400)
    {
        var chunk = new byte[64 << 20];
        for (var i = 0; i < chunk.Length; i += 4096) chunk[i] = 1; // touch every page so it's really used
        chunks.Add(chunk);
        Thread.Sleep(250);
    }
    else Thread.Sleep(500);
}
GC.KeepAlive(chunks);

static (uint load, ulong availMb) Memory()
{
    var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
    GlobalMemoryStatusEx(ref m);
    return (m.dwMemoryLoad, m.ullAvailPhys >> 20);
}

[DllImport("kernel32.dll")]
static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

[StructLayout(LayoutKind.Sequential)]
struct MEMORYSTATUSEX
{
    public uint dwLength, dwMemoryLoad;
    public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
}
