using System;
using ShimmerAPI;
using ShimmerLibrary;
using UnityEngine;

namespace ShimmeringUnity
{
    /// <summary>
    /// PPG requires expansion power and bit 0x0100 (Shimmer3 A13 / Shimmer3R A1).
    /// Filters and the algorithm reset for each stream using the actual sample rate.
    /// </summary>
    public class ShimmerHeartRateMonitor : MonoBehaviour
    {
        [SerializeField] private ShimmerDevice shimmerDevice;
        [Header("Output:")]
        [SerializeField] private int heartRate;
        [Header("Settings:")]
        [SerializeField] private int numberOfHeartBeatsToAverage = 1;
        [Header("Info:")]
        [SerializeField] private double ppgMillivolts;
        [SerializeField] private double filteredPPG;
        [SerializeField] private long ppgSamples;
        [SerializeField] private string ppgSignal = "";
        [SerializeField] private string status = "Not streaming";

        public int HeartRate => heartRate;
        public double PPGMillivolts => ppgMillivolts;
        public long PPGSamples => ppgSamples;
        public string Status => status;

        private Filter lowPass;
        private Filter highPass;
        private PPGToHRAlgorithm algorithm;
        private bool warnedMissingSignal;
        private const int TrainingSeconds = 10;

        private void OnEnable()
        {
            if (shimmerDevice == null) return;
            shimmerDevice.OnDataRecieved.AddListener(OnDataRecieved);
            shimmerDevice.OnStateChanged.AddListener(OnStateChanged);
            if (shimmerDevice.CurrentState == ShimmerDevice.State.Streaming) ResetAlgorithm();
        }

        private void OnDisable()
        {
            if (shimmerDevice == null) return;
            shimmerDevice.OnDataRecieved.RemoveListener(OnDataRecieved);
            shimmerDevice.OnStateChanged.RemoveListener(OnStateChanged);
        }

        private void OnStateChanged(ShimmerDevice device, ShimmerDevice.State state)
        {
            if (state == ShimmerDevice.State.Streaming) ResetAlgorithm();
            else
            {
                heartRate = 0;
                algorithm = null;
                status = state.ToString();
            }
        }

        private void ResetAlgorithm()
        {
            heartRate = 0;
            ppgSamples = 0;
            warnedMissingSignal = false;
            double rate = shimmerDevice.ActualSamplingRate;
            ppgSignal = ShimmerConfig.GetPPGSignalName(shimmerDevice.IsShimmer3R);
            if (rate <= 10 || double.IsNaN(rate) || double.IsInfinity(rate))
            {
                algorithm = null;
                status = "PPG requires a sample rate above 10 Hz (51.2 Hz recommended).";
                Debug.LogWarning(status, this);
                return;
            }
            lowPass = new Filter(Filter.LOW_PASS, rate, new[] { 5.0 });
            highPass = new Filter(Filter.HIGH_PASS, rate, new[] { 0.5 });
            algorithm = new PPGToHRAlgorithm(rate, numberOfHeartBeatsToAverage, TrainingSeconds);
            status = "Training (10 seconds); attach the PPG sensor.";
        }

        private void OnDataRecieved(ShimmerDevice device, ObjectCluster objectCluster)
        {
            if (algorithm == null) return;
            SensorData ppg = objectCluster.GetData(ppgSignal, ShimmerConfiguration.SignalFormats.CAL);
            SensorData timestamp = objectCluster.GetData(ShimmerConfiguration.SignalNames.SYSTEM_TIMESTAMP, ShimmerConfiguration.SignalFormats.CAL);
            if (ppg == null || timestamp == null)
            {
                heartRate = 0;
                status = $"Missing {ppgSignal} or timestamp. Enable PPG (0x0100) and expansion power.";
                if (!warnedMissingSignal)
                {
                    Debug.LogWarning(status, this);
                    warnedMissingSignal = true;
                }
                return;
            }
            if (double.IsNaN(ppg.Data) || double.IsInfinity(ppg.Data)) return;
            ppgMillivolts = ppg.Data;
            ppgSamples++;
            filteredPPG = highPass.filterData(lowPass.filterData(ppg.Data));
            double result = algorithm.ppgToHrConversion(filteredPPG, timestamp.Data);
            bool valid = !double.IsNaN(result) && !double.IsInfinity(result) && result > 0;
            heartRate = valid ? (int)Math.Round(result) : 0;
            status = ppgSamples < device.ActualSamplingRate * TrainingSeconds
                ? "Training (10 seconds)"
                : valid ? "Receiving PPG / heart rate" : "Receiving PPG; waiting for a stable pulse";
        }
    }
}
