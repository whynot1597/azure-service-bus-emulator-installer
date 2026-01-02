package main

import (
	"context"
	"fmt"
	"log"
	"net/http"
	"os"
	"strconv"
	"time"

	"github.com/Azure/azure-sdk-for-go/sdk/azcore"
	"github.com/Azure/azure-sdk-for-go/sdk/messaging/azservicebus"
	"github.com/Azure/azure-sdk-for-go/sdk/messaging/azservicebus/admin"
)

var (
	// Defaults; overridden by environment variables.
	emulatorHost              = "localhost"
	emulatorHTTPPort      int = 5300
	connectionString          = "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;"
	adminConnectionString     = "Endpoint=sb://localhost:5300;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;"
)

func main() {
	updateConnectionStringsFromEnv()
	waitForServiceBusEmulatorToBeHealthy()

	queueName := fmt.Sprintf("queue-%d", time.Now().UnixNano())

	adminClient, err := newAdminClient(adminConnectionString)
	if err != nil {
		log.Fatalf("failed to create admin client: %s", err)
	}

	ctx := context.Background()

	if _, err = adminClient.CreateQueue(ctx, queueName, nil); err != nil {
		log.Fatalf("failed to create queue: %s", err)
	}
	log.Printf("created queue: %s", queueName)

	// Ensure cleanup even if send/receive fails.
	defer func() {
		if _, err := adminClient.DeleteQueue(context.Background(), queueName, nil); err != nil {
			log.Printf("warning: failed to delete queue %s: %s", queueName, err)
			return
		}
		log.Printf("deleted queue: %s", queueName)
	}()

	client, err := azservicebus.NewClientFromConnectionString(connectionString, nil)
	if err != nil {
		log.Fatalf("failed to create messaging client: %s", err)
	}

	sendMessage(client, queueName, "Hello, Azure Service Bus!")
	receiveMessages(client, queueName)
}

// updateConnectionStringsFromEnv reads host/port from EMULATOR_HOST and EMULATOR_HTTP_PORT and rebuilds connection strings.
func updateConnectionStringsFromEnv() {
	if h := os.Getenv("EMULATOR_HOST"); h != "" {
		emulatorHost = h
	}
	if p := os.Getenv("EMULATOR_HTTP_PORT"); p != "" {
		if val, err := strconv.Atoi(p); err == nil {
			emulatorHTTPPort = val
		}
	}

	connectionString = fmt.Sprintf("Endpoint=sb://%s;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;", emulatorHost)
	adminConnectionString = fmt.Sprintf("Endpoint=sb://%s:%d;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;", emulatorHost, emulatorHTTPPort)

	if emulatorHTTPPort == 80 {
		adminConnectionString = connectionString
	}

	fmt.Printf("Runtime ConnectionString: %s\n", connectionString)
	fmt.Printf("Admin API ConnectionString: %s\n", adminConnectionString)
}

// waitForServiceBusEmulatorToBeHealthy polls the emulator health endpoint until it returns 200.
func waitForServiceBusEmulatorToBeHealthy() {
	healthURL := fmt.Sprintf("http://%s:%d/health", emulatorHost, emulatorHTTPPort)
	fmt.Println("Waiting for Service Bus Emulator to be healthy...")
	client := http.Client{Timeout: 3 * time.Second}
	for {
		resp, err := client.Get(healthURL)
		if err == nil && resp.StatusCode == http.StatusOK {
			resp.Body.Close()
			fmt.Println("Service Bus Emulator is healthy.")
			return
		}
		if resp != nil && resp.Body != nil {
			resp.Body.Close()
		}
		fmt.Printf("Service Bus Emulator is not healthy yet. health_url=%s Continuing to wait...%v\n", healthURL, err)
		time.Sleep(1 * time.Second)
	}
}

func sendMessage(client *azservicebus.Client, queueName, message string) {
	sender, err := client.NewSender(queueName, nil)
	if err != nil {
		log.Fatalf("failed to create sender: %s", err)
	}
	defer sender.Close(context.Background())

	msg := &azservicebus.Message{Body: []byte(message)}

	if err := sender.SendMessage(context.Background(), msg, nil); err != nil {
		log.Fatalf("failed to send message: %s", err)
	}

	fmt.Println("Message sent:", message)
}

func receiveMessages(client *azservicebus.Client, queueName string) {
	receiver, err := client.NewReceiverForQueue(queueName, nil)
	if err != nil {
		log.Fatalf("failed to create receiver: %s", err)
	}
	defer receiver.Close(context.Background())

	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()

	messages, err := receiver.ReceiveMessages(ctx, 1, nil)
	if err != nil {
		log.Fatalf("failed to receive messages: %s", err)
	}

	for _, msg := range messages {
		fmt.Println("Received message:", string(msg.Body))
		if err := receiver.CompleteMessage(ctx, msg, nil); err != nil {
			log.Printf("warning: failed to complete message %s: %v", msg.MessageID, err)
		}
	}
}

// Temporary hack to force HTTP (no TLS) for management operation until SDK supports it natively.
func newAdminClient(connStr string) (*admin.Client, error) {
	return admin.NewClientFromConnectionString(connStr, &admin.ClientOptions{
		ClientOptions: azcore.ClientOptions{Transport: &httpOnlyTransport{base: http.DefaultTransport}},
	})
}

// httpOnlyTransport forces HTTP (no TLS) while preserving host/port from the request.
type httpOnlyTransport struct {
	base http.RoundTripper
}

func (t *httpOnlyTransport) Do(req *http.Request) (*http.Response, error) {
	clone := req.Clone(req.Context())
	urlCopy := *clone.URL
	urlCopy.Scheme = "http"
	clone.URL = &urlCopy
	return t.base.RoundTrip(clone)
}
