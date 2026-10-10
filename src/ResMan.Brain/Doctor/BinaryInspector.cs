using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ResMan.Brain.Doctor;

public sealed record BinaryInfo(string Path, string? Description, string? Company, string? Product, string? Version, string? Signer, string Location);

/// <summary>Reads what Windows itself knows about a running program: file description, publisher, signature, location.</summary>
public static partial class BinaryInspector
{
    public static BinaryInfo? Inspect(string processName)
    {
        var name = Core.RuleValidator.NormalizeProcessName(processName);
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                var path = ImagePath(p.Id);
                if (path is not null) return InspectFile(path);
            }
        }
        return null;
    }

    public static BinaryInfo InspectFile(string path)
    {
        var v = FileVersionInfo.GetVersionInfo(path);
        string? signer = null;
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            signer = cert.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch { /* unsigned or unreadable */ }

        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var location = path.StartsWith(win, StringComparison.OrdinalIgnoreCase) ? "part of Windows"
            : path.StartsWith(pf, StringComparison.OrdinalIgnoreCase) || path.StartsWith(pf86, StringComparison.OrdinalIgnoreCase) ? "an installed program"
            : path.StartsWith(appData, StringComparison.OrdinalIgnoreCase) ? "installed for your account only"
            : "an unusual location";

        return new BinaryInfo(path, Blank(v.FileDescription), Blank(v.CompanyName), Blank(v.ProductName), Blank(v.FileVersion), signer, location);
    }

    public static string Describe(BinaryInfo b) =>
        $"{b.Description ?? System.IO.Path.GetFileName(b.Path)}" +
        $"{(b.Company is null ? "" : $" by {b.Company}")}; " +
        $"{(b.Signer is null ? "not digitally signed" : $"signed by {b.Signer}")}; {b.Location}.";

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? ImagePath(int pid)
    {
        var h = OpenProcess(0x1000, false, pid);
        if (h == 0) return null;
        try
        {
            var buf = new char[1024];
            var size = buf.Length;
            return QueryFullProcessImageNameW(h, 0, buf, ref size) ? new string(buf, 0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint h);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(nint h, int flags, [Out] char[] buffer, ref int size);
}
