using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

namespace Blindly;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        CallLog.Start(args);
        return Environment.ExitCode = Run(args);
    }

    static int Run(string[] args)
    {
        if (args.SequenceEqual(new[] { "--test-app" })) { SelfTest.TestApp(); return 0; }
        var clock = Stopwatch.StartNew();
        bool profile = args.Contains("--profile");
        using var deadline = new System.Threading.Timer(_ => {
            Json(new { error = "Accessibility command timed out; inspect the target before retrying input.", code = "accessibility_error" });
            Environment.Exit(77);
        }, null, 25000, Timeout.Infinite);
        try
        {
            args = Defer(args.Where(a => a != "--profile").ToArray(), deadline);
            if (args.SequenceEqual(new[] { "--self-test" })) return SelfTest.Run();
            if (args.SequenceEqual(new[] { "--integration-test" })) { deadline.Change(Timeout.Infinite, Timeout.Infinite); return SelfTest.Integration(); }
            if (args.Length == 0 || args.SequenceEqual(new[] { "--help" }) || args.SequenceEqual(new[] { "help" }))
            { Console.WriteLine(Registry.Help()); return 0; }
            var command = Registry.Get(args[0]);
            if (args.Skip(1).SequenceEqual(new[] { "--help" }))
            { Json(command.Metadata); return 0; }
            var invocation = new Invocation(command, args.Skip(1).ToArray());
            command.Execute(invocation);
            return 0;
        }
        catch (CliError e) { Json(new { error = e.Message, code = e.Code }); return e.ExitCode; }
        catch (Exception e) when (e is ElementNotAvailableException or ElementNotEnabledException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        { Json(new { error = e.Message, code = "accessibility_error" }); return 77; }
        catch (Exception e) { Json(new { error = e.Message, code = "unexpected_error" }); return 1; }
        finally { if (profile) Console.Error.WriteLine(JsonSerializer.Serialize(new { elapsedMs = clock.ElapsedMilliseconds, platform = "windows" })); }
    }
    // --at UNIX_SECONDS waits until that moment and then runs the command, so a slow caller (an LLM) can
    // time an action precisely. The accessibility deadline starts after the wait; at most one hour ahead.
    static string[] Defer(string[] args, System.Threading.Timer deadline)
    {
        int i = Array.IndexOf(args, "--at");
        if (i < 0) return args;
        if (i + 1 >= args.Length || !double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var at))
            throw new CliError("--at needs a Unix time in seconds");
        var wait = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(at * 1000)) - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.FromHours(1)) throw new CliError("--at is more than one hour ahead");
        if (wait > TimeSpan.Zero)
        {
            deadline.Change(Timeout.Infinite, Timeout.Infinite);
            Thread.Sleep(wait);
            deadline.Change(25000, Timeout.Infinite);
        }
        return args.Where((_, j) => j != i && j != i + 1).ToArray();
    }
    internal static void Json(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
}
