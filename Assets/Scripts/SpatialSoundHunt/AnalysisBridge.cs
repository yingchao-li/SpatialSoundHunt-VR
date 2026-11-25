using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SpatialSoundHunt
{
    public enum AnalysisState
    {
        Idle = 0,
        Running = 1,
        Succeeded = 2,
        Failed = 3,
        /// <summary>Analysis cannot start (no Python / no Analytics folder). The CSV is kept.</summary>
        Unavailable = 4
    }

    /// <summary>Everything the worker thread needs. Plain strings only - no UnityEngine objects.</summary>
    public sealed class AnalysisRequest
    {
        public string PythonExecutable;
        public string AnalyticsFolder;   // working directory + location of analyze_session.py
        public string CsvPath;
        public string OutputFolder;
        public string SessionId;
        public int TimeoutSeconds = 180;
        public bool OpenReport = true;
    }

    public sealed class ProcessOutcome
    {
        public bool Started;
        public bool TimedOut;
        public int ExitCode = -1;
        public string Stdout = "";
        public string Stderr = "";
        public string StartError = "";
        public double Seconds;
    }

    public sealed class AnalysisResult
    {
        public AnalysisRequest Request;
        public bool Success;
        public int ExitCode = -1;
        public bool TimedOut;
        public string Stdout = "";
        public string Stderr = "";
        public string ReportPath = "";
        public string SummaryPath = "";
        public string ErrorMessage = "";
        public double Seconds;
    }

    /// <summary>Handle for a background analysis. Poll <see cref="IsDone"/> from the main thread.</summary>
    public sealed class AnalysisJob
    {
        public readonly AnalysisRequest Request;
        volatile bool done;
        AnalysisResult result;

        public AnalysisJob(AnalysisRequest request) { Request = request; }
        public bool IsDone { get { return done; } }
        public AnalysisResult Result { get { return result; } }

        internal void Complete(AnalysisResult r)
        {
            result = r;
            done = true; // volatile write publishes 'result'
        }
    }

    /// <summary>Resolves the Python interpreter, the Analytics folder and the output folders.</summary>
    public static class AnalysisPaths
    {
        public const string PythonEnvVar = "SPATIALSOUNDHUNT_PYTHON";
        public const string AnalyticsEnvVar = "SPATIALSOUNDHUNT_ANALYTICS";
        public const string CsvPrefix = "SpatialSoundHunt_session_";
        public const string ScriptName = "analyze_session.py";

        static bool IsWindows
        {
            get
            {
                return Application.platform == RuntimePlatform.WindowsEditor || Application.platform == RuntimePlatform.WindowsPlayer;
            }
        }

        /// <summary>The Unity project folder - only known inside the Editor (null in a standalone build).</summary>
        public static string ProjectRoot()
        {
            if (!Application.isEditor) return null;
            DirectoryInfo parent = Directory.GetParent(Application.dataPath);
            return parent != null ? parent.FullName : null;
        }

        public static string VenvPython(string root)
        {
            if (string.IsNullOrEmpty(root)) return null;
            return IsWindows
                ? Path.Combine(root, ".venv", "Scripts", "python.exe")
                : Path.Combine(root, ".venv", "bin", "python");
        }

        /// <summary>Folder that contains analyze_session.py, or null (with a message in <paramref name="error"/>).</summary>
        public static string ResolveAnalyticsFolder(string configured, out string error)
        {
            List<string> tried = new List<string>();
            string[] candidates =
            {
                configured,
                Environment.GetEnvironmentVariable(AnalyticsEnvVar),
                ProjectRoot() != null ? Path.Combine(ProjectRoot(), "Analytics") : null
            };

            foreach (string c in candidates)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                string full = Path.GetFullPath(c.Trim());
                tried.Add(full);
                if (File.Exists(Path.Combine(full, ScriptName)))
                {
                    error = "";
                    return full;
                }
            }

            error = tried.Count == 0
                ? "No Analytics folder configured. In a standalone build set AnalysisBridge > Analytics Folder (or the environment variable "
                  + AnalyticsEnvVar + ")."
                : "analyze_session.py not found in: " + string.Join(", ", tried.ToArray());
            return null;
        }

        public static string ResolveReportsFolder(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured.Trim());
            string root = ProjectRoot();
            return root != null ? Path.Combine(root, "reports") : Path.Combine(Application.persistentDataPath, "reports");
        }

        /// <summary>
        /// Interpreter lookup order: configured path, SPATIALSOUNDHUNT_PYTHON, project .venv, Analytics/.venv,
        /// then well-known system locations and PATH. A Finder-launched Unity has a minimal PATH, so absolute
        /// Homebrew / python.org locations are probed explicitly.
        /// </summary>
        public static string ResolvePython(string configured, string analyticsFolder, bool includeVenv, out string error)
        {
            List<string> candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured.Trim());
            string env = Environment.GetEnvironmentVariable(PythonEnvVar);
            if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env.Trim());

            if (includeVenv)
            {
                string root = ProjectRoot();
                if (root != null) candidates.Add(VenvPython(root));
                if (!string.IsNullOrEmpty(analyticsFolder)) candidates.Add(VenvPython(analyticsFolder));
            }

            candidates.AddRange(SystemPythonCandidates());

            foreach (string c in candidates)
            {
                if (string.IsNullOrEmpty(c)) continue;
                try
                {
                    if (File.Exists(c))
                    {
                        error = "";
                        return c;
                    }
                }
                catch (Exception)
                {
                    // ignore malformed candidate paths
                }
            }

            error = "No Python interpreter found. Install Python 3 (e.g. 'brew install python') and run "
                    + "Tools > Spatial Sound Hunt > Analytics > Set Up Python Environment, or set AnalysisBridge > Python Executable.";
            return null;
        }

        public static IEnumerable<string> SystemPythonCandidates()
        {
            if (IsWindows)
            {
                string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                if (!string.IsNullOrEmpty(local))
                {
                    foreach (string v in new[] { "313", "312", "311", "310", "39" })
                        yield return Path.Combine(local, "Programs", "Python", "Python" + v, "python.exe");
                }

                foreach (string p in SearchPath(new[] { "python.exe" })) yield return p;
                yield break;
            }

            yield return "/opt/homebrew/bin/python3";
            yield return "/usr/local/bin/python3";
            yield return "/Library/Frameworks/Python.framework/Versions/Current/bin/python3";
            foreach (string p in SearchPath(new[] { "python3", "python" })) yield return p;

            // /usr/bin/python3 is only a stub that opens an installer dialog unless the Command Line Tools exist.
            if (Directory.Exists("/Library/Developer/CommandLineTools") || Directory.Exists("/Applications/Xcode.app"))
                yield return "/usr/bin/python3";
        }

        static IEnumerable<string> SearchPath(string[] names)
        {
            string path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) yield break;
            foreach (string dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                foreach (string name in names)
                {
                    string full;
                    try { full = Path.Combine(dir.Trim(), name); }
                    catch (Exception) { continue; }
                    yield return full;
                }
            }
        }

        public static string SessionIdFromCsv(string csvPath)
        {
            string stem = Path.GetFileNameWithoutExtension(csvPath) ?? "session";
            return stem.StartsWith(CsvPrefix, StringComparison.Ordinal) ? stem.Substring(CsvPrefix.Length) : stem;
        }

        /// <summary>Newest SpatialSoundHunt_session_*.csv in Application.persistentDataPath, or null.</summary>
        public static string LatestSessionCsv()
        {
            string folder = Application.persistentDataPath;
            if (!Directory.Exists(folder)) return null;
            string best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (string f in Directory.GetFiles(folder, CsvPrefix + "*.csv"))
            {
                DateTime t = File.GetLastWriteTimeUtc(f);
                if (best == null || t > bestTime)
                {
                    best = f;
                    bestTime = t;
                }
            }

            return best;
        }
    }

    /// <summary>Runs external processes on a worker thread. Never touches the UnityEngine API from that thread.</summary>
    public static class AnalysisRunner
    {
        /// <summary>Starts the Python analysis in the background and returns immediately.</summary>
        public static AnalysisJob Start(AnalysisRequest request)
        {
            AnalysisJob job = new AnalysisJob(request);
            Task.Run(() =>
            {
                AnalysisResult result;
                try
                {
                    result = RunAnalysis(request);
                }
                catch (Exception e)
                {
                    result = new AnalysisResult { Request = request, ErrorMessage = "Unexpected error: " + e.Message };
                }

                job.Complete(result);
            });
            return job;
        }

        /// <summary>Generic runner (also used for venv / pip setup). Arguments are passed as an argument list: no shell, no quoting.</summary>
        public static Task<ProcessOutcome> StartProcess(string exe, IList<string> args, string workingDirectory, int timeoutSeconds)
        {
            return Task.Run(() => RunProcess(exe, args, workingDirectory, timeoutSeconds));
        }

        public static ProcessOutcome RunProcess(string exe, IList<string> args, string workingDirectory, int timeoutSeconds)
        {
            ProcessOutcome outcome = new ProcessOutcome();
            Stopwatch clock = Stopwatch.StartNew();

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            if (!string.IsNullOrEmpty(workingDirectory)) psi.WorkingDirectory = workingDirectory;
            foreach (string a in args) psi.ArgumentList.Add(a);
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUNBUFFERED"] = "1";
            psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
            psi.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";

            StringBuilder stdout = new StringBuilder();
            StringBuilder stderr = new StringBuilder();

            try
            {
                using (Process process = new Process { StartInfo = psi })
                {
                    process.OutputDataReceived += (s, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
                    process.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };

                    if (!process.Start())
                    {
                        outcome.StartError = "Process.Start returned false for " + exe;
                        return outcome;
                    }

                    outcome.Started = true;
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    int timeoutMs = Math.Max(1, timeoutSeconds) * 1000;
                    if (!process.WaitForExit(timeoutMs))
                    {
                        outcome.TimedOut = true;
                        try { process.Kill(); } // the analysis script starts no child processes
                        catch (Exception) { /* already gone */ }
                        process.WaitForExit(3000);
                    }
                    else
                    {
                        process.WaitForExit(); // flushes the asynchronous output handlers
                    }

                    outcome.ExitCode = process.HasExited ? process.ExitCode : -1;
                }
            }
            catch (Exception e)
            {
                outcome.StartError = e.GetType().Name + ": " + e.Message;
            }

            clock.Stop();
            outcome.Seconds = clock.Elapsed.TotalSeconds;
            lock (stdout) outcome.Stdout = stdout.ToString();
            lock (stderr) outcome.Stderr = stderr.ToString();
            return outcome;
        }

        static AnalysisResult RunAnalysis(AnalysisRequest request)
        {
            AnalysisResult result = new AnalysisResult { Request = request };

            string script = Path.Combine(request.AnalyticsFolder, AnalysisPaths.ScriptName);
            List<string> args = new List<string> { script, "--input", request.CsvPath, "--output", request.OutputFolder };

            ProcessOutcome run = RunProcess(request.PythonExecutable, args, request.AnalyticsFolder, request.TimeoutSeconds);
            result.Seconds = run.Seconds;
            result.ExitCode = run.ExitCode;
            result.TimedOut = run.TimedOut;
            result.Stdout = run.Stdout;
            result.Stderr = run.Stderr;

            if (!run.Started)
            {
                result.ErrorMessage = "Could not start Python (" + request.PythonExecutable + "): " + run.StartError;
                return result;
            }

            if (run.TimedOut)
            {
                result.ErrorMessage = "Analysis timed out after " + request.TimeoutSeconds + " s and was stopped.";
                return result;
            }

            foreach (string raw in run.Stdout.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("REPORT_PATH=", StringComparison.Ordinal)) result.ReportPath = line.Substring("REPORT_PATH=".Length);
                else if (line.StartsWith("SUMMARY_PATH=", StringComparison.Ordinal)) result.SummaryPath = line.Substring("SUMMARY_PATH=".Length);
            }

            if (run.ExitCode == 0 && !string.IsNullOrEmpty(result.ReportPath) && File.Exists(result.ReportPath))
            {
                result.Success = true;
                return result;
            }

            result.ErrorMessage = DescribeFailure(run, result.ReportPath);
            return result;
        }

        static string DescribeFailure(ProcessOutcome run, string reportPath)
        {
            string last = LastErrorLine(run.Stderr);
            string message = run.ExitCode == 0
                ? "Python finished but no report file was produced" + (string.IsNullOrEmpty(reportPath) ? "." : " (" + reportPath + " is missing).")
                : "Python exited with code " + run.ExitCode + (last.Length > 0 ? ": " + last : ".");

            if (run.Stderr.Contains("ModuleNotFoundError") || run.Stderr.Contains("ImportError"))
                message += "  -> Python packages are missing. Run Tools > Spatial Sound Hunt > Analytics > Set Up Python Environment.";
            return message;
        }

        static string LastErrorLine(string stderr)
        {
            string[] lines = stderr.Split('\n');
            string fallback = "";
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string l = lines[i].Trim();
                if (l.Length == 0) continue;
                if (l.StartsWith("ERROR:", StringComparison.Ordinal)) return l;
                if (fallback.Length == 0) fallback = l;
            }

            return fallback;
        }
    }

    /// <summary>
    /// Connects the experiment to the Python 6-DoF analysis:
    /// ExperimentManager.ExperimentCompleted -> wait until SessionRecorder has closed the CSV -> run
    /// Analytics/analyze_session.py on a worker thread -> open reports/&lt;session_id&gt;/report.html.
    /// A Python problem is logged and shown in the UI; it never interrupts the experiment and the CSV is always kept.
    /// </summary>
    public class AnalysisBridge : MonoBehaviour
    {
        [Header("Wiring (assigned by the builder)")]
        public ExperimentManager manager;
        public SessionRecorder recorder;

        [Header("Behaviour")]
        public bool autoAnalyzeOnCompletion = true;
        public bool openReportOnSuccess = true;

        [Header("Paths (empty = automatic)")]
        [Tooltip("Python 3 interpreter. Empty: SPATIALSOUNDHUNT_PYTHON, <project>/.venv, then system locations.")]
        public string pythonExecutable = "";
        [Tooltip("Folder that contains analyze_session.py. Empty: <project>/Analytics (Editor only; a standalone build must set it).")]
        public string analyticsFolder = "";
        [Tooltip("Where report folders are created. Empty: <project>/reports (Editor) or <persistentDataPath>/reports.")]
        public string reportsFolder = "";
        [Min(10)] public int timeoutSeconds = 180;

        public AnalysisState State { get; private set; } = AnalysisState.Idle;
        public string StatusMessage { get; private set; } = "";
        public string LastReportPath { get; private set; } = "";
        public string LastError { get; private set; } = "";

        public event Action<AnalysisState, string> StatusChanged;
        public event Action<AnalysisResult> AnalysisFinished;

        readonly Queue<AnalysisRequest> queue = new Queue<AnalysisRequest>();
        readonly HashSet<string> handled = new HashSet<string>(StringComparer.Ordinal);
        AnalysisJob current;
        bool completionPending;

        void Awake()
        {
            if (manager == null) manager = GetComponent<ExperimentManager>();
            if (recorder == null) recorder = GetComponent<SessionRecorder>();
        }

        void OnEnable()
        {
            if (manager != null) manager.ExperimentCompleted += OnExperimentCompleted;
            if (recorder != null) recorder.SessionClosed += OnSessionClosed;
        }

        void OnDisable()
        {
            if (manager != null) manager.ExperimentCompleted -= OnExperimentCompleted;
            if (recorder != null) recorder.SessionClosed -= OnSessionClosed;
        }

        void Update()
        {
            if (current != null && current.IsDone)
            {
                AnalysisJob finished = current;
                current = null;
                try { Finish(finished); }
                catch (Exception e) { Fail("Analysis result handling failed: " + e.Message); }
            }

            if (current == null && queue.Count > 0) StartNext();
        }

        // ---- trigger -------------------------------------------------------------------------
        void OnExperimentCompleted()
        {
            completionPending = true;
            TryTriggerAfterCompletion();
        }

        void OnSessionClosed(string path, string reason)
        {
            TryTriggerAfterCompletion();
        }

        /// <summary>
        /// Starts only when the experiment has completed AND the recorder has closed the file. Today the
        /// recorder closes the CSV before the completion event fires; if that order ever changes the analysis
        /// simply waits for SessionClosed.
        /// </summary>
        void TryTriggerAfterCompletion()
        {
            try
            {
                if (!completionPending || recorder == null) return;
                if (recorder.IsRecording) return;
                if (recorder.LastEndReason != "Completed" || string.IsNullOrEmpty(recorder.LastSavedPath)) return;

                completionPending = false;
                if (!autoAnalyzeOnCompletion) return;
                Enqueue(recorder.LastSavedPath, openReportOnSuccess, false);
            }
            catch (Exception e)
            {
                Fail("Could not start the analysis: " + e.Message);
            }
        }

        // ---- public API ------------------------------------------------------------------------
        [ContextMenu("Analyze Latest Session CSV")]
        public void AnalyzeLatest()
        {
            string csv = AnalysisPaths.LatestSessionCsv();
            if (csv == null)
            {
                Fail("No session CSV found in " + Application.persistentDataPath);
                return;
            }

            Enqueue(csv, true, true);
        }

        /// <summary>Queues an analysis of an existing CSV. <paramref name="force"/> bypasses the 'already analysed' guard.</summary>
        public bool Enqueue(string csvPath, bool openReport, bool force)
        {
            try
            {
                if (string.IsNullOrEmpty(csvPath) || !File.Exists(csvPath))
                {
                    Fail("CSV not found: " + csvPath);
                    return false;
                }

                string key = Path.GetFullPath(csvPath);
                if (!force && handled.Contains(key))
                {
                    Debug.Log("[AnalysisBridge] Already analysed or queued, skipping duplicate: " + key);
                    return false;
                }

                string error;
                string analytics = AnalysisPaths.ResolveAnalyticsFolder(analyticsFolder, out error);
                if (analytics == null)
                {
                    Unavailable(error);
                    return false;
                }

                string python = AnalysisPaths.ResolvePython(pythonExecutable, analytics, true, out error);
                if (python == null)
                {
                    Unavailable(error);
                    return false;
                }

                string sessionId = AnalysisPaths.SessionIdFromCsv(key);
                AnalysisRequest request = new AnalysisRequest
                {
                    PythonExecutable = python,
                    AnalyticsFolder = analytics,
                    CsvPath = key,
                    OutputFolder = Path.Combine(AnalysisPaths.ResolveReportsFolder(reportsFolder), sessionId),
                    SessionId = sessionId,
                    TimeoutSeconds = timeoutSeconds,
                    OpenReport = openReport
                };

                handled.Add(key);
                queue.Enqueue(request);
                SetStatus(AnalysisState.Running, "Analysing session " + sessionId + " ...");
                return true;
            }
            catch (Exception e)
            {
                Fail("Could not queue the analysis: " + e.Message);
                return false;
            }
        }

        // ---- worker handling (main thread) ---------------------------------------------------------
        void StartNext()
        {
            AnalysisRequest request = queue.Dequeue();
            Debug.Log("[AnalysisBridge] Analysing " + request.CsvPath + "\n  python:  " + request.PythonExecutable +
                      "\n  script:  " + Path.Combine(request.AnalyticsFolder, AnalysisPaths.ScriptName) +
                      "\n  output:  " + request.OutputFolder);
            SetStatus(AnalysisState.Running, "Analysing session " + request.SessionId + " ...");
            current = AnalysisRunner.Start(request);
        }

        void Finish(AnalysisJob job)
        {
            AnalysisResult r = job.Result;
            if (r.Success)
            {
                LastReportPath = r.ReportPath;
                LastError = "";
                Debug.Log("[AnalysisBridge] Analysis finished in " + r.Seconds.ToString("F1") + " s -> " + r.ReportPath);
                if (!string.IsNullOrEmpty(r.Stderr.Trim()))
                    Debug.Log("[AnalysisBridge] Python warnings:\n" + r.Stderr.Trim());

                string status = "Analysis ready: " + Path.GetFileName(Path.GetDirectoryName(r.ReportPath) ?? "") + "/report.html";
                if (job.Request.OpenReport)
                {
                    status = OpenReport(r.ReportPath) ? "Report opened in your browser." : "Report ready (could not open the browser): " + r.ReportPath;
                }

                SetStatus(AnalysisState.Succeeded, status);
            }
            else
            {
                handled.Remove(job.Request.CsvPath); // allow a retry
                LastError = r.ErrorMessage;
                Debug.LogError("[AnalysisBridge] Analysis FAILED: " + r.ErrorMessage +
                               "\n  CSV kept at: " + job.Request.CsvPath +
                               (string.IsNullOrWhiteSpace(r.Stderr) ? "" : "\n  stderr:\n" + Tail(r.Stderr, 1800)) +
                               (string.IsNullOrWhiteSpace(r.Stdout) ? "" : "\n  stdout:\n" + Tail(r.Stdout, 600)));
                SetStatus(AnalysisState.Failed, "Analysis failed - see Console. CSV kept: " + Path.GetFileName(job.Request.CsvPath));
            }

            Action<AnalysisResult> handler = AnalysisFinished;
            if (handler != null) handler(r);
        }

        public static bool OpenReport(string reportPath)
        {
            try
            {
                Application.OpenURL(new Uri(reportPath).AbsoluteUri);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AnalysisBridge] Could not open the report: " + e.Message);
                return false;
            }
        }

        // ---- status ------------------------------------------------------------------------------------
        void Unavailable(string message)
        {
            LastError = message;
            Debug.LogWarning("[AnalysisBridge] Analysis unavailable: " + message + "  (the session CSV is kept)");
            SetStatus(AnalysisState.Unavailable, "Analysis unavailable - see Console. CSV kept.");
        }

        void Fail(string message)
        {
            LastError = message;
            Debug.LogError("[AnalysisBridge] " + message);
            SetStatus(AnalysisState.Failed, message);
        }

        void SetStatus(AnalysisState state, string message)
        {
            State = state;
            StatusMessage = message;
            Action<AnalysisState, string> handler = StatusChanged;
            if (handler != null)
            {
                try { handler(state, message); }
                catch (Exception e) { Debug.LogWarning("[AnalysisBridge] Status listener failed: " + e.Message); }
            }
        }

        static string Tail(string text, int max)
        {
            text = text.Trim();
            return text.Length <= max ? text : "..." + text.Substring(text.Length - max);
        }
    }
}
