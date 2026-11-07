using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace SimpleMusic;

// Keeps SimpleMusic to one window per user: a second launch (for example
// double-clicking a sheet while the app is open) hands its file to the
// running window and quits.
static class SingleInstance
{
    static readonly string UserId = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
    static readonly string PipeName = $"SimpleMusic.Open.{UserId}";
    static Mutex? mutex;   // held for the life of the first instance

    public static bool TryBecomeFirst()
    {
        mutex = new Mutex(true, $@"Local\SimpleMusic.Instance.{UserId}", out bool createdNew);
        return createdNew;
    }

    // Sends a file path (or "" just to bring the window forward). False when
    // the running instance doesn't answer; then this launch carries on by itself.
    public static bool SendToRunning(string path)
    {
        try
        {
            AllowSetForegroundWindow(-1); // let the running window come to the front
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(3000);
            using var writer = new StreamWriter(pipe);
            writer.WriteLine(path);
            writer.Flush();
            return true;
        }
        catch (Exception) { return false; }
    }

    // Waits for later launches in the background and passes on what they send.
    public static void Listen(Action<string> onOpen)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    pipe.WaitForConnection();
                    using var reader = new StreamReader(pipe);
                    if (reader.ReadLine() is { } path) onOpen(path);
                }
                catch (IOException) { /* a launch that gave up; wait for the next */ }
            }
        })
        { IsBackground = true, Name = "SimpleMusic open requests" };
        thread.Start();
    }

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
}
