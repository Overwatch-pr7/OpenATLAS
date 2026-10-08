using System.Net.Sockets;
using System.Net;
using System.Threading.Tasks;
using Xunit;
using OpenATLAS.Core.Network;

namespace OpenATLAS.Tests.Network
{
    // Unit Testing for Phase 2
    // Ensures our background worker receives data correctly without tying up the main thread.
    public class UdpTelemetryListenerTests
    {
        [Fact]
        public async Task Listener_ShouldReceivePacket_AndFireEvent()
        {
            // Arrange
            int testPort = 9997; // Use a distinct port to avoid conflicts
            using var listener = new UdpTelemetryListener(testPort);
            
            bool eventFired = false;
            
            listener.OnPacketReceived += (buffer) => 
            {
                eventFired = true;
                // Verify we received the exact 256 byte buffer size
                Assert.Equal(256, buffer.Length); 
                // Verify the payload data matches what the test sent
                Assert.Equal(42, buffer[0]); 
            };
            
            // Act
            listener.Start();
            
            // Simulate Assetto Corsa broadcasting a 256-byte packet
            using (var sender = new UdpClient())
            {
                byte[] mockPacket = new byte[256];
                mockPacket[0] = 42; 
                
                var endpoint = new IPEndPoint(IPAddress.Loopback, testPort);
                await sender.SendAsync(mockPacket, mockPacket.Length, endpoint);
            }
            
            // Give the background thread a brief moment to process the UDP packet
            await Task.Delay(100);
            
            listener.Stop();
            
            // Assert
            Assert.True(eventFired, "The listener did not fire the OnPacketReceived event.");
        }
    }
}
