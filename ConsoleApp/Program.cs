using System;
using OpenATLAS.Core.Network;

namespace OpenATLAS.ConsoleApp
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("==================================================");
            Console.WriteLine("    OpenATLAS Live Telemetry Monitor (Testing)    ");
            Console.WriteLine("==================================================");
            Console.WriteLine("\nWaiting for Assetto Corsa Competizione Shared Memory (`acpmf_physics`)...");
            Console.WriteLine("If you drive a lap, you should see your live speed and throttle below!\n");

            using var listener = new SharedMemoryListener();
            
            int packetCount = 0;
            
            listener.OnTelemetryUpdated += (packet) => 
            {
                packetCount++;
                // Print roughly once a second (60Hz / 60)
                if (packetCount % 60 == 0) 
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Speed: {packet.SpeedKmh,6:F1} km/h | Gear: {packet.Gear} | RPM: {packet.Rpms,5} | Throttle: {packet.Gas * 100,5:F0}%");
                }
            };
            
            listener.Start();
            
            Console.WriteLine("Press [ENTER] to stop listening and exit.\n");
            Console.ReadLine();
        }
    }
}
