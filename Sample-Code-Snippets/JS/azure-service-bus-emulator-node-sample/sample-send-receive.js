import { ServiceBusClient, ServiceBusAdministrationClient } from "@azure/service-bus";
import { randomUUID } from "crypto";

// Configuration
let EMULATOR_HOST = "localhost";
let EMULATOR_HTTP_PORT = 5300;
let connectionString = `Endpoint=sb://${EMULATOR_HOST};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;`;
let adminApiConnectionString = `Endpoint=sb://${EMULATOR_HOST}:${EMULATOR_HTTP_PORT};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;`;
const queueName = `queue-${randomUUID()}`;

function updateConnectionStringsFromEnv() {
    EMULATOR_HOST = process.env.EMULATOR_HOST || "localhost";
    EMULATOR_HTTP_PORT = Number(process.env.EMULATOR_HTTP_PORT || "5300");
    connectionString = `Endpoint=sb://${EMULATOR_HOST};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;`;
    adminApiConnectionString = `Endpoint=sb://${EMULATOR_HOST}:${EMULATOR_HTTP_PORT};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;`;
    if (EMULATOR_HTTP_PORT === 80) {
        adminApiConnectionString = connectionString;
    }
    console.log(`Runtime ConnectionString: ${connectionString}`);
    console.log(`Admin API ConnectionString: ${adminApiConnectionString}`);
}

async function waitForServiceBusEmulatorToBeHealthy() {
    const healthUrl = `http://${EMULATOR_HOST}:${EMULATOR_HTTP_PORT}/health`;
    console.log("Waiting for Service Bus Emulator to be healthy...");
    while (true) {
        try {
            const response = await fetch(healthUrl, { method: "GET" });
            if (response.ok) {
                console.log("Service Bus Emulator is healthy.");
                return;
            }
        } catch (err) {
            console.log(`Service Bus Emulator is not healthy yet. health_url=${healthUrl} Continuing to wait...${err}`);
        }
        await new Promise((resolve) => setTimeout(resolve, 1000));
    }
}

async function main() {
    console.log(`--- Starting Run: ${queueName} ---`);

    updateConnectionStringsFromEnv();
    await waitForServiceBusEmulatorToBeHealthy();

    // 1. Initialize the Administration Client
    const adminClient = new ServiceBusAdministrationClient(adminApiConnectionString);

    // Temporary hacks until SDK supports non-TLS and custom-port from connection-string for management operations
    const internalClient = adminClient["_httpClient"]; 
    const originalSendRequest = internalClient.sendRequest.bind(internalClient);
    internalClient.sendRequest = async function (request) {
        request.allowInsecureConnection = true; // Bypass TLS for emulator
        return originalSendRequest(request);
    };
    adminClient.getUrl = function (path, queryParams) {
        const baseUri = `http://${EMULATOR_HOST}:${EMULATOR_HTTP_PORT}/${path}`; // Override to custom admin-api port
        const requestUrl = new URL(baseUri);
        requestUrl.searchParams.set("api-version", this.serviceVersion || "2021-05");
        if (queryParams) {
            for (const [key, value] of Object.entries(queryParams)) {
                requestUrl.searchParams.set(key, value);
            }
        }
        return requestUrl.toString();
    };

    try {
        // Step 1: Create Queue
        console.log(`[Admin] Creating queue: ${queueName}...`);
        await adminClient.createQueue(queueName);
        console.log(`[Admin] Success: Queue created.`);

        // Step 2: Send/Receive (AMQP)
        const sbClient = new ServiceBusClient(connectionString);
        const sender = sbClient.createSender(queueName);
        const receiver = sbClient.createReceiver(queueName);

        console.log(`[Sender] Sending test message...`);
        await sender.sendMessages({ body: "Hello World!" });

        console.log(`[Receiver] Receiving...`);
        const messages = await receiver.receiveMessages(1, { maxWaitTimeInMs: 3000 });
        for (const msg of messages) {
            console.log(`[Receiver] Data:`, msg.body);
            await receiver.completeMessage(msg);
        }

        await sender.close();
        await receiver.close();
        await sbClient.close();

        // Step 3: Delete Queue
        console.log(`[Admin] Deleting queue: ${queueName}...`);
        await adminClient.deleteQueue(queueName);
        console.log(`[Admin] Cleanup complete.`);

    } catch (err) {
        console.error(`\n[Run Failed]`);
        console.error(`Message: ${err.message}`);
    }
}

main();