namespace PrometheusNet.MongoDb.Tests;

public class EventHubTests
{
    [Fact]
    public void CanSubscribeAndPublish()
    {
        var hub = new EventHub();
        string receivedData = null;

        hub.Subscribe<string>(data => receivedData = data);
        hub.Publish("Hello");

        Assert.Equal("Hello", receivedData);
    }

    [Fact]
    public void CanUnsubscribe()
    {
        var hub = new EventHub();
        string receivedData = null;

        Action<string> handler = data => receivedData = data;

        hub.Subscribe(handler);
        hub.Unsubscribe(handler);
        hub.Publish("Hello");

        Assert.Null(receivedData);
    }

    [Fact]
    public void CanCheckExistence()
    {
        var hub = new EventHub();

        hub.Subscribe<string>(_ => { });

        Assert.True(hub.Exists<string>());
    }

    [Fact]
    public void NonExistentSubscriptionReturnsFalse()
    {
        var hub = new EventHub();

        Assert.False(hub.Exists<string>());
    }

    [Fact]
    public void MultipleSubscribersReceiveData()
    {
        var hub = new EventHub();
        string receivedData1 = null;
        string receivedData2 = null;

        hub.Subscribe<string>(data => receivedData1 = data);
        hub.Subscribe<string>(data => receivedData2 = data);
        hub.Publish("Hello");

        Assert.Equal("Hello", receivedData1);
        Assert.Equal("Hello", receivedData2);
    }

    [Fact]
    public void UnsubscribeDoesNotAffectOtherSubscribers()
    {
        var hub = new EventHub();
        string receivedData1 = null;
        string receivedData2 = null;

        Action<string> handler1 = data => receivedData1 = data;
        Action<string> handler2 = data => receivedData2 = data;

        hub.Subscribe(handler1);
        hub.Subscribe(handler2);
        hub.Unsubscribe(handler1);
        hub.Publish("Hello");

        Assert.Null(receivedData1);
        Assert.Equal("Hello", receivedData2);
    }

    [Fact]
    public async Task PublishWorksWithConcurrentSubscribeUnsubscribe()
    {
        var hub = new EventHub();
        var callCount = 0;

        Action<string> permanentHandler = _ => Interlocked.Increment(ref callCount);
        hub.Subscribe(permanentHandler);

        var subscribeTask = Task.Run(() =>
        {
            Action<string> temp = _ => { };
            for (int i = 0; i < 1000; i++)
            {
                hub.Subscribe(temp);
                hub.Unsubscribe(temp);
            }
        });

        var publishTask = Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                hub.Publish("data");
            }
        });

        await Task.WhenAll(subscribeTask, publishTask);

        Assert.True(callCount > 0);
    }
}
