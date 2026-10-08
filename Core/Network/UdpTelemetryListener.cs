using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace OpenATLAS.Core.Network
{
    // Task 2.2: Background Listener Worker
    // Captures live packets from the sim on a background thread without freezing the main application.
    public class UdpTelemetryListener : IDisposable
    {
        private readonly int _port;
        private UdpClient _udpClient;
        private CancellationTokenSource _cts;
        
        // We pre-allocate a 256-byte buffer exactly once. 
        // This avoids memory allocations (and thus garbage collection freezes) in the 60Hz loop.
        private readonly byte[] _buffer = new byte[256];
        
        public event Action<byte[]> OnPacketReceived;

        public UdpTelemetryListener(int port = 9996)
        {
            _port = port;
        }

        public void Start()
        {
            _udpClient = new UdpClient(_port);
            _cts = new CancellationTokenSource();
            
            // Run the continuous listening loop in a background thread
            Task.Run(() => ListenLoop(_cts.Token), _cts.Token);
        }

        private void ListenLoop(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    // Using normal `UdpClient.Receive()` creates a new byte[] for every packet.
                    // Instead, we access the underlying Socket to read bytes directly into
                    // our pre-allocated `_buffer`. This keeps our memory footprint at strictly zero.
                    int bytesRead = _udpClient.Client.Receive(_buffer);
                    
                    if (bytesRead > 0)
                    {
                        OnPacketReceived?.Invoke(_buffer);
                    }
                }
            }
            catch (SocketException)
            {
                // Expected when the socket is closed while waiting for a packet
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            _udpClient?.Close();
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
            _udpClient?.Dispose();
        }
    }
}
