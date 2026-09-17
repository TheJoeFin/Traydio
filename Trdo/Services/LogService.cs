using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Windows.Storage;

namespace Trdo.Services;

/// <summary>
/// Lightweight, thread-safe file logger that persists diagnostics so the user
/// can retrieve them (unlike <see cref="Debug.WriteLine"/>, which only reaches an
/// attached debugger and is stripped from Release builds).
///
/// Design goals:
/// <list type="bullet">
///   <item>Never block or slow a caller. Playback events fire from hot threads
///   (UI dispatcher, WinRT media callbacks, the NAudio capture thread), so
///   <see cref="Info"/>/<see cref="Warn"/>/<see cref="Error"/> only format a line
///   and drop it into a bounded in-memory queue — all disk I/O happens on a single
///   background writer thread. If the queue is full (a burst faster than the disk),
///   lines are dropped rather than blocking the caller.</item>
///   <item>Bounded disk usage. The active file rolls to a single previous
///   generation at <see cref="MaxLogBytes"/>, so total on-disk size never exceeds
///   ~2× that. Size is tracked in memory to avoid a stat syscall per line.</item>
///   <item>Never throw into playback — every path is wrapped in try/catch.</item>
/// </list>
///
/// Writes to <c>ApplicationData.Current.LocalFolder\logs\traydio.log</c>. Follows
/// the app's convention of static services (see <see cref="SettingsService"/>).
/// </summary>
public static class LogService
{
    private const string LogFolderName = "logs";
    private const string LogFileName = "traydio.log";
    private const string PreviousLogFileName = "traydio.prev.log";

    // Roll the active file at ~1 MB; with one previous generation kept, total
    // on-disk usage is bounded to ~2 MB.
    private const long MaxLogBytes = 1 * 1024 * 1024;

    // Upper bound on queued-but-not-yet-written lines. Sized so a burst is
    // absorbed without unbounded memory; excess is dropped (and counted).
    private const int MaxQueuedLines = 8192;

    // Upper bound on how long a reader will wait for the writer thread to drain. Generous
    // enough to cover a below-normal-priority thread on a loaded machine, short enough that
    // a wedged writer can't freeze the UI on the diagnostics button.
    private static readonly TimeSpan DefaultFlushTimeout = TimeSpan.FromMilliseconds(750);

    // Serializes access to the StreamWriter and size counter between the writer
    // thread and readers (ReadRecentText). Producers never take this lock.
    private static readonly object Gate = new();

    private static readonly BlockingCollection<string> Queue =
        new(new ConcurrentQueue<string>(), MaxQueuedLines);

    private static volatile bool _started;
    private static volatile bool _disabled;
    private static string? _logFolderPath;
    private static string? _logFilePath;
    private static string? _previousLogFilePath;

    private static StreamWriter? _writer;
    private static long _currentSize;
    private static int _droppedSinceLastNote;
    private static Thread? _worker;

    // Progress counters for the drain barrier. _accepted advances when a line enters the
    // queue, _consumed when the writer thread has taken it back out (whether or not the
    // write itself succeeded). A reader waits for _consumed to catch up to the _accepted
    // value it observed, which is the only way to know the queue holds nothing older.
    private static long _accepted;
    private static long _consumed;

    /// <summary>Full path to the folder that contains the log files.</summary>
    public static string LogFolderPath
    {
        get
        {
            EnsureStarted();
            return _logFolderPath ?? string.Empty;
        }
    }

    /// <summary>Full path to the current log file.</summary>
    public static string LogFilePath
    {
        get
        {
            EnsureStarted();
            return _logFilePath ?? string.Empty;
        }
    }

    public static void Info(string component, string message) => Enqueue("INFO", component, message);

    public static void Warn(string component, string message) => Enqueue("WARN", component, message);

