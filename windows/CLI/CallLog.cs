using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Blindly;

// Opt-in call log: when BLINDLY4_LOG names a file, every invocation appends one JSON line with its
// start time, duration, BLINDLY4_LOG_TAG, arguments, exit code and stdout. Writers serialize on the file.
internal static class CallLog
{
    static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static void Start(string[] args)
    {
        var path = Environment.GetEnvironmentVariable("BLINDLY4_LOG");
        if (string.IsNullOrEmpty(path)) return;
        var started = DateTimeOffset.Now;
        var clock = Stopwatch.StartNew();
        var output = new StringWriter();
        Console.SetOut(new Tee(Console.Out, output));
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Console.Out.Flush();
                Append(path, JsonSerializer.Serialize(new
                {
                    ts = started.ToString("o"),
                    ms = clock.ElapsedMilliseconds,
                    tag = Environment.GetEnvironmentVariable("BLINDLY4_LOG_TAG"),
                    args,
                    exit = Environment.ExitCode,
                    output = output.ToString(),
                }, Options) + "\n");
            }
            catch (Exception e) { Console.Error.WriteLine($"blindly4: call log not written: {e.Message}"); }
        };
    }

    static void Append(string path, string line)
    {
        var bytes = new UTF8Encoding(false).GetBytes(line);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                file.Write(bytes);
                return;
            }
            catch (IOException) when (attempt < 500) { Thread.Sleep(10); }
        }
    }

    sealed class Tee(TextWriter console, TextWriter copy) : TextWriter
    {
        public override Encoding Encoding => console.Encoding;
        public override void Write(char value) { console.Write(value); copy.Write(value); }
        public override void Write(char[] buffer, int index, int count) { console.Write(buffer, index, count); copy.Write(buffer, index, count); }
        public override void Write(string? value) { console.Write(value); copy.Write(value); }
        public override void Flush() => console.Flush();
    }
}
