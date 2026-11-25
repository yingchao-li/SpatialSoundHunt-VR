using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SpatialSoundHunt.EditorTools
{
    /// <summary>
    /// Editor-only helpers around the Python analysis:
    /// manual re-analysis of any session CSV, Python environment set-up / check, and adding the bridge to an existing scene.
    /// Menu:  Tools &gt; Spatial Sound Hunt &gt; Analytics
    /// </summary>
    public static class SpatialSoundHuntAnalyticsMenu
    {
        const string Root = "Tools/Spatial Sound Hunt/Analytics/";

        static AnalysisJob s_Job;
        static Task<string> s_SetupTask;
        static Task<ProcessOutcome> s_CheckTask;
        static string s_CheckPython = "";

        // =====================================================================================
        //  Manual analysis
        // =====================================================================================
        [MenuItem(Root + "Analyze Latest Session", false, 100)]
        public static void AnalyzeLatestSession()
        {
            string csv = AnalysisPaths.LatestSessionCsv();
            if (csv == null)
            {
                EditorUtility.DisplayDialog("Spatial Sound Hunt", "No session CSV found in:\n" + Application.persistentDataPath +
                                            "\n\nRun the experiment to completion first.", "OK");
                return;
            }

            Analyze(csv);
        }

        [MenuItem(Root + "Analyze CSV...", false, 101)]
        public static void AnalyzeChosenCsv()
        {
            string folder = Directory.Exists(Application.persistentDataPath) ? Application.persistentDataPath : "";
            string csv = EditorUtility.OpenFilePanel("Select a Spatial Sound Hunt session CSV", folder, "csv");
            if (!string.IsNullOrEmpty(csv)) Analyze(csv);
        }

        [MenuItem(Root + "Open Reports Folder", false, 102)]
        public static void OpenReportsFolder()
        {
            AnalysisBridge bridge = Object.FindAnyObjectByType<AnalysisBridge>();
            string folder = AnalysisPaths.ResolveReportsFolder(bridge != null ? bridge.reportsFolder : "");
            Directory.CreateDirectory(folder);
            EditorUtility.RevealInFinder(folder);
        }

        static void Analyze(string csv)
        {
            // In Play Mode the scene's bridge handles it, so the in-world UI shows the status too.
            AnalysisBridge bridge = Object.FindAnyObjectByType<AnalysisBridge>();
            if (Application.isPlaying && bridge != null)
            {
                bridge.Enqueue(csv, true, true);
                return;
            }

            if (s_Job != null && !s_Job.IsDone)
            {
                Debug.LogWarning("[SpatialSoundHunt] An analysis is already running.");
                return;
            }

            string error;
            string analytics = AnalysisPaths.ResolveAnalyticsFolder(bridge != null ? bridge.analyticsFolder : "", out error);
            if (analytics == null)
            {
                Debug.LogError("[SpatialSoundHunt] " + error);
                return;
            }

            string python = AnalysisPaths.ResolvePython(bridge != null ? bridge.pythonExecutable : "", analytics, true, out error);
            if (python == null)
            {
                Debug.LogError("[SpatialSoundHunt] " + error);
                return;
            }

            string sessionId = AnalysisPaths.SessionIdFromCsv(csv);
            AnalysisRequest request = new AnalysisRequest
            {
                PythonExecutable = python,
                AnalyticsFolder = analytics,
                CsvPath = Path.GetFullPath(csv),
                OutputFolder = Path.Combine(AnalysisPaths.ResolveReportsFolder(bridge != null ? bridge.reportsFolder : ""), sessionId),
                SessionId = sessionId,
                TimeoutSeconds = bridge != null ? bridge.timeoutSeconds : 180,
                OpenReport = true
            };

            Debug.Log("[SpatialSoundHunt] Analysing " + request.CsvPath + "\n  python: " + python + "\n  output: " + request.OutputFolder);
            s_Job = AnalysisRunner.Start(request);
            EditorApplication.update -= PollAnalysis;
            EditorApplication.update += PollAnalysis;
        }

        static void PollAnalysis()
        {
            if (s_Job == null || !s_Job.IsDone) return;
            EditorApplication.update -= PollAnalysis;

            AnalysisJob job = s_Job;
            s_Job = null;
            AnalysisResult r = job.Result;
            if (r.Success)
            {
                Debug.Log("[SpatialSoundHunt] Analysis finished in " + r.Seconds.ToString("F1") + " s -> " + r.ReportPath);
                AnalysisBridge.OpenReport(r.ReportPath);
            }
            else
            {
                Debug.LogError("[SpatialSoundHunt] Analysis FAILED: " + r.ErrorMessage + "\n  CSV kept at: " + job.Request.CsvPath +
                               (string.IsNullOrWhiteSpace(r.Stderr) ? "" : "\n  stderr:\n" + r.Stderr.Trim()));
            }
        }

        // =====================================================================================
        //  Python environment
        // =====================================================================================
        [MenuItem(Root + "Check Python Environment", false, 120)]
        public static void CheckPythonEnvironment()
        {
            if (s_CheckTask != null && !s_CheckTask.IsCompleted) return;

            string error;
            AnalysisBridge bridge = Object.FindAnyObjectByType<AnalysisBridge>();
            string analytics = AnalysisPaths.ResolveAnalyticsFolder(bridge != null ? bridge.analyticsFolder : "", out error);
            string python = AnalysisPaths.ResolvePython(bridge != null ? bridge.pythonExecutable : "", analytics, true, out error);
            if (python == null)
            {
                Debug.LogError("[SpatialSoundHunt] " + error);
                return;
            }

            s_CheckPython = python;
            s_CheckTask = AnalysisRunner.StartProcess(python, new List<string>
            {
                "-c", "import sys, numpy, pandas, plotly; print('python', sys.version.split()[0], '| numpy', numpy.__version__, '| pandas', pandas.__version__, '| plotly', plotly.__version__)"
            }, analytics, 60);
            EditorApplication.update -= PollCheck;
            EditorApplication.update += PollCheck;
        }

        static void PollCheck()
        {
            if (s_CheckTask == null || !s_CheckTask.IsCompleted) return;
            EditorApplication.update -= PollCheck;

            ProcessOutcome o = s_CheckTask.Result;
            s_CheckTask = null;
            if (o.Started && o.ExitCode == 0)
                Debug.Log("[SpatialSoundHunt] Python OK: " + s_CheckPython + "\n  " + o.Stdout.Trim());
            else
                Debug.LogError("[SpatialSoundHunt] Python at " + s_CheckPython + " cannot import numpy/pandas/plotly (exit " + o.ExitCode + "). " +
                               "Run Tools > Spatial Sound Hunt > Analytics > Set Up Python Environment.\n" + o.StartError + o.Stderr.Trim());
        }

        [MenuItem(Root + "Set Up Python Environment", false, 121)]
        public static void SetUpPythonEnvironment()
        {
            if (s_SetupTask != null && !s_SetupTask.IsCompleted)
            {
                Debug.LogWarning("[SpatialSoundHunt] Python set-up is already running.");
                return;
            }

            string root = AnalysisPaths.ProjectRoot();
            string error;
            string analytics = AnalysisPaths.ResolveAnalyticsFolder("", out error);
            if (root == null || analytics == null)
            {
                Debug.LogError("[SpatialSoundHunt] " + (error.Length > 0 ? error : "Project root not found."));
                return;
            }

            string basePython = AnalysisPaths.ResolvePython("", analytics, false, out error);
            if (basePython == null)
            {
                Debug.LogError("[SpatialSoundHunt] " + error);
                EditorUtility.DisplayDialog("Spatial Sound Hunt", error, "OK");
                return;
            }

            string venvPython = AnalysisPaths.VenvPython(root);
            string requirements = Path.Combine(analytics, "requirements.txt");
            if (!EditorUtility.DisplayDialog("Spatial Sound Hunt - Python environment",
                    "This creates a virtual environment at\n" + Path.Combine(root, ".venv") +
                    "\nusing\n" + basePython + "\nand installs numpy, pandas, plotly and pytest from PyPI (internet needed, a few minutes).\n\nNothing outside the project folder is changed.",
                    "Create", "Cancel"))
                return;

            Debug.Log("[SpatialSoundHunt] Setting up the Python environment in the background (see the Console for the result)...");
            s_SetupTask = Task.Run(() => RunSetup(root, basePython, venvPython, requirements, analytics));
            EditorApplication.update -= PollSetup;
            EditorApplication.update += PollSetup;
        }

        static string RunSetup(string root, string basePython, string venvPython, string requirements, string analytics)
        {
            StringBuilder log = new StringBuilder();

            if (!File.Exists(venvPython))
            {
                ProcessOutcome venv = AnalysisRunner.RunProcess(basePython, new List<string> { "-m", "venv", Path.Combine(root, ".venv") }, root, 300);
                log.AppendLine("venv: exit " + venv.ExitCode + " " + venv.StartError + venv.Stderr.Trim());
                if (!venv.Started || venv.ExitCode != 0 || !File.Exists(venvPython))
                    return "FAILED creating the virtual environment.\n" + log;
            }

            ProcessOutcome pip = AnalysisRunner.RunProcess(venvPython, new List<string> { "-m", "pip", "install", "-r", requirements }, root, 1200);
            log.AppendLine("pip install: exit " + pip.ExitCode + " in " + pip.Seconds.ToString("F0") + " s");
            if (!pip.Started || pip.ExitCode != 0 || pip.TimedOut)
                return "FAILED installing the requirements.\n" + log + Tail(pip.Stdout + "\n" + pip.Stderr, 2500);

            ProcessOutcome check = AnalysisRunner.RunProcess(venvPython, new List<string>
            {
                "-c", "import sys, numpy, pandas, plotly; print('python', sys.version.split()[0], '| numpy', numpy.__version__, '| pandas', pandas.__version__, '| plotly', plotly.__version__)"
            }, analytics, 60);
            if (check.ExitCode != 0) return "FAILED: packages installed but the import check failed.\n" + log + check.Stderr;

            return "OK " + venvPython + "\n" + check.Stdout.Trim();
        }

        static void PollSetup()
        {
            if (s_SetupTask == null || !s_SetupTask.IsCompleted) return;
            EditorApplication.update -= PollSetup;

            string result = s_SetupTask.IsFaulted ? "FAILED: " + s_SetupTask.Exception.GetBaseException().Message : s_SetupTask.Result;
            s_SetupTask = null;
            if (result.StartsWith("OK")) Debug.Log("[SpatialSoundHunt] Python environment ready.\n" + result);
            else Debug.LogError("[SpatialSoundHunt] Python environment set-up " + result);
        }

        static string Tail(string text, int max)
        {
            text = text.Trim();
            return text.Length <= max ? text : "..." + text.Substring(text.Length - max);
        }

        // =====================================================================================
        //  Existing scenes
        // =====================================================================================
        [MenuItem(Root + "Add Analysis Bridge To Open Scene", false, 140)]
        public static void AddBridgeToOpenScene()
        {
            ExperimentManager manager = Object.FindAnyObjectByType<ExperimentManager>();
            if (manager == null)
            {
                EditorUtility.DisplayDialog("Spatial Sound Hunt", "No ExperimentManager in the open scene.\nOpen the demo scene first.", "OK");
                return;
            }

            AnalysisBridge bridge = manager.GetComponent<AnalysisBridge>();
            if (bridge == null) bridge = Undo.AddComponent<AnalysisBridge>(manager.gameObject);

            bridge.manager = manager;
            bridge.recorder = manager.recorder != null ? manager.recorder : manager.GetComponent<SessionRecorder>();
            EditorUtility.SetDirty(bridge);
            EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
            Debug.Log("[SpatialSoundHunt] AnalysisBridge is on '" + manager.gameObject.name + "'. Save the scene (Cmd+S).");
        }
    }
}
