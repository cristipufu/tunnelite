using Xunit;

namespace Tunnelite.Tests
{
    public class TunnelTests
    {
        [Fact]
        public void TestTunnelConnection()
        {
            // Arrange
            var tunnel = new Tunnel();

            // Act & Assert
            Assert.NotNull(tunnel);
        }

        [Fact]
        public async Task TestTunnelStartStop()
        {
            // Arrange
            var tunnel = new Tunnel();

            // Act & Assert
            await tunnel.StartAsync();
            Assert.True(tunnel.IsRunning);
            
            await tunnel.StopAsync();
            Assert.False(tunnel.IsRunning);
        }
    }
}