    public static void Error(string component, string message, Exception? ex = null)
    {
        string text = ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}";
        Enqueue("ERROR", component, text);
    }

    /// <summary>
    /// Returns the tail of the log (current file, plus the previous generation when
    /// the current one is short) capped at <paramref name="maxChars"/> characters,
    /// for the "Copy diagnostics" feature. Prepended with a fresh session header.
    /// </summary>
    public static string ReadRecentText(int maxChars = 60_000)
    {
        EnsureStarted();

        // Drain the queue and flush to disk before reading, so the copy includes the lines
        // logged moments ago - which, when the user is copying diagnostics, are the whole
        // point. Must happen outside Gate: the writer thread needs it to make progress.
        FlushToDisk();

        var sb = new StringBuilder();
        sb.AppendLine(BuildHeader());
        sb.AppendLine();

        lock (Gate)
        {
            try
            {
                string current = ReadFileSafe(_logFilePath);
                string previous = string.Empty;

                // Include the previous generation only if the current file is small,
                // so a copy captures context around a recent rollover.
                if (current.Length < maxChars / 2)
                {
                    previous = ReadFileSafe(_previousLogFilePath);
                }

                string combined = previous.Length > 0 ? previous + current : current;
                if (combined.Length > maxChars)
                {
                    combined = combined[^maxChars..];
                }

                sb.Append(combined);
            }
            catch
            {
                // Best effort — return whatever header we have.
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Deletes the current and previous log files and starts a fresh one, so a user who has
    /// been reproducing an issue for a while can free the disk space without restarting the app.
    /// </summary>
    public static void ClearLogs()
    {
        EnsureStarted();

        if (_disabled)
        {
            return;
        }

        // Drain the queue first so nothing logged moments ago reappears in the new file.
        FlushToDisk();

        lock (Gate)
        {
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch
            {
                // Best effort.
            }
            finally
            {
                _writer = null;
            }

            try
            {
                if (_logFilePath is not null && File.Exists(_logFilePath))
                {
                    File.Delete(_logFilePath);
                }

                if (_previousLogFilePath is not null && File.Exists(_previousLogFilePath))
                {
                    File.Delete(_previousLogFilePath);
                }
            }
            catch
            {
                // Best effort — a file the OS won't let go of yet will just keep growing
                // until it can be deleted on a later attempt.
            }

            _currentSize = 0;
        }

        // The writer thread reopens the file (via EnsureWriter) when it picks this line up.
        TryQueue(BuildHeader());
    }

    /// <summary>
    /// Reduces a stream URL to <c>scheme://host</c> (dropping path, query and any
    /// embedded credentials/tokens) so logs never leak sensitive URL contents.
    /// </summary>
    public static string Redact(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "(none)";
        }

        try
        {
            var uri = new Uri(url);

            // Keep the port and path: two stations frequently share a host and differ only
            // by mount point or port, so host-only lines can't be told apart when
            // diagnosing which stream failed. The query string and any userinfo are dropped
            // — that is where stream tokens and credentials live.
            string port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
            string path = uri.AbsolutePath == "/" ? string.Empty : uri.AbsolutePath;
            string query = string.IsNullOrEmpty(uri.Query) ? string.Empty : "?(redacted)";

            return $"{uri.Scheme}://{uri.Host}{port}{path}{query}";
        }
        catch
        {
            return "(unparsable-url)";
        }
    }

    private static void Enqueue(string level, string component, string message)
    {
        // Timestamp at the call site so ordering/latency of the writer never skews it.
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{component}] {message}";

#if DEBUG
        Debug.WriteLine(line);
#endif

        EnsureStarted();
        if (_disabled)
        {
            return;
        }

        // Non-blocking: if the writer can't keep up, drop rather than stall a
        // playback thread. Dropped lines are counted and noted in the file later.
        if (!TryQueue(line))
        {
            Interlocked.Increment(ref _droppedSinceLastNote);
        }
    }

    /// <summary>
    /// Adds a line to the queue and, on success, advances the accepted counter the drain
    /// barrier waits on. Every enqueue must go through here or a reader could return before
    /// that line reaches disk.
    /// </summary>
    private static bool TryQueue(string line)
    {
        if (!Queue.TryAdd(line))
        {
            return false;
        }

        Interlocked.Increment(ref _accepted);
        return true;
    }

    /// <summary>
    /// Blocks until every line queued before this call has been written and flushed to
    /// disk, or <paramref name="timeout"/> elapses.
    /// <para>
    /// Flushing the <see cref="StreamWriter"/> alone is not enough: it only pushes what the
    /// writer thread has already dequeued. Lines still in the queue have never reached the
    /// writer, and that thread runs at below-normal priority, so a caller that only flushed
    /// would routinely miss the most recent entries — exactly the ones worth copying when
    /// something has just gone wrong.
    /// </para>
    /// </summary>
    public static void FlushToDisk(TimeSpan? timeout = null)
    {
        EnsureStarted();

        if (_disabled)
        {
            return;
        }

        long target = Interlocked.Read(ref _accepted);
        TimeSpan limit = timeout ?? DefaultFlushTimeout;
        long deadline = Environment.TickCount64 + (long)limit.TotalMilliseconds;

        // Deliberately not holding Gate here — the writer thread needs it to make progress.
        while (Interlocked.Read(ref _consumed) < target)
        {
            if (Environment.TickCount64 >= deadline || _worker is null || !_worker.IsAlive)
            {
                break;
            }

            Thread.Sleep(2);
        }

        lock (Gate)
        {
            try
            {
                _writer?.Flush();
            }
            catch
            {
                // Best effort.
            }
        }
    }

    private static void EnsureStarted()
    {
        if (_started)
        {
            return;
        }

        lock (Gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;

            try
            {
                string root = ApplicationData.Current.LocalFolder.Path;
                _logFolderPath = Path.Combine(root, LogFolderName);
                Directory.CreateDirectory(_logFolderPath);
                _logFilePath = Path.Combine(_logFolderPath, LogFileName);
                _previousLogFilePath = Path.Combine(_logFolderPath, PreviousLogFileName);

                _worker = new Thread(WriterLoop)
                {
                    IsBackground = true,
                    Name = "LogService.Writer",
                    Priority = ThreadPriority.BelowNormal
                };
                _worker.Start();

                // Best-effort flush of buffered lines on normal process exit.
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();

                TryQueue(BuildHeader());
            }
            catch
            {
                // Couldn't set up logging — become a no-op (Debug mirror still works).
                _disabled = true;
                _logFilePath = null;
            }
        }
    }

    private static void WriterLoop()
    {
        try
        {
            foreach (string line in Queue.GetConsumingEnumerable())
            {
                lock (Gate)
                {
                    try
                    {
                        EnsureWriter();
                        if (_writer is null)
                        {
                            continue;
                        }

                        int dropped = Interlocked.Exchange(ref _droppedSinceLastNote, 0);
                        if (dropped > 0)
                        {
                            WriteLineInternal(
                                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [WARN] [LogService] Dropped {dropped} log line(s) (queue full)");
                        }

                        WriteLineInternal(line);

                        // Flush once the burst has drained so readers and a crash-exit
                        // see recent lines, without paying a flush on every line.
                        if (Queue.Count == 0)
                        {
                            _writer.Flush();
                        }
                    }
                    catch
                    {
                        // Drop this line; keep the loop alive.
                    }
                    finally
                    {
                        // Counts consumption, not successful writing: a waiter must be
                        // released even when this line could not be written, or a failing
                        // writer would hang every reader until its timeout.
                        Interlocked.Increment(ref _consumed);
                    }
                }
            }
        }
        catch
        {
            // GetConsumingEnumerable throws only when the collection is disposed; exit quietly.
        }
    }

    /// <summary>Writes one line and rolls the file first if it would exceed the cap.
    /// Caller must hold <see cref="Gate"/> and have a non-null <see cref="_writer"/>.</summary>
    private static void WriteLineInternal(string line)
    {
        // +2 accounts for the newline; approximate is fine for a size trigger.
        long lineBytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
        if (_currentSize + lineBytes > MaxLogBytes)
        {
            Roll();
        }

        _writer!.WriteLine(line);
        _currentSize += lineBytes;
    }

    /// <summary>Opens the writer if needed. Caller must hold <see cref="Gate"/>.</summary>
    private static void EnsureWriter()
    {
        if (_writer is not null || _logFilePath is null)
        {
            return;
        }

        // Append so a prior session's tail is preserved; share read so the user can
        // open the file (or ReadRecentText run) while we hold it.
        var stream = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false
        };
        _currentSize = stream.Length;
    }

    /// <summary>Rolls the active file to the previous generation and reopens a fresh
    /// one. Caller must hold <see cref="Gate"/>.</summary>
    private static void Roll()
    {
        if (_logFilePath is null || _previousLogFilePath is null)
        {
            return;
        }

        try
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;

            if (File.Exists(_previousLogFilePath))
            {
                File.Delete(_previousLogFilePath);
            }

            File.Move(_logFilePath, _previousLogFilePath);
        }
        catch
        {
            // If the roll fails, fall through and keep appending to the existing file.
        }

        try
        {
            EnsureWriter();
        }
        catch
        {
            _writer = null;
        }
    }

    private static void Shutdown()
    {
        try
        {
            Queue.CompleteAdding();
            _worker?.Join(TimeSpan.FromSeconds(2));

            lock (Gate)
            {
                _writer?.Flush();
                _writer?.Dispose();
                _writer = null;
            }
        }
        catch
        {
            // Best effort on exit.
        }
    }

    private static string BuildHeader()
    {
        string version = "unknown";
        try
        {
            Windows.ApplicationModel.PackageVersion v = Windows.ApplicationModel.Package.Current.Id.Version;
            version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
        catch
        {
            // Unpackaged or unavailable — leave "unknown".
        }

        string arch = RuntimeInformationArchitecture();
        return $"===== Traydio session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} | v{version} | {Environment.OSVersion} | {arch} =====";
    }

    private static string RuntimeInformationArchitecture()
    {
        try
        {
            return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        }
        catch
        {
            return "unknown-arch";
        }
    }

    private static string ReadFileSafe(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            // FileShare.ReadWrite is required, not merely polite: the writer holds this file
            // open for writing, and Windows share negotiation is bidirectional. File.ReadAllText
            // opens with FileShare.Read, which refuses to tolerate the existing writer, so it
            // throws for the active log every time - silently leaving the copy with nothing but
            // the previous rolled generation.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }
}
