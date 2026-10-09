using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OpenATLAS.Core.Models;

namespace OpenATLAS.Core.Network
{
    // Task 2.2 & 2.3: Shared Memory Listener & Marshaling
    public class SharedMemoryListener : IDisposable
    {
        private MemoryMappedFile _mmf;
        private CancellationTokenSource _cts;
        
        public event Action<AcPhysicsPacket> OnTelemetryUpdated;

        public void Start()
        {
            _cts = new CancellationTokenSource();
            Task.Run(() => ListenLoop(_cts.Token), _cts.Token);
        }

        private void ListenLoop(CancellationToken token)
        {
            try
            {
                // Assetto Corsa Competizione uses the exact same memory layout as the original game!
                _mmf = MemoryMappedFile.OpenExisting("Local\\acpmf_physics");
                
                using var accessor = _mmf.CreateViewAccessor();
                
                // We don't need to read the whole file, just the size of our explicit struct
                int structSize = Marshal.SizeOf(typeof(AcPhysicsPacket));

                while (!token.IsCancellationRequested)
                {
                    // Task 2.3: Struct Marshaling
                    // Read the raw unmanaged memory directly into our C# struct!
                    // This is blazing fast and allocates zero memory.
                    accessor.Read(0, out AcPhysicsPacket packet);
                    
                    OnTelemetryUpdated?.Invoke(packet);
                    
                    // ACC Physics runs at 333Hz. Updating our UI at 60Hz (16ms) is plenty.
                    Thread.Sleep(16); 
                }
            }
            catch (Exception)
            {
                // If ACC isn't running yet, OpenExisting will throw a FileNotFoundException.
                Console.WriteLine("\n[ERROR] ACC Shared Memory not found. Is the game running?");
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            _mmf?.Dispose();
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
            _mmf?.Dispose();
        }
    }
}
