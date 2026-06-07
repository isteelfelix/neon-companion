// HermesClientBridge.cs - Client capability registration and tracer-bullet ping/pong

using System;
using System.Threading.Tasks;
using NeonCompanion.Runtime.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NeonCompanion.Runtime.Api.Hermes
{
    [Serializable]
    public class ClientPingPayload
    {
        public string request_id;
        public string operation_id;
    }

    [Serializable]
    public class ClientPongPayload
    {
        public string request_id;
        public bool ok = true;
        public long timestamp_ms;
    }

    /// <summary>
    /// Wires client.register after gateway.ready and responds to client.ping off the UI path.
    /// </summary>
    public sealed class HermesClientBridge : IDisposable
    {
        private readonly HermesGateway _gateway;
        private readonly FileTransferReceiver _fileTransferReceiver;
        private readonly FileTransferSender _fileTransferSender;
        private bool _disposed;
        private bool _registered;

        public HermesClientBridge(HermesGateway gateway, FileTransferReceiver fileTransferReceiver, FileTransferSender fileTransferSender)
        {
            _gateway = gateway;
            _fileTransferReceiver = fileTransferReceiver;
            _fileTransferSender = fileTransferSender;
            RegisterHandlers();
        }

        private void RegisterHandlers()
        {
            _gateway.On(GatewayEvents.GatewayReady, HandleGatewayReady);
            _gateway.On(GatewayEvents.ClientPing, HandleClientPing);
            _gateway.OnStateChange(HandleGatewayStateChange);
            _fileTransferReceiver.RegisterHandlers();
            _fileTransferSender.RegisterHandlers();
        }

        private void HandleGatewayStateChange(ConnectionState state)
        {
            if (state == ConnectionState.Closed || state == ConnectionState.Error)
                _registered = false;
        }

        public void SetWorkspace(string cwd)
        {
            _fileTransferReceiver.SetWorkspace(cwd);
        }

        private void HandleGatewayReady(GatewayEvent evt)
        {
            // BuildRegisterParams uses Unity APIs (PlayerPrefs/Application.platform),
            // so registration must start on the Unity main thread. HermesGateway
            // dispatches events on the captured SynchronizationContext.
            _ = RegisterClientAsync();
        }

        private async Task RegisterClientAsync()
        {
            if (_registered)
                return;

            try
            {
                var parameters = HermesClientIdentity.BuildRegisterParams();
                var result = await _gateway.Request<ClientRegisterResult>(
                    RpcMethods.ClientRegister,
                    parameters,
                    timeoutMs: 5000);

                _registered = true;
                if (result != null && result.accepted)
                    NeonLogger.Log("[Hermes] client.register accepted (protocol v" + result.protocol_version + ")");
                else
                    Debug.LogWarning("[Hermes] client.register returned without acceptance");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Hermes] client.register not supported or failed: " + ex.Message);
            }
        }

        private void HandleClientPing(GatewayEvent evt)
        {
            if (evt == null || evt.Payload == null)
                return;

            _ = Task.Run(() => RespondToPingAsync(evt.Payload));
        }

        private async Task RespondToPingAsync(JToken payload)
        {
            ClientPingPayload ping;
            try
            {
                ping = payload.ToObject<ClientPingPayload>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Hermes] Invalid client.ping payload: " + ex.Message);
                return;
            }

            string requestId = ping != null ? ping.request_id : null;
            if (string.IsNullOrEmpty(requestId) && ping != null)
                requestId = ping.operation_id;

            if (string.IsNullOrEmpty(requestId))
            {
                Debug.LogWarning("[Hermes] client.ping missing request_id");
                return;
            }

            try
            {
                await _gateway.NotifyAsync(RpcMethods.ClientPong, new ClientPongPayload
                {
                    request_id = requestId,
                    ok = true,
                    timestamp_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Hermes] client.pong failed: " + ex.Message);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _fileTransferReceiver?.Dispose();
            _fileTransferSender?.Dispose();
        }
    }
}