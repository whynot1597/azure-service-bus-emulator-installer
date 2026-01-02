package com.contoso.org;

import com.azure.core.http.HttpClient;
import com.azure.core.http.netty.NettyAsyncHttpClientBuilder;
import com.azure.core.http.policy.HttpPipelinePolicy;
import com.azure.messaging.servicebus.ServiceBusClientBuilder;
import com.azure.messaging.servicebus.ServiceBusMessage;
import com.azure.messaging.servicebus.ServiceBusReceiverClient;
import com.azure.messaging.servicebus.ServiceBusSenderClient;
import com.azure.messaging.servicebus.administration.ServiceBusAdministrationClient;
import com.azure.messaging.servicebus.administration.ServiceBusAdministrationClientBuilder;
import com.azure.messaging.servicebus.administration.models.CreateQueueOptions;
import java.net.HttpURLConnection;
import java.net.MalformedURLException;
import java.net.URL;
import java.time.Duration;
import java.util.UUID;
import reactor.core.publisher.Mono;

public class Main {
    private static String emulatorHost = "127.0.0.1";
    private static int emulatorHttpPort = 5300;
    private static String connectionString = "Endpoint=sb://127.0.0.1;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
    private static String adminConnectionString = "Endpoint=sb://127.0.0.1:5300;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    public static void main(String[] args) {
        updateConnectionStringsFromEnv();
        waitForServiceBusEmulatorToBeHealthy();

        String queueName = "queue-" + UUID.randomUUID();

        // Force admin calls over HTTP to the emulator management endpoint on port 5300.
        HttpClient adminHttpClient = new NettyAsyncHttpClientBuilder()
            .port(emulatorHttpPort)
            .build();

        HttpPipelinePolicy forceHttpPolicy = (context, next) -> {
            try {
                URL current = context.getHttpRequest().getUrl();
                URL httpUrl = new URL("http", current.getHost(), emulatorHttpPort, current.getFile());
                context.getHttpRequest().setUrl(httpUrl);
            } catch (MalformedURLException e) {
                return Mono.error(e);
            }
            return next.process();
        };

        ServiceBusAdministrationClient adminClient = new ServiceBusAdministrationClientBuilder()
            .connectionString(adminConnectionString)
            .httpClient(adminHttpClient)
            .addPolicy(forceHttpPolicy)
            .buildClient();

        ServiceBusSenderClient senderClient = null;
        ServiceBusReceiverClient receiverClient = null;
        boolean queueCreated = false;

        try {
            CreateQueueOptions queueOptions = new CreateQueueOptions()
                .setDefaultMessageTimeToLive(Duration.ofMinutes(5));

            adminClient.createQueue(queueName, queueOptions);
            queueCreated = true;
            System.out.println("Created queue: " + queueName);

            ServiceBusClientBuilder builder = new ServiceBusClientBuilder()
                .connectionString(connectionString);

            senderClient = builder.sender().queueName(queueName).buildClient();
            receiverClient = builder.receiver().queueName(queueName).buildClient();

            String message = "Hello, Service Bus!";
            senderClient.sendMessage(new ServiceBusMessage(message));
            System.out.println("Sent : " + message);

            final ServiceBusReceiverClient finalReceiverClient = receiverClient;
            finalReceiverClient.receiveMessages(1).forEach(msg -> {
                System.out.println("Received: " + msg.getBody().toString());
                finalReceiverClient.complete(msg);
            });
        } finally {
            if (senderClient != null) {
                senderClient.close();
            }
            if (receiverClient != null) {
                receiverClient.close();
            }
            if (queueCreated) {
                adminClient.deleteQueue(queueName);
                System.out.println("Deleted queue: " + queueName);
            }
        }
    }

    private static void updateConnectionStringsFromEnv() {
        String host = System.getenv("EMULATOR_HOST");
        if (host != null && !host.isEmpty()) {
            emulatorHost = host;
        }

        String port = System.getenv("EMULATOR_HTTP_PORT");
        if (port != null && !port.isEmpty()) {
            try {
                emulatorHttpPort = Integer.parseInt(port);
            } catch (NumberFormatException ignored) {
                // keep default
            }
        }

        connectionString = String.format("Endpoint=sb://%s;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;", emulatorHost);
        adminConnectionString = String.format("Endpoint=sb://%s:%d;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;", emulatorHost, emulatorHttpPort);

        if (emulatorHttpPort == 80) {
            adminConnectionString = connectionString;
        }

        System.out.println("Runtime ConnectionString: " + connectionString);
        System.out.println("Admin API ConnectionString: " + adminConnectionString);
    }

    private static void waitForServiceBusEmulatorToBeHealthy() {
        String healthUrl = String.format("http://%s:%d/health", emulatorHost, emulatorHttpPort);
        System.out.println("Waiting for Service Bus Emulator to be healthy...");
        while (true) {
            try {
                HttpURLConnection conn = (HttpURLConnection) new URL(healthUrl).openConnection();
                conn.setConnectTimeout(3000);
                conn.setReadTimeout(3000);
                conn.setRequestMethod("GET");
                int code = conn.getResponseCode();
                conn.disconnect();
                if (code == 200) {
                    System.out.println("Service Bus Emulator is healthy.");
                    return;
                }
            } catch (Exception ex) {
                System.out.println("Service Bus Emulator is not healthy yet. health_url=" + healthUrl + " Continuing to wait..." + ex.getMessage());
            }

            try {
                Thread.sleep(1000);
            } catch (InterruptedException ie) {
                Thread.currentThread().interrupt();
                throw new RuntimeException("Interrupted while waiting for emulator health", ie);
            }
        }
    }
}