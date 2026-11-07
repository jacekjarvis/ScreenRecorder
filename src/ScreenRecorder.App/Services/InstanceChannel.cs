using System.IO;
using System.IO.Pipes;

namespace ScreenRecorder.App.Services;

/// <summary>
/// Lets a second launch (e.g. double-clicking a .jsrec file while the app is already running)
/// hand its request to the running copy instead of starting another one.
/// </summary>
internal static class InstanceChannel
{
    private const string PipeName = "JarvoScreenRecorder.Open";
    public const string ShowMessage = "show";

    public static bool Send(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client);
            writer.WriteLine(message);
            writer.Flush();
            return true;
        }
        catch { return false; }
    }

    public static void Listen(Action<string> onMessage)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    if (reader.ReadLine() is { Length: > 0 } line) onMessage(line);
                }
                catch { Thread.Sleep(500); }
            }
        }) { IsBackground = true, Name = "Instance channel" };
        thread.Start();
    }
}
