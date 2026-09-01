using NServiceBus;

namespace Repro.WebApp;

public class Ping : ICommand
{
    public string Text { get; set; } = "ping";
}

public class PingHandler(ILogger<PingHandler> logger) : IHandleMessages<Ping>
{
    public Task Handle(Ping message, IMessageHandlerContext context)
    {
        // Logging from inside a handler runs inside the endpoint's log slot, so it
        // goes to the host's providers. This is the path that already works for the
        // customer, and it is why they see NServiceBus entries in App Insights.
        logger.LogInformation("Handled Ping: {Text}", message.Text);
        return Task.CompletedTask;
    }
}
