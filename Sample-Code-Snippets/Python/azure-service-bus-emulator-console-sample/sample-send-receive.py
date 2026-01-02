# Install the Azure Service Bus SDK for Python using following command prior to running the sample. Versions >=7.14.0 support Emulator.
# pip install azure-servicebus==7.14.3 

from uuid import uuid4
from azure.servicebus import ServiceBusClient, ServiceBusMessage
from azure.servicebus.management import ServiceBusAdministrationClient
import os
import time
import urllib.request
import urllib.error

EMULATOR_HOST = "localhost"
EMULATOR_HTTP_PORT = 5300
CONNECTION_STR = f"Endpoint=sb://{EMULATOR_HOST};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;"
ADMIN_API_CONNECTION_STR = f"Endpoint=sb://{EMULATOR_HOST}:{EMULATOR_HTTP_PORT};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;"

def wait_for_servicebus_emulator_to_be_healthy():
    health_url = f"http://{EMULATOR_HOST}:{EMULATOR_HTTP_PORT}/health"
    print("Waiting for Service Bus Emulator to be healthy...", flush=True)
    while True:
        try:
            with urllib.request.urlopen(health_url, timeout=3) as resp:
                if resp.status == 200:
                    print("Service Bus Emulator is healthy.", flush=True)
                    return
        except Exception as ex:
            print(f"Service Bus Emulator is not healthy yet. health_url={health_url} Continuing to wait...{ex}", flush=True)
        time.sleep(1)

def update_connection_strings_from_env():
    global CONNECTION_STR, ADMIN_API_CONNECTION_STR
    CONNECTION_STR = f"Endpoint=sb://{EMULATOR_HOST};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;"
    ADMIN_API_CONNECTION_STR = f"Endpoint=sb://{EMULATOR_HOST}:{EMULATOR_HTTP_PORT};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;"
    if EMULATOR_HTTP_PORT == 80:
        ADMIN_API_CONNECTION_STR = CONNECTION_STR
    print(f"Runtime ConnectionString: {CONNECTION_STR}")
    print(f"Admin API ConnectionString: {ADMIN_API_CONNECTION_STR}")

def send_message(sender):
    message = ServiceBusMessage("Hello, Azure Service Bus!")
    sender.send_messages(message)
    print("Message sent:", message)

def receive_messages(receiver):
    with receiver:
        for msg in receiver.receive_messages(max_message_count=1, max_wait_time=5):
            print("Received message:", str(msg))
            receiver.complete_message(msg)

def main():
    global EMULATOR_HOST, EMULATOR_HTTP_PORT
    EMULATOR_HOST = os.getenv("EMULATOR_HOST", "localhost")
    EMULATOR_HTTP_PORT = int(os.getenv("EMULATOR_HTTP_PORT", "5300"))
    
    wait_for_servicebus_emulator_to_be_healthy()
    update_connection_strings_from_env()
    with ServiceBusAdministrationClient.from_connection_string(conn_str=ADMIN_API_CONNECTION_STR) as admin_client:
        # Temporary hack until SDK supports non-TLS and custom-port for management operations
        # Override the base URL to point to the emulator's admin API endpoint
        admin_client._impl._client._base_url = f"http://{EMULATOR_HOST}:{EMULATOR_HTTP_PORT}"

        queue_name = f"queue-{uuid4().hex}"
        try:
            admin_client.create_queue(queue_name)
            print("Queue created:", queue_name)

            with ServiceBusClient.from_connection_string(conn_str=CONNECTION_STR, logging_enable=True) as servicebus_client:
                with servicebus_client.get_queue_sender(queue_name=queue_name) as sender:
                    send_message(sender)

                with servicebus_client.get_queue_receiver(queue_name=queue_name) as receiver:
                    receive_messages(receiver)
        finally:
            try:
                admin_client.delete_queue(queue_name)
                print("Queue deleted:", queue_name)
            except Exception as e:
                print(f"Error deleting queue ({type(e).__name__}):", e)


if __name__ == "__main__":
    main()