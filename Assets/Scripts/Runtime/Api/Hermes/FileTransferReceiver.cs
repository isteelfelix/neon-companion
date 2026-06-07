// FileTransferReceiver.cs - Incoming file transfer handler (receive-to-client)

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NeonCompanion.Runtime.Api.Hermes
{
    /// <summary>
    /// Handles server-initiated file transfers to the local client.
    /// Writes to a temp .part file, verifies SHA-256, then atomically renames.
    /// </summary>
    public sealed class FileTransferReceiver : IDisposable
    {
        private readonly HermesGateway _gateway;
        private readonly FilePathRootResolver _rootResolver;
        private readonly object _lock = new object();
        private readonly Dictionary<string, ActiveTransfer> _active = new Dictionary<string, ActiveTransfer>();
        private bool _disposed;

        private sealed class ActiveTransfer
        {
            public string TransferId;
            public string PartPath;
            public string FinalPath;
            public FileStream Stream;
            public SHA256 Hasher;
            public long DeclaredSize;
            public long BytesWritten;
            public string ExpectedSha256;
        }

        public FileTransferReceiver(HermesGateway gateway, FilePathRootResolver rootResolver)
        {
            _gateway = gateway;
            _rootResolver = rootResolver;
        }

        public void RegisterHandlers()
        {
            _gateway.On(GatewayEvents.FileTransferStart, HandleStart);
            _gateway.On(GatewayEvents.FileTransferChunk, HandleChunk);
            _gateway.On(GatewayEvents.FileTransferFinish, HandleFinish);
        }

        public void SetWorkspace(string cwd)
        {
            _rootResolver.SetWorkspace(cwd);
        }

        public void AbortAll()
        {
            lock (_lock)
            {
                foreach (var kv in _active)
                    CleanupTransfer(kv.Value, deletePart: true);
                _active.Clear();
            }
        }

        private void HandleStart(GatewayEvent evt)
        {
            if (evt == null || evt.Payload == null)
                return;

            _ = Task.Run(() => ProcessStartAsync(evt.Payload));
        }

        private void HandleChunk(GatewayEvent evt)
        {
            if (evt == null || evt.Payload == null)
                return;

            _ = Task.Run(() => ProcessChunkAsync(evt.Payload));
        }

        private void HandleFinish(GatewayEvent evt)
        {
            if (evt == null || evt.Payload == null)
                return;

            _ = Task.Run(() => ProcessFinishAsync(evt.Payload));
        }

        private async Task ProcessStartAsync(JToken payload)
        {
            FileTransferStartPayload start;
            try
            {
                start = payload.ToObject<FileTransferStartPayload>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[FileTransfer] Invalid start payload: " + ex.Message);
                return;
            }

            if (start == null || string.IsNullOrEmpty(start.transfer_id))
                return;

            if (start.direction != FileTransferProtocol.DirectionToClient)
            {
                Debug.LogWarning("[FileTransfer] Unsupported direction: " + start.direction);
                return;
            }

            if (start.destination == null)
            {
                await SendCompleteAsync(start.transfer_id, false, "missing destination");
                return;
            }

            if (!FilePathValidator.TryValidateRelativePath(start.destination.path, out string pathError))
            {
                await SendCompleteAsync(start.transfer_id, false, pathError);
                return;
            }

            if (!_rootResolver.TryResolveDestination(
                    start.destination.root,
                    start.destination.path,
                    out string finalPath,
                    out string rootError))
            {
                await SendCompleteAsync(start.transfer_id, false, rootError);
                return;
            }

            string partPath = finalPath + FileTransferProtocol.PartFileSuffix;
            ActiveTransfer transfer = null;

            lock (_lock)
            {
                if (_active.ContainsKey(start.transfer_id))
                {
                    CleanupTransfer(_active[start.transfer_id], deletePart: true);
                    _active.Remove(start.transfer_id);
                }

                try
                {
                    string parentDir = Path.GetDirectoryName(finalPath);
                    if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                        Directory.CreateDirectory(parentDir);

                    if (File.Exists(partPath))
                        File.Delete(partPath);

                    var stream = new FileStream(
                        partPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None);

                    transfer = new ActiveTransfer
                    {
                        TransferId = start.transfer_id,
                        PartPath = partPath,
                        FinalPath = finalPath,
                        Stream = stream,
                        Hasher = SHA256.Create(),
                        DeclaredSize = start.size,
                        BytesWritten = 0,
                        ExpectedSha256 = start.sha256
                    };
                    _active[start.transfer_id] = transfer;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[FileTransfer] Failed to open part file: " + ex.Message);
                    transfer = null;
                }
            }

            if (transfer == null)
            {
                await SendCompleteAsync(start.transfer_id, false, "failed to open destination");
                return;
            }

            try
            {
                await _gateway.NotifyAsync(RpcMethods.FileTransferAck, new FileTransferAckParams
                {
                    transfer_id = start.transfer_id,
                    next_offset = 0
                });
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[FileTransfer] Start ack failed: " + ex.Message);
                lock (_lock)
                {
                    if (_active.TryGetValue(start.transfer_id, out var active))
                    {
                        CleanupTransfer(active, deletePart: true);
                        _active.Remove(start.transfer_id);
                    }
                }
            }
        }

        private async Task ProcessChunkAsync(JToken payload)
        {
            FileTransferChunkPayload chunk;
            try
            {
                chunk = payload.ToObject<FileTransferChunkPayload>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[FileTransfer] Invalid chunk payload: " + ex.Message);
                return;
            }

            if (chunk == null || string.IsNullOrEmpty(chunk.transfer_id))
                return;

            ActiveTransfer transfer;
            lock (_lock)
            {
                if (!_active.TryGetValue(chunk.transfer_id, out transfer))
                    return;
            }

            string encoding = chunk.encoding ?? FileTransferProtocol.EncodingBase64;
            if (encoding != FileTransferProtocol.EncodingBase64)
            {
                await AbortTransferAsync(transfer, "unsupported encoding: " + encoding);
                return;
            }

            byte[] data;
            try
            {
                data = Convert.FromBase64String(chunk.data ?? string.Empty);
            }
            catch (Exception ex)
            {
                await AbortTransferAsync(transfer, "invalid base64: " + ex.Message);
                return;
            }

            if (chunk.offset != transfer.BytesWritten)
            {
                await AbortTransferAsync(transfer, "out-of-order chunk at offset " + chunk.offset);
                return;
            }

            if (transfer.DeclaredSize > 0 && transfer.BytesWritten + data.Length > transfer.DeclaredSize)
            {
                await AbortTransferAsync(transfer, "chunk exceeds declared size");
                return;
            }

            try
            {
                transfer.Stream.Write(data, 0, data.Length);
                transfer.Stream.Flush();
                transfer.Hasher.TransformBlock(data, 0, data.Length, null, 0);
                transfer.BytesWritten += data.Length;
            }
            catch (Exception ex)
            {
                await AbortTransferAsync(transfer, "write failed: " + ex.Message);
                return;
            }

            try
            {
                await _gateway.NotifyAsync(RpcMethods.FileTransferAck, new FileTransferAckParams
                {
                    transfer_id = chunk.transfer_id,
                    next_offset = transfer.BytesWritten
                });
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[FileTransfer] Chunk ack failed: " + ex.Message);
                await AbortTransferAsync(transfer, "ack failed");
            }
        }

        private async Task ProcessFinishAsync(JToken payload)
        {
            FileTransferFinishPayload finish;
            try
            {
                finish = payload.ToObject<FileTransferFinishPayload>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[FileTransfer] Invalid finish payload: " + ex.Message);
                return;
            }

            if (finish == null || string.IsNullOrEmpty(finish.transfer_id))
                return;

            ActiveTransfer transfer;
            lock (_lock)
            {
                if (!_active.TryGetValue(finish.transfer_id, out transfer))
                    return;
                _active.Remove(finish.transfer_id);
            }

            bool verified = false;
            string error = null;

            try
            {
                transfer.Stream.Flush();
                transfer.Hasher.TransformFinalBlock(new byte[0], 0, 0);
                string actualHash = BitConverter.ToString(transfer.Hasher.Hash)
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();

                long expectedSize = finish.size > 0 ? finish.size : transfer.DeclaredSize;
                if (expectedSize > 0 && transfer.BytesWritten != expectedSize)
                    error = "size mismatch";
                else if (!string.IsNullOrEmpty(finish.sha256) &&
                         !string.Equals(finish.sha256, actualHash, StringComparison.OrdinalIgnoreCase))
                    error = "sha256 mismatch";
                else if (!string.IsNullOrEmpty(transfer.ExpectedSha256) &&
                         !string.Equals(transfer.ExpectedSha256, actualHash, StringComparison.OrdinalIgnoreCase))
                    error = "sha256 mismatch";
                else
                    verified = true;

                transfer.Stream.Dispose();
                transfer.Stream = null;
                transfer.Hasher.Dispose();
                transfer.Hasher = null;

                if (verified)
                {
                    if (File.Exists(transfer.FinalPath))
                        File.Delete(transfer.FinalPath);

                    File.Move(transfer.PartPath, transfer.FinalPath);
                }
                else
                {
                    TryDeleteFile(transfer.PartPath);
                }
            }
            catch (Exception ex)
            {
                error = "finalize failed: " + ex.Message;
                CleanupTransfer(transfer, deletePart: true);
            }

            await SendCompleteAsync(finish.transfer_id, verified, error);
        }

        private async Task AbortTransferAsync(ActiveTransfer transfer, string reason)
        {
            if (transfer == null)
                return;

            lock (_lock)
            {
                if (_active.ContainsKey(transfer.TransferId))
                    _active.Remove(transfer.TransferId);
            }

            CleanupTransfer(transfer, deletePart: true);
            await SendCompleteAsync(transfer.TransferId, false, reason);
        }

        private async Task SendCompleteAsync(string transferId, bool verified, string error)
        {
            try
            {
                await _gateway.NotifyAsync(RpcMethods.FileTransferComplete, new FileTransferCompleteParams
                {
                    transfer_id = transferId,
                    verified = verified,
                    error = verified ? null : error
                });
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[FileTransfer] Complete notify failed: " + ex.Message);
            }
        }

        private static void CleanupTransfer(ActiveTransfer transfer, bool deletePart)
        {
            if (transfer == null)
                return;

            try
            {
                if (transfer.Stream != null)
                {
                    transfer.Stream.Dispose();
                    transfer.Stream = null;
                }
            }
            catch { }

            try
            {
                if (transfer.Hasher != null)
                {
                    transfer.Hasher.Dispose();
                    transfer.Hasher = null;
                }
            }
            catch { }

            if (deletePart)
                TryDeleteFile(transfer.PartPath);
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _gateway.Off(GatewayEvents.FileTransferStart, HandleStart);
            _gateway.Off(GatewayEvents.FileTransferChunk, HandleChunk);
            _gateway.Off(GatewayEvents.FileTransferFinish, HandleFinish);
            AbortAll();
        }
    }
}