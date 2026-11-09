using Shorty.Core.Analytics;

namespace Shorty.Core.Tests;

public sealed class BrokerConnectionTests
{
    [Theory]
    [InlineData("Endpoint=sb://localhost;SharedAccessKeyName=k;SharedAccessKey=v;UseDevelopmentEmulator=true;", true)]
    [InlineData("endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=v", true)]
    [InlineData("shorty-dev-ns.servicebus.windows.net", false)]
    public void EventHubsSetting_IsTold_ConnectionStringFromNamespaceAddress(string setting, bool expected) =>
        Assert.Equal(expected, BrokerConnection.IsEventHubsConnectionString(setting));

    [Theory]
    [InlineData("https://stshortydev.blob.core.windows.net", true)]
    [InlineData("HTTPS://stshortydev.blob.core.windows.net", true)]
    [InlineData("UseDevelopmentStorage=true", false)]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=a;AccountKey=k;EndpointSuffix=core.windows.net", false)]
    public void BlobSetting_IsTold_AddressFromConnectionString(string setting, bool expected) =>
        Assert.Equal(expected, BrokerConnection.IsBlobAddress(setting));
}
