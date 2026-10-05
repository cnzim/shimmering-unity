using System;
using System.Threading;
using ShimmerAPI;

namespace ShimmeringUnity
{
    /// <summary>
    /// Windows/Unity transport adapter. DiscardInBuffer cancels an outstanding async read in
    /// System.IO.Ports 5.0; the vendor reader handles TimeoutException, but not OperationCanceledException.
    /// </summary>
    internal sealed class UnityShimmerSerialPort : ShimmerLogAndStreamSystemSerialPort
    {
        public UnityShimmerSerialPort(string name, string port) : base(name, port) { }

        protected override int ReadByte()
        {
            try { return base.ReadByte(); }
            catch (OperationCanceledException exception)
            {
                if (StopReading || !SerialPort.IsOpen)
                    throw new InvalidOperationException("Serial port was closed.", exception);
                throw new TimeoutException("Serial read was interrupted by a buffer flush.", exception);
            }
            catch (ObjectDisposedException exception)
            {
                throw new InvalidOperationException("Serial port was disposed.", exception);
            }
        }

        protected override void CloseConnection()
        {
            // The API also closes at the end of ReadData; disposal must not escape that thread.
            try { if (SerialPort.IsOpen) base.CloseConnection(); }
            catch (ObjectDisposedException) { }
        }

        public void CloseSafely()
        {
            StopReading = true;
            try { base.Disconnect(); }
            finally
            {
                if (ReadThread != null && Thread.CurrentThread != ReadThread) ReadThread.Join(1500);
            }
        }
    }
}
