using NServiceBus;

namespace Repro.WebApp;

/// <summary>
/// Stands in for the customer's <c>DriverNServicebusConfiguration.ConfigEndPoint</c>.
/// Their custom code (SendGrid, Redis, callback queues, topology loading) is
/// deliberately left out - none of it is needed to reproduce the log file.
/// </summary>
static class ReproNServiceBusConfiguration
{
    public static EndpointConfiguration ConfigEndPoint(AppSettings appSettings)
    {
        var endpointConfiguration = new EndpointConfiguration(appSettings.EndpointName);

        if (string.IsNullOrWhiteSpace(appSettings.AzureWebJobsServiceBus))
        {
            // No Azure resources required. The rolling file logger is transport
            // agnostic, so this reproduces the same behaviour as Azure Service Bus.
            endpointConfiguration.UseTransport(new LearningTransport());
        }
        else
        {
            endpointConfiguration.UseTransport(
                new AzureServiceBusTransport(appSettings.AzureWebJobsServiceBus, TopicTopology.Default));
        }

        endpointConfiguration.UseSerialization<NewtonsoftJsonSerializer>();
        endpointConfiguration.SendFailedMessagesTo($"{appSettings.EndpointName}.error");
        endpointConfiguration.EnableInstallers();

        return endpointConfiguration;
    }
}
