// FileTransferSender.cs - Outgoing file transfer handler (send-from-client)

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace NeonCompanion.Runtime.Api.Hermes
{
    /// <summary>
    /// Handles server-requested uploads from the local client to Hermes.
    /// The gateway emits file.transfer.start with direction=from_client; this sender
    /// resolves the source path in the local root jail and streams it back through
    /// file.transfer.start/chunk/finish RPC calls.
    /// </summary>
    public sealed class FileTransferSender : IDisposable
    {
        private readonly HermesGateway _gateway;
        private readonly FilePathRootResolver _rootResolver;
        private bool _disposed;

        public FileTransferSender(HermesGateway gateway, FilePathRootResolver rootResolver)
        {
            _gateway = gateway;
            _rootResolver = rootResolver;
        }

        public void RegisterHandlers()
        {
            _gateway.On(GatewayEvents.FileTransferStart, HandleStart);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _gateway.Off(GatewayEvents.FileTransferStart, HandleStart);
        }

        private void HandleStart(GatewayEvent evt)
        {
            if (evt == null || evt.Payload == null)
                return;

            FileTransferStartPayload start;
            try
            {
                start = evt.Payload.ToObject<FileTransferStartPayload>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[FileTransfer] Invalid outgoing start payload: " + ex.Message);
                return;
            }

            if (start == null || start.direction != FileTransferProtocol.DirectionFromClient)
                return;

            _ = Task.Run(() => SendFileAsync(start));
        }

        private async Task SendFileAsync(FileTransferStartPayload request)
        {
            string transferId = request.transfer_id;
            if (string.IsNullOrEmpty(transferId))
                return;

            try
            {
                if (request.source == null)
                {
                    await SendCompleteAsync(transferId, false, "missing source", 0, null);
                    return;
                }

                if (!FilePathValidator.TryValidateRelativePath(request.source.path, out string pathError))
                {
                    await SendCompleteAsync(transferId, false, pathError, 0, null);
                    return;
                }

                if (!_rootResolver.TryResolveDestination(
                        request.source.root,
                        request.source.path,
                        out string sourcePath,
                        out string rootError))
                {
                    await SendCompleteAsync(transferId, false, rootError, 0, null);
                    return;
                }

                if (!File.Exists(sourcePath))
                {
                    await SendCompleteAsync(transferId, false, "source file not found", 0, null);
                    return;
                }

                string sha256 = ComputeSha256(sourcePath, out long size);
                int chunkSize = request.chunk_size > 0
                    ? Math.Min(request.chunk_size, FileTransferProtocol.DefaultChunkSizeMax)
                    : FileTransferProtocol.DefaultChunkSizeMax;
                chunkSize = Math.Max(1024, chunkSize);

                var startAck = await _gateway.Request<FileTransferAckParams>(
                    RpcMethods.FileTransferStart,
                    new FileTransferStartPayload
                    {
                        transfer_id = transferId,
                        direction = FileTransferProtocol.DirectionFromClient,
                        source = request.source,
                        destination = request.destination,
                        size = size,
                        sha256 = sha256,
                        chunk_size = chunkSize
                    },
                    timeoutMs: 10000);

                long offset = startAck != null ? startAck.next_offset : 0;
                int accepted = startAck != null && startAck.accepted_chunk_size > 0
                    ? startAck.accepted_chunk_size
                    : chunkSize;
                chunkSize = Math.Max(1024, Math.Min(chunkSize, accepted));
                int seq = 0;

                using (var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (offset > 0)
                        stream.Seek(offset, SeekOrigin.Begin);

                    byte[] buffer = new byte[chunkSize];
                    while (offset < size)
                    {
                        int read = await stream.ReadAsync(buffer, 0, buffer.Length);
                        if (read <= 0)
                            break;

                        byte[] data = buffer;
                        if (read != buffer.Length)
                        {
                            data = new byte[read];
                            Buffer.BlockCopy(buffer, 0, data, 0, read);
                        }

                        var chunkAck = await _gateway.Request<FileTransferAckParams>(
                            RpcMethods.FileTransferChunk,
                            new FileTransferChunkPayload
                            {
                                transfer_id = transferId,
                                seq = seq,
                                offset = offset,
                                encoding = FileTransferProtocol.EncodingBase64,
                                data = Convert.ToBase64String(data)
                            },
                            timeoutMs: 10000);

                        long nextOffset = chunkAck != null ? chunkAck.next_offset : offset + read;
                        if (nextOffset != offset + read)
                            throw new InvalidOperationException("server ack offset mismatch");

                        offset = nextOffset;
                        seq++;
                    }
                }

                var complete = await _gateway.Request<FileTransferCompleteParams>(
                    RpcMethods.FileTransferFinish,
                    new FileTransferFinishPayload
                    {
                        transfer_id = transferId,
                        size = size,
                        sha256 = sha256
                    },
                    timeoutMs: 10000);

                if (complete == null || !complete.verified)
                    Debug.LogWarning("[FileTransfer] Server did not verify upload: " + (complete != null ? complete.error : "no response"));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[FileTransfer] Upload failed: " + ex.Message);
                await SendCompleteAsync(transferId, false, ex.Message, 0, null);
            }
        }

        private async Task SendCompleteAsync(string transferId, bool verified, string error, long size, string sha256)
        {
            try
            {
                await _gateway.NotifyAsync(RpcMethods.FileTransferComplete, new FileTransferCompleteParams
                {
                    transfer_id = transferId,
                    verified = verified,
                    error = verified ? null : error,
                    size = size,
                    sha256 = sha256
                });
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[FileTransfer] Upload complete notify failed: " + ex.Message);
            }
        }

        private static string ComputeSha256(string path, out long size)
        {
            size = 0;
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] buffer = new byte[1024 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    size += read;
                    sha.TransformBlock(buffer, 0, read, null, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }
}
