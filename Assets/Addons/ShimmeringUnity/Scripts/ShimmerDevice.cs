using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using ShimmerAPI;
using System;
using System.Threading;
using UnityEngine.Events;

namespace ShimmeringUnity
{

    /// <summary>
    /// Handles connection and streaming from Shimmer3 and Shimmer3R devices
    /// </summary>
    public class ShimmerDevice : MonoBehaviour
    {

        /// <summary>
        /// The possible states of the shimmer device
        /// </summary>
        public enum State
        {
            /// <summary>
            /// No state, device not connected.
            /// </summary>
            None,
            /// <summary>
            /// Device is currently connecting via bluetooth.
            /// </summary>
            Connecting,
            /// <summary>
            /// Device has sucessfully connected.
            /// </summary>
            Connected,
            /// <summary>
            /// Device has been disconnected.
            /// </summary>
            Disconnected,
            /// <summary>
            /// Device is currently streaming (and connected).
            /// </summary>
            Streaming
        }

        /// <summary>
        /// The current state of this shimmer device
        /// </summary>
        /// <value></value>
        public State CurrentState { get; private set; }

        //Inspector & Public members

        [Header("Configuration")]

        [SerializeField]
        [Tooltip("The dev name for this device.")]
        private string devName = "";

        public string DevName
        {
            get => devName;
            set => devName = value;
        }

        [SerializeField]
        [Tooltip("The communication port this device is connected to.")]
        private string comPort = "COM8";

        public string COMPort
        {
            get => comPort;
            set => comPort = value;
        }

        [SerializeField]
        [Tooltip("The sampling rate for the device (default 51.2Hz).")]
        private float samplingRate = 51.2f;
        public float SamplingRate
        {
            get => samplingRate;
            set => samplingRate = value;
        }

        [SerializeField]
        [Tooltip("Select the sensors you want to enable during connection.")]
        private ShimmerConfig.SensorBitmap enabledSensors;

        public ShimmerConfig.SensorBitmap EnabledSensors
        {
            get => enabledSensors;
            set => enabledSensors = value;
        }

        [SerializeField]
        [Tooltip("The range for the accelerometer.")]
        private ShimmerConfig.AccelerometerRange accelerometerRange;

        public ShimmerConfig.AccelerometerRange AccelerometerRange
        {
            get => accelerometerRange;
            set => accelerometerRange = value;
        }

        [SerializeField]
        [Tooltip("The range for the GSR.")]
        private ShimmerConfig.GSRRange gsrRange;

        public ShimmerConfig.GSRRange GSRRange
        {
            get => gsrRange;
            set => gsrRange = value;
        }

        [SerializeField]
        [Tooltip("The range for the gyroscope.")]
        private ShimmerConfig.GyroscopeRange gyroscopeRange;

        public ShimmerConfig.GyroscopeRange GyroscopeRange
        {
            get => gyroscopeRange;
            set => gyroscopeRange = value;
        }

        [SerializeField]
        [Tooltip("The range for the magnetometer.")]
        private ShimmerConfig.MagnetometerRange magnetometerRange;

        public ShimmerConfig.MagnetometerRange MagnetometerRange
        {
            get => magnetometerRange;
            set => magnetometerRange = value;
        }

        [SerializeField]
        private bool enableLowPowerAccel = false;

        public bool EnableLowPowerAccel
        {
            get => enableLowPowerAccel;
            set => enableLowPowerAccel = value;
        }

        [SerializeField]
        private bool enableLowPowerGyro = false;

        public bool EnableLowPowerGyro
        {
            get => enableLowPowerGyro;
            set => enableLowPowerGyro = value;
        }

        [SerializeField]
        private bool enableLowPowerMag = false;

        public bool EnableLowPowerMag
        {
            get => enableLowPowerMag;
            set => enableLowPowerMag = value;
        }

        [SerializeField]
        [Tooltip("Enables the internal ADC pins on the shimmer3.")]
        private bool enableInternalExpPower = true;

        public bool EnableInternalExpPower
        {
            get => enableInternalExpPower;
            set => enableInternalExpPower = value;
        }

        [SerializeField]
        [Tooltip("Data recieved event.")]
        private DataRecievedEvent onDataRecieved = new DataRecievedEvent();

        public DataRecievedEvent OnDataRecieved => onDataRecieved;

        [SerializeField]
        [Tooltip("Device state changed event.")]
        private StateChangeEvent onStateChanged = new StateChangeEvent();

        public StateChangeEvent OnStateChanged => onStateChanged;

        [SerializeField, Tooltip("Apply the Inspector settings after hardware detection. Disable to read the device's existing configuration.")]
        private bool applyConfigurationOnConnect = true;
        public bool ApplyConfigurationOnConnect { get => applyConfigurationOnConnect; set => applyConfigurationOnConnect = value; }

