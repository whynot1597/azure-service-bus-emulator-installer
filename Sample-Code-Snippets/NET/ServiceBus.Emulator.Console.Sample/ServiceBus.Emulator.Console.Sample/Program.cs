using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using System.Text;

internal class Program
{
    private static string _connectionString = "";
    private static string _adminApiConnectionString = "";
    private static ServiceBusAdministrationClient _adminClient = default!;

    const int numOfBatches = 3;
    const int numOfMessagesPerBatch = 3;

    public static async Task Main(string[] args)
    {
        Console.WriteLine("Service Bus Emulator .NET Sample");

        await WaitForServiceBusEmulatorToBeHealthy();

        UpdateConnectionStringFromEnvironmentVariables();

	    _adminClient = new ServiceBusAdministrationClient(_adminApiConnectionString);

        //Case 1 : Send and Receive from a Queue
        Console.WriteLine("Running Case 1: Send Receive from a Queue");
        await SendAndReceiveFromQueue();

        //Case 2: Send and Receive from a Topic
        Console.WriteLine("Running Case 2: Send Receive from a Topic with 4 Subscription with varying correlation filters");
        await SendAndReceiveFromTopic();

        Console.WriteLine("Press enter key to exit.");
        Console.ReadLine();  
    }

    private static async Task WaitForServiceBusEmulatorToBeHealthy()
    {
        Console.WriteLine($"Waiting for Service Bus Emulator to be healthy...");
        string hostName = Environment.GetEnvironmentVariable("EMULATOR_HOST") ?? "localhost";
        string port = Environment.GetEnvironmentVariable("EMULATOR_HTTP_PORT") ?? "5300";
        string healthUrl = $"http://{hostName}:{port}/health";

        using (var httpClient = new HttpClient())
        {
            while (true)
            {
                // HTTP GET on http://EMULATOR_HOST:EMULATOR_HTTP_PORT/health should return 200 OK when the emulator is healthy
                try
                {
                    var response = await httpClient.GetAsync(healthUrl);
                    if (response.IsSuccessStatusCode)
                    {
                        Console.WriteLine("Service Bus Emulator is healthy.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    // Ignore exceptions and continue waiting
                    Console.WriteLine($"Service Bus Emulator is not healthy yet. Continuing to wait...{ex.Message}");
                }
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
    }

    private static void UpdateConnectionStringFromEnvironmentVariables()
    {
        string hostName = Environment.GetEnvironmentVariable("EMULATOR_HOST") ?? "localhost";
        string port = Environment.GetEnvironmentVariable("EMULATOR_HTTP_PORT") ?? "5300";

        _connectionString = $"Endpoint=sb://{hostName};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
        _adminApiConnectionString = $"Endpoint=sb://{hostName}:{port};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

        if (port == "80")
        {
            _adminApiConnectionString = _connectionString;
        }

        Console.WriteLine($"Runtime ConnectionString: {_connectionString}");
        Console.WriteLine($"Admin API ConnectionString: {_adminApiConnectionString}");
    }

    private static async Task SendAndReceiveFromQueue()
    {
        string queueName = $"queue-{Guid.NewGuid()}";
        await CreateQueueAsync(queueName);

        try
        {
            await PublishMessageToQueue(queueName);
            await ConsumeMessageFromQueue(queueName);
        }
        finally
        {
            await DeleteQueueIfExistsAsync(queueName);
        }
    }

    private static async Task SendAndReceiveFromTopic()
    {
        string topicName = $"topic-{Guid.NewGuid()}";
        List<string> subscriptions = await CreateTopicSubscriptionAndRulesAsync(topicName);
        if (subscriptions.Count == 0)
        {
            throw new InvalidOperationException($"No subscriptions were provisioned for topic '{topicName}'.");
        }

        try
        {
            await PublishMessageToTopic(topicName);
            await ConsumeMessageFromTopic(topicName, subscriptions);
        }
        finally
        {
            await DeleteTopicHierarchyAsync(topicName, subscriptions);
        }
    }

    public static async Task PublishMessageToQueue(string queueName = "queue.1") 
    {
        var client = new ServiceBusClient(_connectionString);
        var sender = client.CreateSender(queueName);

        for (int i = 1; i <= numOfBatches; i++)
        {
            using ServiceBusMessageBatch messageBatch = await sender.CreateMessageBatchAsync();

            for (int j = 1; j <= numOfMessagesPerBatch; j++)
            {
                messageBatch.TryAddMessage(new ServiceBusMessage($"Batch:{i}:Message:{j}"));
            }
            await sender.SendMessagesAsync(messageBatch);
        }

        await sender.DisposeAsync();
        await client.DisposeAsync();

        Console.WriteLine($"{numOfBatches} batches with {numOfMessagesPerBatch} messages per batch has been published to the queue.");
    }

    public static async Task ConsumeMessageFromQueue(string queueName = "queue.1")
    {
        var client = new ServiceBusClient(_connectionString);

        ServiceBusReceiverOptions opt = new ServiceBusReceiverOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
        };

        ServiceBusReceiver receiver = client.CreateReceiver(queueName, opt);

        while (true)
        {
            ServiceBusReceivedMessage message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
            if (message != null)
            {
                // Process the message
                Console.WriteLine($"\tReceived message: {Encoding.UTF8.GetString(message.Body)}");

                // Complete the message to remove it from the queue
                await receiver.CompleteMessageAsync(message);
            }
            else
            {
                Console.WriteLine("No messages received.");
                break;
            }
        }

        Console.WriteLine("Done receiving.");

        await receiver.DisposeAsync();
        await client.DisposeAsync();
    }


    public static async Task PublishMessageToTopic(string topicName = "topic.1")
    {
        await using (var client = new ServiceBusClient(_connectionString))
        {
            ServiceBusSender sender = client.CreateSender(topicName);
            int messageCount = 0;

            //First 10 message will goto Subscription 1 and Subscription 3 as per set filters in Config.json
            for (; messageCount < numOfMessagesPerBatch; messageCount++)
            {
                ServiceBusMessage message = new ServiceBusMessage(Encoding.UTF8.GetBytes($"Message number : {messageCount}"))
                {
                    ContentType = "application/text",
                    //CorrelationId = "id1",
                    //Subject = "subject1",
                    //MessageId = "msgid1",
                    //ReplyTo = "someQueue",
                    //ReplyToSessionId = "sessionId",
                    //SessionId = "session1",
                    //To = "xyz"
                };

                await sender.SendMessageAsync(message);
            }

            //Next 10 message will goto Subscription 2 and Subscription 3 as per set filters  in Config.json
            for (; messageCount < numOfMessagesPerBatch * 2; messageCount++)
            {
                ServiceBusMessage message = new ServiceBusMessage(Encoding.UTF8.GetBytes($"Message number : {messageCount}"));
                message.ApplicationProperties.Add("prop1", "value1");

                await sender.SendMessageAsync(message);
            }

            //Next 10 message will goto Subscription 3 and Subscription 4 as per set filters  in Config.json
            for (; messageCount < numOfMessagesPerBatch * 3; messageCount++)
            {
                ServiceBusMessage message = new ServiceBusMessage(Encoding.UTF8.GetBytes($"Message number : {messageCount}"))
                {
                    MessageId = "123456"
                };
                message.ApplicationProperties.Add("userProp1", "value1");

                await sender.SendMessageAsync(message);
            }

            Console.WriteLine($"Sent {messageCount} messages to the topic: {topicName}");
        }
    }

    public static async Task ConsumeMessageFromTopic(string topicName = "topic.1", List<string>? subscriptions = null)
    {
        if (subscriptions == null || subscriptions.Count == 0)
        {
            throw new ArgumentException("At least one subscription is required to consume topic messages.", nameof(subscriptions));
        }
        foreach (string subscription in subscriptions) {
            Console.WriteLine($"Starting to receive messages from {subscription}");
            await ConsumeMessageFromSubscription(topicName, subscription);
        }
    }

    private static async Task ConsumeMessageFromSubscription(string topicName,string subscriptionName)
    {
        Console.WriteLine($"Rcv_Sub {subscriptionName} Begin");

        //  Receive on Sub 1
        var client1 = new ServiceBusClient(_connectionString);
        var opt1 = new ServiceBusProcessorOptions();
        opt1.ReceiveMode = ServiceBusReceiveMode.PeekLock;
        var processor1 = client1.CreateProcessor(topicName, subscriptionName, opt1);

        processor1.ProcessMessageAsync += MessageHandler;
        processor1.ProcessErrorAsync += ErrorHandler;

        await processor1.StartProcessingAsync();

        await Task.Delay(TimeSpan.FromSeconds(5));

        await processor1.StopProcessingAsync();
        await processor1.DisposeAsync();
        await client1.DisposeAsync();
        Console.WriteLine($"Rcv_Sub {subscriptionName} End");
    }

    private static async Task MessageHandler(ProcessMessageEventArgs args)
    {
        string body = args.Message.Body.ToString();
        Console.WriteLine($"\tReceived message: SequenceNumber:{args.Message.SequenceNumber} Body:{body} To:{args.Message.To}");
        await args.CompleteMessageAsync(args.Message);
    }

    private static Task ErrorHandler(ProcessErrorEventArgs args)
    {
        Console.WriteLine($"Message handler encountered an exception {args.Exception}.");
        return Task.CompletedTask;
    }

    private static async Task CreateQueueAsync(string queueName)
    {
        await _adminClient.CreateQueueAsync(queueName);
        Console.WriteLine($"Created queue '{queueName}'.");
    }

    private static async Task DeleteQueueIfExistsAsync(string queueName)
    {
        if (await _adminClient.QueueExistsAsync(queueName))
        {
            await _adminClient.DeleteQueueAsync(queueName);
            Console.WriteLine($"Deleted queue '{queueName}'.");
        }
    }

    private static async Task<List<string>> CreateTopicSubscriptionAndRulesAsync(string topicName)
    {

        var topicOptions = new CreateTopicOptions(topicName)
        {
            DefaultMessageTimeToLive = TimeSpan.FromHours(1),
            DuplicateDetectionHistoryTimeWindow = TimeSpan.FromSeconds(20),
            RequiresDuplicateDetection = false
        };

        await _adminClient.CreateTopicAsync(topicOptions);
        Console.WriteLine($"Created topic '{topicName}'.");

        var createdSubscriptions = new List<string>();

        string subscription1 =$"sub-1-{Guid.NewGuid()}";
        await CreateSubscriptionAsync(topicName, subscription1, CreateSubscription1Rule());
        createdSubscriptions.Add(subscription1);

        string subscription2 = $"sub-2-{Guid.NewGuid()}";
        await CreateSubscriptionAsync(topicName, subscription2, CreateSubscription2Rule());
        createdSubscriptions.Add(subscription2);

        string subscription3 = $"sub-3-{Guid.NewGuid()}";
        await CreateSubscriptionAsync(topicName, subscription3, null);
        createdSubscriptions.Add(subscription3);

        string subscription4 = $"sub-4-{Guid.NewGuid()}";
        await CreateSubscriptionAsync(topicName, subscription4, CreateSubscription4Rule());
        createdSubscriptions.Add(subscription4);

        return createdSubscriptions;
    }

    private static async Task DeleteTopicHierarchyAsync(string topicName, IEnumerable<string> subscriptions)
    {
        if (!await _adminClient.TopicExistsAsync(topicName))
        {
            return;
        }

        foreach (string subscription in subscriptions)
        {
            if (await _adminClient.SubscriptionExistsAsync(topicName, subscription))
            {
                await _adminClient.DeleteSubscriptionAsync(topicName, subscription);
                Console.WriteLine($"Deleted subscription '{subscription}'.");
            }
        }

        await _adminClient.DeleteTopicAsync(topicName);
        Console.WriteLine($"Deleted topic '{topicName}'.");
    }

    private static async Task CreateSubscriptionAsync(string topicName, string subscriptionName, CreateRuleOptions? ruleOptions)
    {
        var options = new CreateSubscriptionOptions(topicName, subscriptionName)
        {
            DeadLetteringOnMessageExpiration = false,
            DefaultMessageTimeToLive = TimeSpan.FromHours(1),
            LockDuration = TimeSpan.FromMinutes(1),
            MaxDeliveryCount = 3,
            RequiresSession = false
        };

        if (ruleOptions == null)
        {
            await _adminClient.CreateSubscriptionAsync(options);
        }
        else
        {
            await _adminClient.CreateSubscriptionAsync(options, ruleOptions);
        }

        Console.WriteLine($"Created subscription '{subscriptionName}'.");
    }

    private static CreateRuleOptions CreateSubscription1Rule()
    {
        var correlationFilter = new CorrelationRuleFilter
        {
            ContentType = "application/text",
            //CorrelationId = "id1",
            //Subject = "subject1",
            //MessageId = "msgid1",
            //ReplyTo = "someQueue",
            //ReplyToSessionId = "sessionId",
            //SessionId = "session1",
            //To = "xyz"
        };

        return new CreateRuleOptions("app-prop-filter-1", correlationFilter);
    }

    private static CreateRuleOptions CreateSubscription2Rule()
    {
        var correlationFilter = new CorrelationRuleFilter();
        correlationFilter.ApplicationProperties["prop1"] = "value1";
        return new CreateRuleOptions("user-prop-filter-1", correlationFilter);
    }

    private static CreateRuleOptions CreateSubscription4Rule()
    {
        var sqlFilter = new SqlRuleFilter("sys.MessageId = '123456' AND userProp1 = 'value1'");
        var sqlAction = new SqlRuleAction("SET sys.To = 'Entity'");
        return new CreateRuleOptions("sql-filter-1", sqlFilter)
        {
            Action = sqlAction
        };
    }

}
