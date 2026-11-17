using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IGYO
{
    // One IGYO per data folder. A copy that is started while another one is open on the same data (Windows starts one
    // for each link another program opens once IGYO is the default browser, and the shortcut starts one too) hands
    // what it was started with to the open one and ends: the link opens as a tab of the window that is already there,
    // instead of in a second IGYO working on the same files. The open copy holds a named lock while it runs and
    // listens on a pipe that only the same Windows user can open. Both names come from the data folder, so a test copy
    // with a folder of its own never reaches the real one (or the other way round).
    static class OneCopy
    {
        const string Prefix = "IGYO.Open.";
        const int MaxRequestBytes = 256 * 1024;

        // What a copy was started with, as it is handed over.
        public sealed class Request
        {
            public List<string> Links = new List<string>();
            public string Profile = "";                            // "--profile=": the profile it was started for
            public bool Restore;                                   // "--restore-tabs": it takes the place of a copy that restarted itself
            public List<string> AlsoProfiles = new List<string>(); // "--also-profiles=": more profiles whose windows were open then
        }

        static Mutex held;           // owned by this copy's first thread for as long as it runs
        static string pipeName;
        static readonly object gate = new object();
        static BrowserApp app;       // null until this copy's first window is up
        static SynchronizationContext ui;
        static bool closing;
        static readonly List<KeyValuePair<Request, TaskCompletionSource<bool>>> early = new List<KeyValuePair<Request, TaskCompletionSource<bool>>>();

        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

        // True when this copy is to run: it is now the one for its data folder, and serves the copies started after
        // it. False when an open copy took what this one was started with (or could not be reached, which the person
        // is told): this copy then ends.
        public static bool Claim(Request request)
        {
            string key = BridgePipe.KeyFor(BridgePipe.DataDirPath());
            pipeName = Prefix + key;
            var giveUp = DateTime.UtcNow.AddSeconds(45);
            while (true)
            {
                Mutex candidate;
                bool mine;
                try
                {
                    candidate = new Mutex(false, @"Local\" + Prefix + key);
                    try { mine = candidate.WaitOne(0); }
                    catch (AbandonedMutexException) { mine = true; }   // the copy that held it ended without letting go
                }
                catch (Exception ex)
                {
                    // The lock can't be had at all (its name is taken by something else): IGYO runs as it did before
                    // there was one, by itself, and serves no other copy.
                    ErrorLog.Write(ex);
                    return true;
                }
                if (mine)
                {
                    held = candidate;
                    Task.Run(Listen);
                    return true;
                }
                candidate.Dispose();
                switch (Knock(request))
                {
                    case Answer.Taken: return false;
                    case Answer.Silent: Tell(); return false;
                }
                // It was closing, or went away: the lock is free in a moment, or already.
                if (DateTime.UtcNow > giveUp) { Tell(); return false; }
                Thread.Sleep(100);
            }
        }

        enum Answer { Taken, Closing, Gone, Silent }

        // Hands the request to the open copy, from a copy that is starting (on its first thread, before any window).
        static Answer Knock(Request request)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
                {
                    // The open copy makes its door a moment after it takes the lock, and a new one for each caller.
                    var until = DateTime.UtcNow.AddSeconds(5);
                    while (true)
                    {
                        try { pipe.Connect(200); break; }
                        catch (Exception ex) when (ex is TimeoutException || ex is IOException)
                        {
                            if (DateTime.UtcNow > until) return Answer.Gone;
                            Thread.Sleep(50);
                        }
                    }
                    int server = ProcessFacts.ServerOf(pipe);
                    // The open copy may then bring its window to the front: this copy was started by what the person
                    // just did (a link clicked in another program), and Windows lets such a program pass that on.
                    if (server > 0) AllowSetForegroundWindow(server);
                    var line = Encoding.UTF8.GetBytes(Json.Write(new Dictionary<string, object>
                    {
                        ["links"] = request.Links, ["profile"] = request.Profile ?? "", ["restore"] = request.Restore, ["alsoProfiles"] = request.AlsoProfiles,
                    }) + "\n");
                    pipe.Write(line, 0, line.Length);
                    pipe.Flush();

                    // A copy that has only just started answers once its first window is up. (Read on a thread of the
                    // pool, so that waiting for it here can never hold up its own reading.)
                    var reading = Task.Run(() => new LineReader(pipe, 1024).ReadAsync());
                    var silentAfter = DateTime.UtcNow.AddSeconds(15);
                    while (!reading.Wait(200))
                    {
                        if (server > 0 && !ProcessFacts.IsRunning(server)) return Answer.Gone;
                        if (DateTime.UtcNow > silentAfter) return Answer.Silent;
                    }
                    switch (reading.Result)
                    {
                        case "ok": return Answer.Taken;
                        case "closing":
                            WaitToEnd(server, 20000);
                            return Answer.Closing;
                        default: return Answer.Gone;   // the pipe ended without an answer: that copy went away
                    }
                }
            }
            catch (Exception)
            {
                return Answer.Gone;
            }
        }

        static void WaitToEnd(int processId, int ms)
        {
            if (processId <= 0) return;
            try { using (var p = System.Diagnostics.Process.GetProcessById(processId)) p.WaitForExit(ms); } catch { }
        }

        static void Tell()
        {
            try
            {
                L.Use(L.Auto);   // the language Windows is shown in (the settings file isn't read by a copy that ends at once)
                MessageBox.Show(L.T("{0} is open but isn't answering. Close {0} and try again.", InstallInfo.AppName), InstallInfo.AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { }
        }

        // ----- in the open copy -----

        static NamedPipeServerStream NewDoor() =>
            new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 4096, 4096, BridgePipe.OnlyThisUser());

        // Waits for callers for as long as this copy runs (on threads of the pool: a copy that is ending still says so).
        static async Task Listen()
        {
            NamedPipeServerStream next = null;
            int failures = 0;
            while (true)
            {
                NamedPipeServerStream pipe;
                try { pipe = next ?? NewDoor(); }
                catch (Exception ex)
                {
                    if (failures++ == 0) ErrorLog.Write(ex);
                    await Task.Delay(Math.Min(30000, 500 * failures)).ConfigureAwait(false);
                    continue;
                }
                next = null;
                try { await pipe.WaitForConnectionAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    pipe.Dispose();
                    if (failures++ == 0) ErrorLog.Write(ex);
                    await Task.Delay(Math.Min(30000, 500 * failures)).ConfigureAwait(false);
                    continue;
                }
                failures = 0;
                try { next = NewDoor(); } catch { }   // so that the next caller finds the door at once
                _ = Hear(pipe);
            }
        }

        static async Task Hear(NamedPipeServerStream pipe)
        {
            using (pipe)
            {
                try
                {
                    var reading = new LineReader(pipe, MaxRequestBytes).ReadAsync();
                    if (await Task.WhenAny(reading, Task.Delay(5000)).ConfigureAwait(false) != reading) return;   // a caller that says nothing
                    var request = Parse(await reading.ConfigureAwait(false));
                    if (request == null) return;
                    bool taken = await Hand(request).ConfigureAwait(false);
                    var reply = Encoding.UTF8.GetBytes((taken ? "ok" : "closing") + "\n");
                    await pipe.WriteAsync(reply, 0, reply.Length).ConfigureAwait(false);
                    await pipe.FlushAsync().ConfigureAwait(false);
                    pipe.WaitForPipeDrain();
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidDataException || ex is OperationCanceledException) { }
                catch (Exception ex) { ErrorLog.Write(ex); }
            }
        }

        internal static Request Parse(string line)
        {
            try
            {
                var o = Json.Obj(Json.Parse(line ?? ""));
                if (o == null) return null;
                List<string> Texts(string key) => Json.Arr(Json.Get(o, key)).OfType<string>().Take(64).ToList();
                return new Request { Links = Texts("links"), Profile = Json.Str(o, "profile") ?? "", Restore = Json.Get(o, "restore") is bool restore && restore, AlsoProfiles = Texts("alsoProfiles") };
            }
            catch { return null; }
        }

        // True once the request was carried out, false when this copy is closing (the caller then waits for it to end).
        static Task<bool> Hand(Request request)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                if (closing) done.TrySetResult(false);
                else if (app == null) early.Add(new KeyValuePair<Request, TaskCompletionSource<bool>>(request, done));   // carried out once the first window is up
                else Post(request, done);
            }
            return done.Task;
        }

        static void Post(Request request, TaskCompletionSource<bool> done)
        {
            var owner = app;
            ui.Post(_ =>
            {
                bool taken;
                lock (gate) taken = !closing;
                if (taken)
                {
                    // (If it fails, the caller is still told it was taken: a second IGYO on the same data would be worse.)
                    try { taken = owner.TakeOver(request); }
                    catch (Exception ex) { ErrorLog.Write(ex); }
                }
                done.TrySetResult(taken);
            }, null);
        }

        // Called on the first thread once the first window is up: requests are carried out from now on.
        public static void Serve(BrowserApp owner)
        {
            lock (gate)
            {
                if (held == null || app != null) return;   // (a copy that runs without the lock serves nobody)
                app = owner;
                ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
                foreach (var waiting in early) Post(waiting.Key, waiting.Value);
                early.Clear();
            }
        }

        // This copy is ending: whoever calls from now on is told so, waits for it to end, and starts by itself.
        public static void Closing()
        {
            lock (gate)
            {
                closing = true;
                foreach (var waiting in early) waiting.Value.TrySetResult(false);
                early.Clear();
            }
        }
    }
}
