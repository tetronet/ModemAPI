using System.Globalization;
using ModemAPI;

namespace ModemAPI.Tests
{
    /// <summary>
    /// The default router: an address is within the local one only when it is the same or continues it after a dash.
    /// </summary>
    public class PacketRouterTests
    {
        private static PacketRouterDecidion Up(string local, string from, string to) =>
            new PacketRouter(new Address(local)).Decide(new(new Address(from), new Address(to)), PacketTransmissionDirection.FromUpperToLower);

        private static PacketRouterDecidion Down(string local, string from, string to) =>
            new PacketRouter(new Address(local)).Decide(new(new Address(from), new Address(to)), PacketTransmissionDirection.FromLowerToUpper);

        [Theory]
        // from a child of 7-1: to another child, to the address machine itself, deeper, to the neighbour network 7-10, outside
        [InlineData("7-1-5", "7-1-6", PacketRouterDecidion.ForwardInInnerNetwork)]
        [InlineData("7-1-5", "7-1", PacketRouterDecidion.ForwardInInnerNetwork)]
        [InlineData("7-1-5", "7-1-6-2", PacketRouterDecidion.ForwardInInnerNetwork)]
        [InlineData("7-1-5", "7-10-5", PacketRouterDecidion.ForwardDown)]
        [InlineData("7-1-5", "7-15", PacketRouterDecidion.ForwardDown)]
        [InlineData("7-1-5", "9-9", PacketRouterDecidion.ForwardDown)]
        // a slave can't send as somebody, who isn't under 7-1: the neighbour network 7-10 included
        [InlineData("7-10-5", "9-9", PacketRouterDecidion.AddressSpoofingDetectedRefusedToForward)]
        [InlineData("7-15", "7-1-6", PacketRouterDecidion.AddressSpoofingDetectedRefusedToForward)]
        [InlineData("9-9", "7-1-6", PacketRouterDecidion.AddressSpoofingDetectedRefusedToForward)]
        public void FromASlave(string from, string to, PacketRouterDecidion expected)
        {
            Assert.Equal(expected, Up("7-1", from, to));
        }

        [Theory]
        [InlineData("7-1-6", PacketRouterDecidion.ForwardInInnerNetwork)]
        [InlineData("7-1", PacketRouterDecidion.ForwardInInnerNetwork)]
        [InlineData("7-10-5", PacketRouterDecidion.ForwardDown)]
        [InlineData("7-15", PacketRouterDecidion.ForwardDown)]
        public void FromTheLine(string to, PacketRouterDecidion expected)
        {
            Assert.Equal(expected, Down("7-1", "9-9", to));
        }

        [Fact]
        public void EmptyLocalAddressHasEverythingWithin()
        {
            Assert.Equal(PacketRouterDecidion.ForwardInInnerNetwork, Up("", "7-1", "9-9"));
        }

        [Fact]
        public void DecisionDoesntDependOnTheCulture()
        {
            CultureInfo saved = CultureInfo.CurrentCulture;
            try
            {
                foreach (string culture in new[] { "ru-RU", "en-US", "tr-TR", "" })
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                    Assert.Equal(PacketRouterDecidion.ForwardInInnerNetwork, Up("7-1", "7-1-5", "7-1-6"));
                    Assert.Equal(PacketRouterDecidion.ForwardDown, Up("7-1", "7-1-5", "7-10-5"));
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
        }
    }
}
