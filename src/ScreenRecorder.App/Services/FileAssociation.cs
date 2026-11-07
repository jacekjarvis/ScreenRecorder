using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ScreenRecorder.App.Services;

/// <summary>
/// Registers .jsrec (Jarvo Screen Recording) for the current user only, so double-clicking a
/// recording opens it in the Video Editor. No admin rights needed; only rewritten if the app moved.
/// </summary>
internal static class FileAssociation
{
    private const string ProgId = "JarvoScreenRecorder.Recording";

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

    public static void Register()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe == null) return;
            string command = $"\"{exe}\" \"%1\"";
            using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
            using (var existing = classes.OpenSubKey($@"{ProgId}\shell\open\command"))
            {
                if (existing?.GetValue(null) as string == command) return;
            }
            using (var ext = classes.CreateSubKey(Core.Project.ProjectManifest.Extension))
                ext.SetValue(null, ProgId);
            using (var prog = classes.CreateSubKey(ProgId))
            {
                prog.SetValue(null, "Jarvo Screen Recording");
                using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{exe}\",0");
                using (var cmd = prog.CreateSubKey(@"shell\open\command")) cmd.SetValue(null, command);
            }
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
        }
        catch { /* not being able to register the file type isn't fatal */ }
    }
}