        public int HardwareVersion { get; private set; } = -1;
        public bool IsShimmer3R => HardwareVersion == (int)ShimmerBluetooth.ShimmerVersion.SHIMMER3R;
        public string FirmwareVersion { get; private set; } = "";
        public double ActualSamplingRate { get; private set; }
        public long ActualEnabledSensors { get; private set; }
        public string LastError { get; private set; } = "";
        public string LastNotification { get; private set; } = "";
        public long ReceivedPackets { get; private set; }
        public long DroppedPackets => session == null ? 0 : Interlocked.Read(ref session.DroppedPackets);
        public int PendingPackets => session == null ? 0 : session.Data.Count;

        // Each connection owns its queues and API instance. Old callbacks cannot affect a new connection.
        private sealed class Session
        {
            public UnityShimmerSerialPort Device;
            public Thread Worker;
            public EventHandler Callback;
            public readonly ManualResetEventSlim ConnectionReady = new ManualResetEventSlim(false);
            public readonly System.Collections.Concurrent.BlockingCollection<Action> Commands =
                new System.Collections.Concurrent.BlockingCollection<Action>();
            public readonly System.Collections.Concurrent.ConcurrentQueue<State> States =
                new System.Collections.Concurrent.ConcurrentQueue<State>();
            public readonly System.Collections.Concurrent.ConcurrentQueue<string> Messages =
                new System.Collections.Concurrent.ConcurrentQueue<string>();
            public readonly System.Collections.Concurrent.ConcurrentQueue<string> Notifications =
                new System.Collections.Concurrent.ConcurrentQueue<string>();
            public readonly System.Collections.Concurrent.ConcurrentQueue<ObjectCluster> Data =
                new System.Collections.Concurrent.ConcurrentQueue<ObjectCluster>();
            public volatile bool Stopping;
            public volatile bool Configured;
            public long DroppedPackets;
        }

        private Session session;

        private void Update()
        {
            var current = session;
            if (current == null) return;
            while (current.Notifications.TryDequeue(out string notification))
            {
                LastNotification = notification;
                Debug.Log($"Shimmer {devName}: {notification}", this);
            }
            while (current.Messages.TryDequeue(out string message))
            {
                LastError = message;
                Debug.LogWarning($"Shimmer {devName} ({comPort}): {message}", this);
            }
            while (current.States.TryDequeue(out State state))
            {
                if (state == State.Connected)
                {
                    HardwareVersion = current.Device.GetShimmerVersion();
                    FirmwareVersion = current.Device.GetFirmwareVersionFullName();
                    ActualSamplingRate = current.Device.GetSamplingRate();
                    ActualEnabledSensors = current.Device.GetEnabledSensors();
                    Debug.Log($"Shimmer {devName}: {(ShimmerBluetooth.ShimmerVersion)HardwareVersion}, {FirmwareVersion}, {ActualSamplingRate:F2} Hz, sensors 0x{ActualEnabledSensors:X6}", this);
                }
                SetState(state);
            }
            // Drain more than one sample per frame; the bound also keeps high-rate streams responsive.
            for (int i = 0; i < 512 && current.Data.TryDequeue(out ObjectCluster data); i++)
            {
                if (CurrentState != State.Streaming) continue;
                ReceivedPackets++;
                OnDataRecieved.Invoke(this, data);
            }
        }

        private void SetState(State state)
        {
            if (CurrentState == state) return;
            CurrentState = state;
            OnStateChanged.Invoke(this, state);
        }

        private void OnDisable() => Shutdown();
        private void OnDestroy() => Shutdown();
        private void OnApplicationQuit() => Shutdown();

