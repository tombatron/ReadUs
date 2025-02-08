using System;
using System.Threading.Tasks;
using Tombatron.Results;
using Cons = System.Console;

namespace ReadUs.Console;

public static class PubSubScenario
{
    public static async Task Run()
    {
        var connectionString = new Uri("redis://192.168.86.40:6379?connectionsPerNode=5");
        
        using var redis = RedisConnectionPool.Create(connectionString);

        using var subscriber = redis.CreateSubscriber();

        // PSUBSCRIBE
        subscriber.PatternSubscribe("h?llo"); // async?
        subscriber.PatternUnsubscribe("h?llo");

        // RESET
        subscriber.Reset();
        
        // SSUBSCRIBE
        var subResult = subscriber.ShardSubscribe("hello"); // Can be more than one, but must be at least one.
        
        if (subResult is Ok)
        {
            Cons.WriteLine("Successfully connected to a sharded subscription.");
        }

        if (subResult is Error err)
        {
            Cons.WriteLine($"Error subscribing to a sharded subscription: {err.Message}");
        }
        
        // SUNSUBSCRIBE
        var unsubResult = subscriber.SharedUnsubscribe("hello");

        if (unsubResult is Ok)
        {
            Cons.WriteLine("Successfully unsubscribed from a sharded subscription.");
        }

        if (unsubResult is Error err2)
        {
            Cons.WriteLine("Error unsubscribing from a sharded subscription.");
        }
        
        // SUBSCRIBE
        subscriber.Subscribe("hello"); // Can be more than one, but must be at least one. 
        
        // UNSUBSCRIBE
        subscriber.Unsubscribe("hello");
        
        //RedisSubscribtionEventArgs?
        subscriber.MessageReceived += (_, subscriptionsArgs) =>
        {
            // Handle the message here. 
        };
        
        // On dispose we'll unsubscribe and then kill the connection. 
    }
}