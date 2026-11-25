using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace SpatialSoundHunt
{
    /// <summary>
    /// Records HMD pose samples and experiment events to a CSV file in Application.persistentDataPath.
    /// One file per session (Start -> Completed / Aborted). Rows are streamed to disk so data
    /// survives a crash. No analysis is done here.
    /// </summary>
    public class SessionRecorder : MonoBehaviour
    {
        public const string Header =
            "timestamp_utc,session_time_s,session_id,condition,state,record_type,event,current_target,interacted_target," +
            "hmd_px,hmd_py,hmd_pz,hmd_qx,hmd_qy,hmd_qz,hmd_qw,details";

        [Header("Wiring (assigned by the builder)")]
        public ExperimentManager manager;

        [Header("Settings")]
        [Range(1f, 60f)] public float sampleRateHz = 10f;
        public string filePrefix = "SpatialSoundHunt_session_";

        public bool IsRecording { get; private set; }
        public string SessionId { get; private set; } = "";
        public string LastSavedPath { get; private set; } = "";
        /// <summary>Why the last session ended: Completed, Aborted, Restarted or Interrupted.</summary>
        public string LastEndReason { get; private set; } = "";
        public int RowCount { get; private set; }

        /// <summary>Raised with the CSV path when a session file has been closed.</summary>
        public event Action<string> SessionSaved;

        /// <summary>Raised after the CSV is flushed and closed, with its path and the end reason (see <see cref="LastEndReason"/>).</summary>
        public event Action<string, string> SessionClosed;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        StreamWriter writer;
        string currentPath = "";
        float sessionStartRealtime;
        float nextSampleTime;
        float lastFlushTime;
        readonly StringBuilder row = new StringBuilder(256);

        void Update()
        {
            if (!IsRecording || writer == null) return;

            float now = Time.realtimeSinceStartup;
            if (now < nextSampleTime) return;

            nextSampleTime = now + 1f / Mathf.Max(1f, sampleRateHz);
            WriteRow("Pose", "", "", "");

            if (now - lastFlushTime > 1f)
            {
                writer.Flush();
                lastFlushTime = now;
            }
        }

        void OnApplicationQuit()
        {
            EndSession("Interrupted");
        }

        void OnDestroy()
        {
            EndSession("Interrupted");
        }

        /// <summary>Opens a new CSV file. Any session that is still open is closed first.</summary>
        public void BeginSession(string orderDescription)
        {
            if (IsRecording) EndSession("Restarted");

            SessionId = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", Inv) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string path = Path.Combine(Application.persistentDataPath, filePrefix + SessionId + ".csv");

            try
            {
                writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogError("[SessionRecorder] Cannot create " + path + ": " + e.Message);
                writer = null;
                return;
            }

            currentPath = path;
            sessionStartRealtime = Time.realtimeSinceStartup;
            nextSampleTime = 0f;
            lastFlushTime = sessionStartRealtime;
            RowCount = 0;
            IsRecording = true;

            writer.WriteLine(Header);
            WriteRow("Event", "SessionStart", "", "order=" + orderDescription);
            writer.Flush();
        }

        /// <summary>Writes one event row (also contains the current HMD pose).</summary>
        public void LogEvent(string eventName, string interactedTarget = "", string details = "")
        {
            if (!IsRecording || writer == null) return;
            WriteRow("Event", eventName, interactedTarget, details);
            writer.Flush();
        }

        /// <summary>Closes the CSV file. Safe to call repeatedly.</summary>
        public void EndSession(string reason)
        {
            if (!IsRecording) return;

            WriteRow("Event", "SessionEnd", "", "reason=" + reason);

            try
            {
                writer.Flush();
                writer.Dispose();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SessionRecorder] Closing file failed: " + e.Message);
            }

            writer = null;
            IsRecording = false;
            LastSavedPath = currentPath;
            LastEndReason = reason;
            Debug.Log("[SessionRecorder] Saved " + RowCount + " rows (" + reason + ") -> " + currentPath);
            try
            {
                SessionSaved?.Invoke(currentPath);
                SessionClosed?.Invoke(currentPath, reason);
            }
            catch (Exception e)
            {
                // a listener must never be able to break the experiment
                Debug.LogWarning("[SessionRecorder] A session listener threw: " + e.Message);
            }
        }

        void WriteRow(string recordType, string eventName, string interactedTarget, string details)
        {
            if (writer == null) return;

            Vector3 position;
            Quaternion rotation;
            bool hasPose = HeadPose.TryGet(out position, out rotation);

            string condition = manager != null ? manager.Condition.ToString() : "";
            string state = manager != null ? manager.State.ToString() : "";
            string current = manager != null ? manager.CurrentTargetId : "";

            row.Length = 0;
            row.Append(DateTime.UtcNow.ToString("o", Inv)).Append(',');
            row.Append((Time.realtimeSinceStartup - sessionStartRealtime).ToString("F3", Inv)).Append(',');
            row.Append(SessionId).Append(',');
            row.Append(condition).Append(',');
            row.Append(state).Append(',');
            row.Append(recordType).Append(',');
            row.Append(Csv(eventName)).Append(',');
            row.Append(Csv(current)).Append(',');
            row.Append(Csv(interactedTarget)).Append(',');

            if (hasPose)
            {
                row.Append(position.x.ToString("F4", Inv)).Append(',');
                row.Append(position.y.ToString("F4", Inv)).Append(',');
                row.Append(position.z.ToString("F4", Inv)).Append(',');
                row.Append(rotation.x.ToString("F5", Inv)).Append(',');
                row.Append(rotation.y.ToString("F5", Inv)).Append(',');
                row.Append(rotation.z.ToString("F5", Inv)).Append(',');
                row.Append(rotation.w.ToString("F5", Inv)).Append(',');
            }
            else
            {
                row.Append(",,,,,,,");
            }

            row.Append(Csv(details));

            writer.WriteLine(row.ToString());
            RowCount++;
        }

        static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