        /// <summary>Connect through Bluetooth Classic's COM port, detect hardware, then configure it.</summary>
        public void Connect()
        {
            if (!isActiveAndEnabled || (session != null && session.Worker.IsAlive)) return;
            if (string.IsNullOrWhiteSpace(comPort) || samplingRate <= 0 || float.IsNaN(samplingRate) || float.IsInfinity(samplingRate))
            {
                LastError = "Enter a COM port and a positive, finite sampling rate.";
                Debug.LogWarning(LastError, this);
                return;
            }

            // Snapshot Unity's serialized settings on the main thread before starting the worker.
            var current = new Session { Device = new UnityShimmerSerialPort(devName, comPort.Trim()) };
            int sensors = (int)enabledSensors;
            // Avoid float serialization noise truncating the API's sampling divisor (51.2f -> 639 instead of 640).
            double rate = Math.Round((double)samplingRate, 4);
            int accel = (int)accelerometerRange, gsr = (int)gsrRange, gyro = (int)gyroscopeRange, mag = (int)magnetometerRange;
            bool apply = applyConfigurationOnConnect, power = enableInternalExpPower;
            bool lowAccel = enableLowPowerAccel, lowGyro = enableLowPowerGyro, lowMag = enableLowPowerMag;
            current.Callback = (sender, args) => HandleEvent(current, args);
            current.Device.UICallback += current.Callback;
            current.Worker = new Thread(() =>
            {
                try
                {
                    current.Device.Connect();
                    if (!current.ConnectionReady.Wait(TimeSpan.FromSeconds(30)))
                        throw new TimeoutException("Device initialization timed out. Check pairing, COM port and LogAndStream firmware.");
                    if (current.Stopping) return;
                    if (!current.Device.IsConnected())
                        throw new InvalidOperationException("Unable to connect. Check that the sensor is powered on and the COM port is not occupied.");
                    int hardware = current.Device.GetShimmerVersion();
                    if (hardware != (int)ShimmerBluetooth.ShimmerVersion.SHIMMER3 && hardware != (int)ShimmerBluetooth.ShimmerVersion.SHIMMER3R)
                        throw new NotSupportedException("This Unity wrapper supports Shimmer3 and Shimmer3R with LogAndStream firmware.");
                    if (current.Device.GetFirmwareIdentifier() != ShimmerBluetooth.FW_IDENTIFIER_LOGANDSTREAM)
                        throw new NotSupportedException("LogAndStream firmware is required.");
                    if (apply)
                    {
                        // Configure only selected sensors. Shimmer3R's LIS2MDL has no legacy mag gain setting.
                        current.Device.WriteSamplingRate(rate);
                        if (current.Stopping) return;
                        if ((sensors & (int)ShimmerConfig.SensorBitmap.SENSOR_D_ACCEL) != 0) current.Device.WriteAccelRange(accel);
                        if ((sensors & (int)ShimmerConfig.SensorBitmap.SENSOR_GSR) != 0) current.Device.WriteGSRRange(gsr);
                        if ((sensors & (int)ShimmerConfig.SensorBitmap.SENSOR_MPU9150_GYRO) != 0) current.Device.WriteGyroRange(gyro);
                        if ((sensors & (int)ShimmerConfig.SensorBitmap.SENSOR_LSM303DLHC_MAG) != 0 && hardware == (int)ShimmerBluetooth.ShimmerVersion.SHIMMER3)
                            current.Device.WriteMagRange(mag);
                        if ((sensors & (int)ShimmerConfig.SensorBitmap.SENSOR_D_ACCEL) != 0) current.Device.SetLowPowerAccel(lowAccel);
                        if ((sensors & (int)ShimmerConfig.SensorBitmap.SENSOR_MPU9150_GYRO) != 0) current.Device.SetLowPowerGyro(lowGyro);
                        if ((sensors & (int)ShimmerConfig.SensorBitmap.SENSOR_LSM303DLHC_MAG) != 0) current.Device.SetLowPowerMag(lowMag);
                        if ((sensors & 0x180018) != 0)
                            current.Device.WriteEXGConfigurations(Shimmer3Configuration.EXG_EMG_CONFIGURATION_CHIP1, Shimmer3Configuration.EXG_EMG_CONFIGURATION_CHIP2);
                        current.Device.WriteInternalExpPower(power ? 1 : 0);
                        if (current.Stopping) return;
                        current.Device.WriteSensors(sensors);
                        if (current.Stopping) return;
                        if (!current.Device.IsConnected()) throw new InvalidOperationException("Connection lost during configuration.");
                        if (current.Device.GetEnabledSensors() != sensors)
                            throw new InvalidOperationException("The device did not accept the selected sensors. Check the expansion module and sensor conflicts.");
                    }
                    current.Configured = true;
                    current.States.Enqueue(State.Connected);
                    while (!current.Stopping)
                    {
                        if (current.Commands.TryTake(out Action command, 100)) command();
                    }
                }
                catch (Exception exception)
                {
                    if (!current.Stopping) current.Messages.Enqueue(exception.Message);
                }
                finally
                {
                    current.Stopping = true;
                    current.Device.UICallback -= current.Callback;
                    try { current.Device.CloseSafely(); }
                    catch (Exception exception) { current.Messages.Enqueue(exception.Message); }
                    try { current.Device.SerialPort.Dispose(); }
                    catch (Exception exception) { current.Messages.Enqueue(exception.Message); }
                    current.States.Enqueue(State.Disconnected);
                }
            }) { IsBackground = true, Name = "Shimmer " + comPort };
            session = current;
            HardwareVersion = -1;
            FirmwareVersion = "";
            ActualSamplingRate = 0;
            ActualEnabledSensors = 0;
            ReceivedPackets = 0;
            LastError = "";
            LastNotification = "";
            current.Worker.Start();
            SetState(State.Connecting);
        }

