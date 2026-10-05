using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ShimmeringUnity
{
    /// <summary>
    /// Custom inspector for the Shimmer device
    /// </summary>
    [CustomEditor(typeof(ShimmerDevice), true)]
    public class ShimmerDeviceEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            ShimmerDevice shimmerDevice = (ShimmerDevice)target;
            GUIStyle style = new GUIStyle();
            style.richText = true;
            if (Application.isPlaying)
                GUILayout.Label($"<size=24><color=#00ff00>Current State: {shimmerDevice.CurrentState.ToString()}</color></size>", style);
            else
                GUILayout.Label($"<size=24><color=#ff0000>App not running.</color></size>", style);

            if (Application.isPlaying)
            {
                EditorGUILayout.LabelField("Hardware", shimmerDevice.HardwareVersion < 0 ? "Not detected" : ((ShimmerAPI.ShimmerBluetooth.ShimmerVersion)shimmerDevice.HardwareVersion).ToString());
                EditorGUILayout.LabelField("Firmware", shimmerDevice.FirmwareVersion);
                EditorGUILayout.LabelField("Actual sample rate", $"{shimmerDevice.ActualSamplingRate:F2} Hz");
                EditorGUILayout.LabelField("Packets received / pending / dropped", $"{shimmerDevice.ReceivedPackets} / {shimmerDevice.PendingPackets} / {shimmerDevice.DroppedPackets}");
                if (!string.IsNullOrEmpty(shimmerDevice.LastError)) EditorGUILayout.HelpBox(shimmerDevice.LastError, MessageType.Warning);
            }

            if (Application.isPlaying && GUILayout.Button("Connect"))
            {
                shimmerDevice.Connect();
            }
            if (Application.isPlaying && GUILayout.Button("Disconnect"))
            {
                shimmerDevice.Disconnect();
            }
            if (Application.isPlaying && GUILayout.Button("Start Streaming"))
            {
                shimmerDevice.StartStreaming();
            }
            if (Application.isPlaying && GUILayout.Button("Stop Streaming"))
            {
                shimmerDevice.StopStreaming();
            }
            if (Application.isPlaying && GUILayout.Button("Cancel Connection / Close Port"))
            {
                shimmerDevice.ForceAbortThread();
            }
            DrawDefaultInspector();
            if (EditorApplication.isPlaying)
                Repaint();
        }
    }
}