        public void StartStreaming()
        {
            var current = session;
            if (current == null || current.Stopping || CurrentState != State.Connected) return;
            current.Commands.Add(() =>
            {
                if (current.Device.GetState() == ShimmerBluetooth.SHIMMER_STATE_CONNECTED) current.Device.StartStreaming();
            });
        }

        public void StopStreaming()
        {
            var current = session;
            if (current == null || current.Stopping || CurrentState != State.Streaming) return;
            current.Commands.Add(() =>
            {
                if (current.Device.GetState() == ShimmerBluetooth.SHIMMER_STATE_STREAMING) current.Device.StopStreaming();
            });
        }

        public void Disconnect()
        {
            var current = session;
            if (current == null || current.Stopping) return;
            // Close during an unfinished handshake to interrupt blocking reads. Otherwise stop streaming first.
            if (!current.Configured)
            {
                current.Stopping = true;
                current.ConnectionReady.Set();
                try { current.Device.SerialPort.Close(); } catch (Exception exception) { current.Messages.Enqueue(exception.Message); }
            }
            else current.Commands.Add(() =>
            {
                try
                {
                    if (current.Device.GetState() == ShimmerBluetooth.SHIMMER_STATE_STREAMING) current.Device.StopStreaming();
                }
                finally { current.Stopping = true; }
            });
        }

        /// <summary>Kept for existing callers; cancels cooperatively instead of aborting a thread.</summary>
        public void ForceAbortThread() => Shutdown();

        private void Shutdown()
        {
            var current = session;
            if (current == null || !current.Worker.IsAlive) return;
            Disconnect();
            if (!current.Worker.Join(1500))
            {
                current.Stopping = true;
                current.ConnectionReady.Set();
                try { current.Device.SerialPort.Close(); } catch (Exception exception) { current.Messages.Enqueue(exception.Message); }
                if (!current.Worker.Join(3000)) Debug.LogWarning("Shimmer worker is still finishing; reconnect is blocked until it exits.", this);
            }
            SetState(State.Disconnected);
        }

        private static void HandleEvent(Session current, EventArgs args)
        {
            if (!(args is CustomEventArgs eventArgs)) return;
            if (current.Stopping && eventArgs.getIndicator() != (int)ShimmerBluetooth.ShimmerIdentifier.MSG_IDENTIFIER_NOTIFICATION_MESSAGE) return;
            switch (eventArgs.getIndicator())
            {
                case (int)ShimmerBluetooth.ShimmerIdentifier.MSG_IDENTIFIER_STATE_CHANGE:
                    int state = (int)eventArgs.getObject();
                    if (state == ShimmerBluetooth.SHIMMER_STATE_CONNECTED)
                    {
                        current.ConnectionReady.Set();
                        if (current.Configured) current.States.Enqueue(State.Connected);
                    }
                    else if (state == ShimmerBluetooth.SHIMMER_STATE_NONE)
                    {
                        current.ConnectionReady.Set();
                        if (current.Configured)
                        {
                            current.Stopping = true;
                            current.Messages.Enqueue("Connection lost. Check sensor power and Bluetooth pairing.");
                        }
                        // The worker publishes Disconnected after releasing the port and joining the reader.
                    }
                    else if (state == ShimmerBluetooth.SHIMMER_STATE_STREAMING) current.States.Enqueue(State.Streaming);
                    break;
                case (int)ShimmerBluetooth.ShimmerIdentifier.MSG_IDENTIFIER_NOTIFICATION_MESSAGE:
                    if (eventArgs.getMinorIndication() == (int)ShimmerLogAndStream.ShimmerSDBTMinorIdentifier.MSG_ERROR ||
                        eventArgs.getMinorIndication() == (int)ShimmerLogAndStream.ShimmerSDBTMinorIdentifier.MSG_WARNING)
                        current.Messages.Enqueue(Convert.ToString(eventArgs.getObject()));
                    else current.Notifications.Enqueue(Convert.ToString(eventArgs.getObject()));
                    break;
                case (int)ShimmerBluetooth.ShimmerIdentifier.MSG_IDENTIFIER_DATA_PACKET:
                    if (current.Data.Count >= 4096 && current.Data.TryDequeue(out _)) Interlocked.Increment(ref current.DroppedPackets);
                    current.Data.Enqueue((ObjectCluster)eventArgs.getObject());
                    break;
            }
        }
    }

    // Preserve the existing event types and serialized event field names.
    [Serializable]
    public class DataRecievedEvent : UnityEvent<ShimmerDevice, ObjectCluster> { }

    [Serializable]
    public class StateChangeEvent : UnityEvent<ShimmerDevice, ShimmerDevice.State> { }
}
